using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalMailAI.Api.Model;

/// <summary>
/// Minimal client for Ollama on localhost (BACKEND.md §1, rule 1: localhost is the
/// only destination besides the user's own IMAP server).
///
/// Ollama is optional. Every call here fails as a typed exception rather than taking
/// the service down: without Ollama the product keeps working, it just loses
/// summaries and extraction.
/// </summary>
public sealed class OllamaClient(ILogger<OllamaClient> log)
{
    public const string Endpoint = "http://127.0.0.1:11434";

    /// <summary>
    /// The model the sidebar advertises. Kept here and read by ModelStatusService so
    /// the name shown and the name called can never drift apart.
    /// </summary>
    public const string Model = "qwen3:4b";

    // Generation is slow on CPU and this is an explicit user action, so the timeout is
    // generous. The availability probe uses its own short one: a sidebar must not hang
    // for two minutes because Ollama is not installed.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    /// <summary>
    /// Runs a prompt and returns the response text.
    /// </summary>
    /// <exception cref="OllamaUnavailableException">Ollama does not answer, or the
    /// model is not pulled.</exception>
    public async Task<string> GenerateAsync(
        string system, string prompt, int maxTokens, CancellationToken ct = default)
    {
        var request = new GenerateRequest(
            Model: Model,
            System: system,
            Prompt: prompt,
            Stream: false,

            // Qwen3 is a hybrid reasoning model with thinking ON by default. For a
            // three-line summary the reasoning is pure latency - hundreds of tokens
            // before the first word of the answer - so it is turned off explicitly.
            Think: false,
            Options: new GenerateOptions(
                NumPredict: maxTokens,

                // Low but not zero: a summary should be reproducible between two runs
                // over the same thread, while still reading like prose.
                Temperature: 0.3));

        HttpResponseMessage response;
        try
        {
            response = await Http.PostAsJsonAsync($"{Endpoint}/api/generate", request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Ollama did not answer on {Endpoint}", Endpoint);
            throw new OllamaUnavailableException(
                $"Ollama does not answer on {Endpoint}. Install it and run 'ollama serve'.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);

            // Ollama answers 404 for a model that is not pulled. That is a different
            // problem from "Ollama is not there" and deserves its own instruction.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new OllamaUnavailableException(
                    $"the model '{Model}' is not downloaded. Run: ollama pull {Model}");
            }

            throw new OllamaUnavailableException($"Ollama answered {(int)response.StatusCode}: {detail}");
        }

        var body = await response.Content.ReadFromJsonAsync<GenerateResponse>(ct);
        var text = body?.Response?.Trim();

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new OllamaUnavailableException("Ollama returned an empty response");
        }

        return text;
    }

    /// <summary>
    /// Whether Ollama answers, with a short timeout of its own. Used by the status
    /// endpoint, which is polled and must never block the sidebar.
    /// </summary>
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromMilliseconds(600) };
            using var r = await probe.GetAsync($"{Endpoint}/api/tags");
            return r.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private sealed record GenerateRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("system")] string System,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("think")] bool Think,
        [property: JsonPropertyName("options")] GenerateOptions Options);

    private sealed record GenerateOptions(
        [property: JsonPropertyName("num_predict")] int NumPredict,
        [property: JsonPropertyName("temperature")] double Temperature);

    private sealed record GenerateResponse(
        [property: JsonPropertyName("response")] string? Response);
}

/// <summary>Ollama is absent, unreachable, or the model is not pulled.</summary>
public sealed class OllamaUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
