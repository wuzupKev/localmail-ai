using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tokenizers.DotNet;

namespace LocalMailAI.Api.Embeddings;

/// <summary>
/// Embeddings con multilingual-e5-small en ONNX Runtime (BACKEND.md §2 y §5, etapa 5).
///
/// Dos cosas que esta clase impone a proposito, porque las dos fallan en silencio:
///
/// 1. **Los prefijos de e5.** No hay un metodo <c>Embed(texto)</c>: hay
///    <see cref="EmbedPassage"/> y <see cref="EmbedQuery"/>. e5 exige "passage: " para
///    los documentos y "query: " para las consultas, y olvidarlo degrada los resultados
///    sin dar ningun error (§8 lo llama "el error mas comun con estos modelos"). Si la
///    unica forma de llamar al embedder es eligiendo cual de los dos, no se puede
///    olvidar.
/// 2. **El pooling con mascara.** Vive en <see cref="Pooling"/>, probado aparte.
///
/// La tokenizacion usa el mismo tokenizer.json que verifico el hito 0, asi que los
/// identificadores coinciden con la referencia de Python. No se vuelve a discutir.
/// </summary>
public sealed class E5Embedder : IDisposable
{
    public const int Dim = 384;
    public const string ModelName = "multilingual-e5-small (ONNX, 118M)";

    /// <summary>§5: lotes de 32.</summary>
    public const int BatchSize = 32;

    private readonly Tokenizer _tokenizer;
    private readonly InferenceSession _sesion;
    private readonly bool _necesitaTokenTypeIds;
    private readonly ILogger<E5Embedder> _log;

    private E5Embedder(Tokenizer tokenizer, InferenceSession sesion, ILogger<E5Embedder> log)
    {
        _tokenizer = tokenizer;
        _sesion = sesion;
        _log = log;

        var entradas = _sesion.InputMetadata.Keys.ToList();
        _necesitaTokenTypeIds = entradas.Contains("token_type_ids");

        log.LogInformation("Embedder listo: entradas [{Entradas}], dimension {Dim}",
            string.Join(", ", entradas), Dim);
    }

    /// <summary>
    /// Carga el modelo si esta en disco. Devuelve null si falta, en vez de reventar:
    /// el producto tiene que arrancar igual y degradar, como con Ollama (§7). Sin
    /// embeddings se pierden la busqueda vectorial y el clasificador entrenado, pero
    /// el triage por senales y la busqueda BM25 siguen funcionando.
    /// </summary>
    public static E5Embedder? TryLoad(string directorioModelo, ILogger<E5Embedder> log)
    {
        var tokenizerJson = Path.Combine(directorioModelo, "tokenizer.json");
        var modeloOnnx = Path.Combine(directorioModelo, "onnx", "model.onnx");

        if (!File.Exists(tokenizerJson) || !File.Exists(modeloOnnx))
        {
            log.LogWarning(
                "Sin embeddings: falta {Que}. El triage por senales y la busqueda BM25 siguen funcionando.",
                !File.Exists(modeloOnnx) ? modeloOnnx : tokenizerJson);
            return null;
        }

        try
        {
            // Cualificado: ASP.NET Core tiene su propio SessionOptions y entra por los
            // using implicitos.
            var opciones = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                // Un proceso local en el portatil de alguien: no se puede acaparar la
                // maquina mientras el usuario trabaja.
                IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };

            return new E5Embedder(new Tokenizer(tokenizerJson), new InferenceSession(modeloOnnx, opciones), log);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "No se ha podido cargar el modelo de embeddings; se sigue sin el");
            return null;
        }
    }

    /// <summary>Embedding de un documento. Anade el prefijo "passage: " que exige e5.</summary>
    public float[] EmbedPassage(string texto) => Embed([Prefijar("passage: ", texto)])[0];

    /// <summary>Embedding de una consulta. Anade el prefijo "query: " que exige e5.</summary>
    public float[] EmbedQuery(string texto) => Embed([Prefijar("query: ", texto)])[0];

    /// <summary>Varios documentos de una vez. El lote es donde la mascara importa.</summary>
    public float[][] EmbedPassages(IReadOnlyList<string> textos)
    {
        ArgumentNullException.ThrowIfNull(textos);

        var resultado = new float[textos.Count][];

        for (var desde = 0; desde < textos.Count; desde += BatchSize)
        {
            var lote = textos.Skip(desde).Take(BatchSize)
                .Select(t => Prefijar("passage: ", t))
                .ToList();

            var vectores = Embed(lote);
            for (var i = 0; i < vectores.Length; i++)
            {
                resultado[desde + i] = vectores[i];
            }
        }

        return resultado;
    }

    private static string Prefijar(string prefijo, string texto)
    {
        var limpio = (texto ?? string.Empty).Trim();

        // Un cuerpo vacio no se puede embeber de forma util, pero tampoco debe reventar
        // la ingesta: el prefijo solo ya da un vector valido y consistente.
        return limpio.Length == 0 ? prefijo.TrimEnd() : prefijo + limpio;
    }

    private float[][] Embed(IReadOnlyList<string> textosConPrefijo)
    {
        var secuencias = textosConPrefijo
            .Select(t => Pooling.Truncate(_tokenizer.Encode(t) ?? []))
            .ToList();

        var largo = secuencias.Max(s => s.Length);
        var n = secuencias.Count;

        var ids = new DenseTensor<long>([n, largo]);
        var mascara = new DenseTensor<long>([n, largo]);
        var tipos = _necesitaTokenTypeIds ? new DenseTensor<long>([n, largo]) : null;

        for (var f = 0; f < n; f++)
        {
            var secuencia = secuencias[f];

            for (var t = 0; t < largo; t++)
            {
                var esReal = t < secuencia.Length;
                ids[f, t] = esReal ? secuencia[t] : Pooling.PadId;
                mascara[f, t] = esReal ? 1 : 0;

                if (tipos is not null)
                {
                    // XLM-RoBERTa no usa pares de frases: todo es segmento 0.
                    tipos[f, t] = 0;
                }
            }
        }

        var entradas = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", ids),
            NamedOnnxValue.CreateFromTensor("attention_mask", mascara),
        };

        if (tipos is not null)
        {
            entradas.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tipos));
        }

        using var salida = _sesion.Run(entradas);
        var ultimaCapa = salida.First().AsTensor<float>();

        if (ultimaCapa.Dimensions[2] != Dim)
        {
            throw new InvalidOperationException(
                $"el modelo devuelve vectores de {ultimaCapa.Dimensions[2]} y se esperaban {Dim}");
        }

        var vectores = new float[n][];
        var plano = ultimaCapa.ToArray();

        for (var f = 0; f < n; f++)
        {
            var desde = f * largo * Dim;
            var filaMascara = new long[largo];
            for (var t = 0; t < largo; t++)
            {
                filaMascara[t] = mascara[f, t];
            }

            vectores[f] = Pooling.Normalize(
                Pooling.MeanPool(plano.AsSpan(desde, largo * Dim), filaMascara, Dim));
        }

        return vectores;
    }

    public void Dispose()
    {
        _sesion.Dispose();
        _tokenizer.Dispose();
    }
}
