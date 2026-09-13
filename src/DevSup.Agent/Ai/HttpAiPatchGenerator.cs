namespace DevSup.Agent.Ai;

using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DevSup.Core;
using Microsoft.Extensions.Logging;

/// <summary>Supported chat-API wire shapes.</summary>
public enum AiChatScheme
{
    OpenAiCompatible, // OpenAI, DeepSeek, Ollama /v1
    Anthropic,
    Gemini
}

/// <summary>Endpoint definition for one AI provider; defaults are overridable via the AiModels config section.</summary>
public sealed class AiModelEndpoint
{
    public AiModelProvider Provider { get; init; } = AiModelProvider.OpenAi;
    public AiChatScheme Scheme { get; init; } = AiChatScheme.OpenAiCompatible;
    public string BaseUrl { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 60;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && (!string.IsNullOrWhiteSpace(Model) || Scheme != AiChatScheme.Gemini);
}

/// <summary>
/// Builds an <see cref="AiModelEndpoint"/> for a bound provider/model, merging
/// built-in defaults with the optional <c>AiModels</c> configuration section.
/// </summary>
public sealed class AiEndpointResolver(Microsoft.Extensions.Configuration.IConfiguration config)
{
    public AiModelEndpoint Resolve(AiModelProvider provider, string model)
    {
        var defaults = provider switch
        {
            AiModelProvider.AnthropicClaude => new AiModelEndpoint
            {
                Provider = provider, Scheme = AiChatScheme.Anthropic,
                BaseUrl = "https://api.anthropic.com", Model = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-5" : model
            },
            AiModelProvider.GoogleGemini => new AiModelEndpoint
            {
                Provider = provider, Scheme = AiChatScheme.Gemini,
                BaseUrl = "https://generativelanguage.googleapis.com",
                Model = string.IsNullOrWhiteSpace(model) ? "gemini-2.5-flash" : model
            },
            AiModelProvider.DeepSeek => new AiModelEndpoint
            {
                Provider = provider, Scheme = AiChatScheme.OpenAiCompatible,
                BaseUrl = "https://api.deepseek.com", Model = string.IsNullOrWhiteSpace(model) ? "deepseek-chat" : model
            },
            AiModelProvider.Ollama => new AiModelEndpoint
            {
                Provider = provider, Scheme = AiChatScheme.OpenAiCompatible,
                BaseUrl = "http://localhost:11434", Model = string.IsNullOrWhiteSpace(model) ? "llama3.2" : model
            },
            _ => new AiModelEndpoint
            {
                Provider = provider, Scheme = AiChatScheme.OpenAiCompatible,
                BaseUrl = "https://api.openai.com", Model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model
            }
        };

        var section = $"AiModels:{provider}";
        var baseUrl = config[$"{section}:BaseUrl"];
        var configuredModel = config[$"{section}:Model"];
        var timeout = int.TryParse(config[$"{section}:TimeoutSeconds"], out var seconds) ? seconds : defaults.TimeoutSeconds;

        return new AiModelEndpoint
        {
            Provider = provider,
            Scheme = defaults.Scheme,
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? defaults.BaseUrl : baseUrl,
            Model = string.IsNullOrWhiteSpace(configuredModel) ? defaults.Model : configuredModel,
            TimeoutSeconds = timeout
        };
    }
}

/// <summary>
/// Real HTTP patch generator. Speaks the three chat-API wire shapes, teaches the
/// model the repair protocol (exact fragment → replacement) and parses the JSON
/// answer tolerantly. Any malformed or incomplete reply yields null — the pipeline
/// never trusts a misunderstood answer enough to write a file.
/// </summary>
public sealed class HttpAiPatchGenerator(
    IHttpClientFactory clientFactory,
    ILogger<HttpAiPatchGenerator> logger) : IAiPatchGenerator
{
    private const string SystemPrompt = """
        You are DevSup, an expert software engineer repairing a production API failure.
        Produce a SINGLE minimal, surgical code repair as JSON.

        Rules:
        - "file" is the repository-relative path of the file to change.
        - "fragment" is an EXACT contiguous string that exists verbatim in the current file.
        - "replacement" is the new text to substitute for that one occurrence.
        - The replacement must compile on its own (full statements, balanced braces).
        - Prefer a null-guard, early-return, or retry wrap around the failing line.
        - Never invent files; only edit a file shown in the context or obvious from the stack trace.
        - "summary" is a short commit-message style sentence.

        Respond with ONLY this JSON, no markdown fences:
        {"file": "...", "fragment": "...", "replacement": "...", "summary": "..."}
        """;

    public async Task<AiPatchSuggestion?> GenerateAsync(AiGenerationRequest request, AiModelEndpoint endpoint, string apiKey, CancellationToken ct)
    {
        if (!endpoint.IsConfigured)
        {
            return null;
        }

        try
        {
            var http = clientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(endpoint.TimeoutSeconds);

            var response = endpoint.Scheme switch
            {
                AiChatScheme.Anthropic => await CallAnthropicAsync(http, endpoint, apiKey, request, ct),
                AiChatScheme.Gemini => await CallGeminiAsync(http, endpoint, apiKey, request, ct),
                _ => await CallOpenAiCompatibleAsync(http, endpoint, apiKey, request, ct)
            };

            response.EnsureSuccessStatusCode();
            return await Parse(response);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI patch generation failed for {Path}", request.Failure.Path);
            return null;
        }
    }

    private static async Task<HttpResponseMessage> CallOpenAiCompatibleAsync(
        HttpClient http, AiModelEndpoint endpoint, string apiKey, AiGenerationRequest request, CancellationToken ct)
    {
        var url = endpoint.BaseUrl.TrimEnd('/') + "/v1/chat/completions";
        var body = new
        {
            model = endpoint.Model,
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = request.Prompt }
            }
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }
        return await http.SendAsync(message, ct);
    }

    private static async Task<HttpResponseMessage> CallAnthropicAsync(
        HttpClient http, AiModelEndpoint endpoint, string apiKey, AiGenerationRequest request, CancellationToken ct)
    {
        var url = endpoint.BaseUrl.TrimEnd('/') + "/v1/messages";
        var body = new
        {
            model = endpoint.Model,
            max_tokens = 1500,
            system = SystemPrompt,
            messages = new[] { new { role = "user", content = request.Prompt } }
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", "2023-06-01");
        return await http.SendAsync(message, ct);
    }

    private static async Task<HttpResponseMessage> CallGeminiAsync(
        HttpClient http, AiModelEndpoint endpoint, string apiKey, AiGenerationRequest request, CancellationToken ct)
    {
        var modelPath = System.Uri.EscapeDataString(endpoint.Model);
        var url = $"{endpoint.BaseUrl.TrimEnd('/')}/v1beta/models/{modelPath}:generateContent?key={System.Uri.EscapeDataString(apiKey)}";
        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = SystemPrompt } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = request.Prompt } } } }
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        return await http.SendAsync(message, ct);
    }

    private static async Task<AiPatchSuggestion?> Parse(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;

        var text = ReadOpenAiCompatibleText(root)
            ?? ReadAnthropicText(root)
            ?? ReadGeminiText(root);

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var json = ExtractJsonObject(text);
        if (json is null)
        {
            return null;
        }

        try
        {
            var suggestion = JsonSerializer.Deserialize<AiSuggestionJson>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (suggestion is null ||
                string.IsNullOrWhiteSpace(suggestion.File) ||
                string.IsNullOrWhiteSpace(suggestion.Fragment) ||
                string.IsNullOrWhiteSpace(suggestion.Replacement))
            {
                return null;
            }

            return new AiPatchSuggestion(suggestion.File, suggestion.Fragment, suggestion.Replacement, suggestion.Summary);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadOpenAiCompatibleText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            return null;
        }
        var first = choices.EnumerateArray().First();
        return first.TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String
            ? content.GetString()
            : null;
    }

    private static string? ReadAnthropicText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var part in content.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString();
            }
        }
        return null;
    }

    private static string? ReadGeminiText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    return text.GetString();
                }
            }
        }
        return null;
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }
        return text[start..(end + 1)];
    }

    private sealed class AiSuggestionJson
    {
        public string? File { get; set; }
        public string? Fragment { get; set; }
        public string? Replacement { get; set; }
        public string? Summary { get; set; }
    }
}