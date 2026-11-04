using System.Text.Json.Serialization;
using Dapper;
using LocalMailAI.Api.Data;
using LocalMailAI.Api.Embeddings;

namespace LocalMailAI.Api.Model;

/// <summary>
/// Model status for the frontend sidebar (BACKEND.md §3).
/// </summary>
/// <param name="Examples">Examples in effect: excludes any neutralised by an undo.</param>
/// <param name="ExamplesBySource">Breakdown by origin, so the UI can distinguish
/// "I don't know you yet" from "I can't classify this".</param>
/// <param name="BootstrapRan">Whether the cold start has contributed any examples.</param>
/// <param name="Precision">Precision measured against the evaluation set. Null until
/// one exists: inventing a number here would be lying in the UI itself.</param>
/// <param name="LastTrained">Last retraining, or null if there is no model yet.</param>
public sealed record ModelStatusDto(
    int Examples,
    ExampleCounts ExamplesBySource,
    bool BootstrapRan,

    // These two ARE serialised when null, unlike the rest of the API. The difference
    // is semantic: in Email, a missing "extracted" means "this message has no
    // extraction"; here, null means "not measured yet", and the frontend needs to
    // receive the field to know that. Omitting it would make a precision.toFixed(2)
    // in the sidebar blow up.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? Precision,

    string Embeddings,
    string Slm,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? LastTrained,

    int IndexedEmails,
    bool OllamaAvailable,

    /// <summary>Whether the embedding model is loaded.</summary>
    bool EmbeddingsAvailable,

    /// <summary>
    /// Why embeddings are unavailable. Always present, null when they are available:
    /// the frontend needs the field in order to warn the user instead of letting the
    /// product quietly do less than it promises.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? EmbeddingsMissingReason);

/// <summary>
/// Gathers model status. The only thing that leaves the machine is a request to Ollama
/// on localhost:11434, which rule 1 of §1 explicitly allows.
/// </summary>
public sealed class ModelStatusService(
    Database db,
    CorrectionsRepository corrections,
    EmbeddingProvider embeddings,
    ILogger<ModelStatusService> log)
{
    // A single client with a very short timeout: this is requested on every sidebar
    // load and must not hang when Ollama is absent.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(500) };

    private static readonly TimeSpan OllamaCacheWindow = TimeSpan.FromSeconds(30);
    private bool _ollamaLastResult;
    private DateTimeOffset _ollamaCheckedAt = DateTimeOffset.MinValue;

    public async Task<ModelStatusDto> GetAsync()
    {
        using var cn = db.Open();

        // The example count lives in CorrectionsRepository: it is the same rule the
        // frontend's toast uses after a correction, and keeping it in two places would
        // eventually produce two different numbers for the same thing.
        var breakdown = await corrections.CountAsync();
        var total = breakdown.Total;

        var indexed = (int)await cn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM emails");

        // model_version > 0 means a trained classifier exists. At 0 there are only the
        // deterministic signals, which are not "trained".
        var lastTrained = await cn.ExecuteScalarAsync<string?>(
            "SELECT MAX(computed_at) FROM classifications WHERE model_version > 0");

        // Representative runs only. While there are none, precision is null and the
        // frontend shows "not measured", which is the truth: measuring on synthetic
        // mail says nothing about this person's mail.
        var precision = await cn.ExecuteScalarAsync<double?>(
            """
            SELECT precision_macro FROM eval_runs
            WHERE representative = 1
            ORDER BY created_at DESC
            LIMIT 1
            """);

        return new ModelStatusDto(
            Examples: total,
            ExamplesBySource: breakdown,
            BootstrapRan: breakdown.BootstrapSent + breakdown.BootstrapFolder > 0,

            // Comes from eval_runs, not from a constant. Null until it has been
            // measured against a representative set: the spec shows 0.91 as an
            // example, but that number can only come from real hand-labelled mail.
            Precision: precision,

            Embeddings: E5Embedder.ModelName,
            Slm: "Qwen3 4B - Q4_K_M",
            LastTrained: lastTrained is null
                ? null
                : DateTimeOffset.Parse(lastTrained, null, System.Globalization.DateTimeStyles.RoundtripKind),
            IndexedEmails: indexed,
            OllamaAvailable: await IsOllamaAvailableAsync(),
            EmbeddingsAvailable: embeddings.Available,
            EmbeddingsMissingReason: embeddings.MissingReason);
    }

    /// <summary>
    /// Checks whether Ollama answers on localhost:11434, with a short cache.
    /// If it is absent the product stays whole: no <c>extracted</c>, no
    /// <c>summary</c>, and not a single error (§7).
    /// </summary>
    private async Task<bool> IsOllamaAvailableAsync()
    {
        if (DateTimeOffset.UtcNow - _ollamaCheckedAt < OllamaCacheWindow)
        {
            return _ollamaLastResult;
        }

        try
        {
            using var r = await Http.GetAsync("http://127.0.0.1:11434/api/tags");
            _ollamaLastResult = r.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Not running, or not installed. That is a normal state, not a failure.
            _ollamaLastResult = false;
            log.LogDebug("Ollama does not answer on 127.0.0.1:11434: {Error}", ex.Message);
        }

        _ollamaCheckedAt = DateTimeOffset.UtcNow;
        return _ollamaLastResult;
    }
}
