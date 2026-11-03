using System.Text.Json.Serialization;
using Dapper;
using LocalMailAI.Api.Data;
using LocalMailAI.Api.Embeddings;

namespace LocalMailAI.Api.Model;

/// <summary>
/// Estado del modelo para la barra lateral del front (BACKEND.md §3).
/// </summary>
/// <param name="Examples">Ejemplos en vigor: no incluye los neutralizados por un deshacer.</param>
/// <param name="ExamplesBySource">Desglose por origen, para que la UI pueda distinguir
/// "aun no te conozco" de "no se clasificar esto".</param>
/// <param name="BootstrapRan">Si el arranque en frio ya ha aportado ejemplos.</param>
/// <param name="Precision">Precision medida contra el set de evaluacion. Null mientras
/// no exista ese set: inventarse un numero aqui seria mentir en la propia UI.</param>
/// <param name="LastTrained">Ultimo reentrenamiento, o null si todavia no hay modelo.</param>
public sealed record ModelStatusDto(
    int Examples,
    ExampleCounts ExamplesBySource,
    bool BootstrapRan,

    // Estos dos SI se serializan cuando son null, al contrario que el resto de la API.
    // La diferencia es semantica: en Email, "extracted" ausente significa "este correo
    // no tiene extraccion"; aqui, null significa "todavia no se ha medido", y el front
    // necesita recibir el campo para saber que no lo tiene. Omitirlo haria que un
    // precision.toFixed(2) en la barra lateral explotara.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? Precision,

    string Embeddings,
    string Slm,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? LastTrained,

    int IndexedEmails,
    bool OllamaAvailable,

    /// <summary>Si el modelo de embeddings esta cargado.</summary>
    bool EmbeddingsAvailable,

    /// <summary>
    /// Por que no estan los embeddings. Siempre presente, null cuando si estan: el
    /// front necesita recibir el campo para poder avisar en la interfaz en vez de
    /// dejar que el producto haga menos de lo que promete sin explicar por que.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? EmbeddingsMissingReason);

/// <summary>
/// Reune el estado del modelo. Lo unico que sale de la maquina es una peticion a
/// Ollama en localhost:11434, que la regla 1 de §1 permite explicitamente.
/// </summary>
public sealed class ModelStatusService(
    Database db,
    CorrectionsRepository correcciones,
    EmbeddingProvider embeddings,
    ILogger<ModelStatusService> log)
{
    // Un unico cliente, con un tiempo de espera muy corto: esto se pide en cada carga
    // de la barra lateral y no puede quedarse colgado si Ollama no esta.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(500) };

    private static readonly TimeSpan CacheOllama = TimeSpan.FromSeconds(30);
    private bool _ollamaUltimo;
    private DateTimeOffset _ollamaComprobado = DateTimeOffset.MinValue;

    public async Task<ModelStatusDto> GetAsync()
    {
        using var cn = db.Open();

        // El conteo de ejemplos vive en CorrectionsRepository: es la misma regla que
        // usa el toast del front tras una correccion, y tenerla en dos sitios acabaria
        // dando dos numeros distintos para lo mismo.
        var desglose = await correcciones.CountAsync();
        var total = desglose.Total;

        var indexados = (int)await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM emails");

        // model_version > 0 significa que hay un clasificador entrenado. Con 0 solo
        // estan las senales deterministas, que no se "entrenan".
        var ultimoEntrenamiento = await cn.ExecuteScalarAsync<string?>(
            "SELECT MAX(computed_at) FROM classifications WHERE model_version > 0");

        // Solo las pasadas representativas. Mientras no haya ninguna, precision es
        // null y el front pinta "sin medir", que es la verdad: medir sobre correo
        // sintetico no dice nada sobre el correo de esta persona.
        var precision = await cn.ExecuteScalarAsync<double?>(
            """
            SELECT precision_macro FROM eval_runs
            WHERE representative = 1
            ORDER BY created_at DESC
            LIMIT 1
            """);

        return new ModelStatusDto(
            Examples: total,
            ExamplesBySource: desglose,
            BootstrapRan: desglose.BootstrapSent + desglose.BootstrapFolder > 0,

            // Sale de eval_runs, no de una constante. Null mientras no se haya medido
            // contra un set representativo: la spec pone 0.91 como ejemplo, pero ese
            // numero solo puede salir de medir contra correo real etiquetado a mano.
            Precision: precision,

            Embeddings: E5Embedder.ModelName,
            Slm: "Qwen3 4B - Q4_K_M",
            LastTrained: ultimoEntrenamiento is null
                ? null
                : DateTimeOffset.Parse(ultimoEntrenamiento, null, System.Globalization.DateTimeStyles.RoundtripKind),
            IndexedEmails: indexados,
            OllamaAvailable: await OllamaDisponibleAsync(),
            EmbeddingsAvailable: embeddings.Available,
            EmbeddingsMissingReason: embeddings.MissingReason);
    }

    /// <summary>
    /// Comprueba si Ollama responde en localhost:11434, con cache corta.
    /// Si no esta, el producto sigue entero: sin <c>extracted</c> ni <c>summary</c>
    /// y sin un solo error (§7).
    /// </summary>
    private async Task<bool> OllamaDisponibleAsync()
    {
        if (DateTimeOffset.UtcNow - _ollamaComprobado < CacheOllama)
        {
            return _ollamaUltimo;
        }

        try
        {
            using var r = await Http.GetAsync("http://127.0.0.1:11434/api/tags");
            _ollamaUltimo = r.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // No esta arrancado, o no esta instalado. Es un estado normal, no un fallo.
            _ollamaUltimo = false;
            log.LogDebug("Ollama no responde en 127.0.0.1:11434: {Error}", ex.Message);
        }

        _ollamaComprobado = DateTimeOffset.UtcNow;
        return _ollamaUltimo;
    }

    }
