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

    /// <summary>
    /// Tokens allowed for the model's reasoning on top of the answer itself. Qwen3
    /// thinks before writing and that thinking is billed to the same budget.
    ///
    /// Generous because it is a ceiling, not a target: generation stops when the
    /// model finishes, so a high limit costs nothing on the common case and is the
    /// difference between a summary and an empty answer on a long thread. Measured
    /// at 900, where threads in Spanish ran out mid-reasoning.
    /// </summary>
    private const int ThinkingBudget = 3000;

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
        // /api/chat and not /api/generate. Qwen3 is a hybrid reasoning model and
        // thinks before answering; only the chat endpoint separates that reasoning
        // into its own field. On /api/generate it arrives glued to the front of the
        // answer, and "think": false does not prevent it - the model reasons anyway
        // and the flag only stops Ollama from splitting it out.
        var request = new ChatRequest(
            Model: Model,
            Messages:
            [
                new ChatMessage("system", system),
                new ChatMessage("user", prompt),
            ],
            Stream: false,
            Options: new GenerateOptions(
                // The budget covers reasoning AND answer. Sized for the answer plus
                // room to think: too tight and generation stops mid-reasoning, which
                // returns an empty content rather than a short summary.
                NumPredict: maxTokens + ThinkingBudget,

                // Low but not zero: a summary should be reproducible between two runs
                // over the same thread, while still reading like prose.
                Temperature: 0.3));

        HttpResponseMessage response;
        try
        {
            response = await Http.PostAsJsonAsync($"{Endpoint}/api/chat", request, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Timing out is not the same as not being installed, and saying "install
            // Ollama" to someone who has it running is worse than saying nothing.
            log.LogWarning(ex, "Ollama timed out after {Timeout}", Http.Timeout);
            throw new OllamaUnavailableException(
                $"Ollama took longer than {Http.Timeout.TotalMinutes:0} minutes. The first "
                + "call after starting it also loads the model into memory.", ex);
        }
        catch (HttpRequestException ex)
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

        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(ct);
        var text = body?.Message?.Content?.Trim();

        if (string.IsNullOrWhiteSpace(text))
        {
            // The usual cause is the token budget running out during the reasoning,
            // which leaves the answer empty. Saying so beats "empty response".
            throw new OllamaUnavailableException(
                body?.DoneReason == "length"
                    ? "the model ran out of tokens while reasoning and never reached an answer"
                    : "Ollama returned an empty response");
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

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] ChatMessage[] Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("options")] GenerateOptions Options);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record GenerateOptions(
        [property: JsonPropertyName("num_predict")] int NumPredict,
        [property: JsonPropertyName("temperature")] double Temperature);

    private sealed record ChatResponse(
        [property: JsonPropertyName("message")] ChatResponseMessage? Message,
        [property: JsonPropertyName("done_reason")] string? DoneReason);

    // "thinking" is deliberately not read: it is the model's reasoning, not the
    // summary, and the UI must never show it.
    private sealed record ChatResponseMessage(
        [property: JsonPropertyName("content")] string? Content);
}

/// <summary>Ollama is absent, unreachable, or the model is not pulled.</summary>
public sealed class OllamaUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
