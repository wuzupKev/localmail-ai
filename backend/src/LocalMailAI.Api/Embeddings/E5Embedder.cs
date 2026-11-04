using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tokenizers.DotNet;

namespace LocalMailAI.Api.Embeddings;

/// <summary>
/// Embeddings with multilingual-e5-small on ONNX Runtime (BACKEND.md §2 and §5, stage 5).
///
/// Two things this class enforces deliberately, because both fail silently:
///
/// 1. **The e5 prefixes.** There is no <c>Embed(text)</c> method: there is
///    <see cref="EmbedPassage"/> and <see cref="EmbedQuery"/>. e5 requires "passage: "
///    for documents and "query: " for queries, and omitting it degrades results with
///    no error at all (§8 calls it "the most common mistake with these models"). If
///    the only way to call the embedder is to choose one of the two, it cannot be
///    forgotten.
/// 2. **Mask-weighted pooling.** It lives in <see cref="Pooling"/>, tested separately.
///
/// Tokenisation uses the same tokenizer.json that milestone 0 verified, so the token
/// ids match the Python reference. That is settled and not revisited.
/// </summary>
public sealed class E5Embedder : IDisposable
{
    public const int Dim = 384;
    public const string ModelName = "multilingual-e5-small (ONNX, 118M)";

    /// <summary>§5: batches of 32.</summary>
    public const int BatchSize = 32;

    private readonly Tokenizer _tokenizer;
    private readonly InferenceSession _session;
    private readonly bool _needsTokenTypeIds;
    private readonly ILogger<E5Embedder> _log;

    private E5Embedder(Tokenizer tokenizer, InferenceSession session, ILogger<E5Embedder> log)
    {
        _tokenizer = tokenizer;
        _session = session;
        _log = log;

        var inputs = _session.InputMetadata.Keys.ToList();
        _needsTokenTypeIds = inputs.Contains("token_type_ids");

        log.LogInformation("Embedder ready: inputs [{Inputs}], dimension {Dim}",
            string.Join(", ", inputs), Dim);
    }

    /// <summary>
    /// Loads the model if it is on disk. Returns null when it is missing instead of
    /// throwing: the product must still start and degrade, as it does with Ollama (§7).
    /// Without embeddings there is no vector search and no trained classifier, but
    /// signal triage and BM25 search keep working.
    /// </summary>
    public static E5Embedder? TryLoad(string modelDirectory, ILogger<E5Embedder> log)
    {
        var tokenizerJson = Path.Combine(modelDirectory, "tokenizer.json");
        var onnxModel = Path.Combine(modelDirectory, "onnx", "model.onnx");

        if (!File.Exists(tokenizerJson) || !File.Exists(onnxModel))
        {
            log.LogWarning(
                "No embeddings: {What} is missing. Signal triage and BM25 search keep working.",
                !File.Exists(onnxModel) ? onnxModel : tokenizerJson);
            return null;
        }

        try
        {
            // Fully qualified: ASP.NET Core has its own SessionOptions and it comes in
            // through the implicit usings.
            var options = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                // A local process on someone's laptop: it must not take over the
                // machine while the user is working.
                IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };

            return new E5Embedder(new Tokenizer(tokenizerJson), new InferenceSession(onnxModel, options), log);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not load the embedding model; continuing without it");
            return null;
        }
    }

    /// <summary>Embeds a document. Adds the "passage: " prefix that e5 requires.</summary>
    public float[] EmbedPassage(string text) => Embed([Prefixed("passage: ", text)])[0];

    /// <summary>Embeds a query. Adds the "query: " prefix that e5 requires.</summary>
    public float[] EmbedQuery(string text) => Embed([Prefixed("query: ", text)])[0];

    /// <summary>Several documents at once. Batching is where the mask matters.</summary>
    public float[][] EmbedPassages(IReadOnlyList<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var result = new float[texts.Count][];

        for (var from = 0; from < texts.Count; from += BatchSize)
        {
            var batch = texts.Skip(from).Take(BatchSize)
                .Select(t => Prefixed("passage: ", t))
                .ToList();

            var vectors = Embed(batch);
            for (var i = 0; i < vectors.Length; i++)
            {
                result[from + i] = vectors[i];
            }
        }

        return result;
    }

    private static string Prefixed(string prefix, string text)
    {
        var clean = (text ?? string.Empty).Trim();

        // An empty body cannot be embedded usefully, but it must not break ingestion
        // either: the prefix alone still yields a valid, consistent vector.
        return clean.Length == 0 ? prefix.TrimEnd() : prefix + clean;
    }

    private float[][] Embed(IReadOnlyList<string> prefixedTexts)
    {
        var sequences = prefixedTexts
            .Select(t => Pooling.Truncate(_tokenizer.Encode(t) ?? []))
            .ToList();

        var width = sequences.Max(s => s.Length);
        var n = sequences.Count;

        var ids = new DenseTensor<long>([n, width]);
        var mask = new DenseTensor<long>([n, width]);
        var types = _needsTokenTypeIds ? new DenseTensor<long>([n, width]) : null;

        for (var row = 0; row < n; row++)
        {
            var sequence = sequences[row];

            for (var t = 0; t < width; t++)
            {
                var isReal = t < sequence.Length;
                ids[row, t] = isReal ? sequence[t] : Pooling.PadId;
                mask[row, t] = isReal ? 1 : 0;

                if (types is not null)
                {
                    // XLM-RoBERTa does not use sentence pairs: everything is segment 0.
                    types[row, t] = 0;
                }
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", ids),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        };

        if (types is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", types));
        }

        using var output = _session.Run(inputs);
        var lastHiddenState = output.First().AsTensor<float>();

        if (lastHiddenState.Dimensions[2] != Dim)
        {
            throw new InvalidOperationException(
                $"the model returns vectors of {lastHiddenState.Dimensions[2]} but {Dim} was expected");
        }

        var vectors = new float[n][];
        var flat = lastHiddenState.ToArray();

        for (var row = 0; row < n; row++)
        {
            var from = row * width * Dim;
            var rowMask = new long[width];
            for (var t = 0; t < width; t++)
            {
                rowMask[t] = mask[row, t];
            }

            vectors[row] = Pooling.Normalize(
                Pooling.MeanPool(flat.AsSpan(from, width * Dim), rowMask, Dim));
        }

        return vectors;
    }

    public void Dispose()
    {
        _session.Dispose();
        _tokenizer.Dispose();
    }
}
