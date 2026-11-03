namespace LocalMailAI.Api.Embeddings;

/// <summary>
/// Dueno del embedder: lo carga una vez al arrancar y dice si esta disponible.
///
/// El modelo son 470 MB que no van en git, asi que su ausencia es un estado normal y
/// esperable -no un error de instalacion-. Pero degradar en silencio no vale: si falta,
/// se pierden la busqueda vectorial y el clasificador entrenado, y el usuario tiene
/// derecho a saber por que su producto hace menos de lo que promete. Por eso
/// <see cref="Available"/> y <see cref="MissingReason"/> salen por /api/model/status.
/// </summary>
public sealed class EmbeddingProvider : IDisposable
{
    private readonly E5Embedder? _embedder;

    public EmbeddingProvider(IConfiguration config, ILogger<EmbeddingProvider> log, ILogger<E5Embedder> logEmbedder)
    {
        ModelPath = ResolveModelPath(config);

        if (ModelPath is null)
        {
            MissingReason = "no encuentro la carpeta models/multilingual-e5-small";
            log.LogWarning("Embeddings no disponibles: {Razon}", MissingReason);
            return;
        }

        _embedder = E5Embedder.TryLoad(ModelPath, logEmbedder);

        if (_embedder is null)
        {
            MissingReason = File.Exists(Path.Combine(ModelPath, "onnx", "model.onnx"))
                ? "el modelo esta en disco pero no se ha podido cargar"
                : "falta onnx/model.onnx (470 MB, no va en git)";
        }
    }

    /// <summary>Carpeta del modelo, o null si no se ha encontrado.</summary>
    public string? ModelPath { get; }

    /// <summary>Por que no esta disponible. Null cuando si lo esta.</summary>
    public string? MissingReason { get; }

    public bool Available => _embedder is not null;

    /// <summary>
    /// El embedder, o null. Quien lo use tiene que contemplar el null: no hay una
    /// version que lance excepcion, porque eso convertiria una degradacion prevista en
    /// un fallo.
    /// </summary>
    public E5Embedder? Embedder => _embedder;

    private static string? ResolveModelPath(IConfiguration config)
    {
        var configurada = config["Models:Path"];
        if (!string.IsNullOrWhiteSpace(configurada))
        {
            return Directory.Exists(configurada) ? Path.GetFullPath(configurada) : null;
        }

        // Subiendo desde el ejecutable, igual que las herramientas de paridad.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidata = Path.Combine(dir.FullName, "models", "multilingual-e5-small");
            if (Directory.Exists(candidata))
            {
                return candidata;
            }
            dir = dir.Parent;
        }

        return null;
    }

    public void Dispose() => _embedder?.Dispose();
}
