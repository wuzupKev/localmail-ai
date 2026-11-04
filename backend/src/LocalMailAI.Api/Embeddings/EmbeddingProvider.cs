namespace LocalMailAI.Api.Embeddings;

/// <summary>
/// Owns the embedder: loads it once at startup and reports whether it is available.
///
/// The model is 470 MB and is not in git, so its absence is a normal, expected state
/// rather than a broken installation. But degrading silently is not acceptable: when
/// it is missing, vector search and the trained classifier are lost, and the user is
/// entitled to know why their product does less than it promises. That is why
/// <see cref="Available"/> and <see cref="MissingReason"/> are exposed through
/// /api/model/status.
/// </summary>
public sealed class EmbeddingProvider : IDisposable
{
    private readonly E5Embedder? _embedder;

    public EmbeddingProvider(IConfiguration config, ILogger<EmbeddingProvider> log, ILogger<E5Embedder> embedderLog)
    {
        ModelPath = ResolveModelPath(config);

        if (ModelPath is null)
        {
            MissingReason = "cannot find the models/multilingual-e5-small directory";
            log.LogWarning("Embeddings unavailable: {Reason}", MissingReason);
            return;
        }

        _embedder = E5Embedder.TryLoad(ModelPath, embedderLog);

        if (_embedder is null)
        {
            MissingReason = File.Exists(Path.Combine(ModelPath, "onnx", "model.onnx"))
                ? "the model is on disk but could not be loaded"
                : "onnx/model.onnx is missing (470 MB, not in git)";
        }
    }

    /// <summary>Model directory, or null if it was not found.</summary>
    public string? ModelPath { get; }

    /// <summary>Why it is unavailable. Null when it is available.</summary>
    public string? MissingReason { get; }

    public bool Available => _embedder is not null;

    /// <summary>
    /// The embedder, or null. Callers must handle the null: there is no throwing
    /// variant, because that would turn a planned degradation into a failure.
    /// </summary>
    public E5Embedder? Embedder => _embedder;

    private static string? ResolveModelPath(IConfiguration config)
    {
        var configured = config["Models:Path"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Directory.Exists(configured) ? Path.GetFullPath(configured) : null;
        }

        // Walk up from the executable, same as the parity tools.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "models", "multilingual-e5-small");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        return null;
    }

    public void Dispose() => _embedder?.Dispose();
}
