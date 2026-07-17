using System.Net.Http;
using System.Text;
using System.Text.Json;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.AI;

namespace CAP.Avalonia.Services;

/// <summary>
/// Claude (Anthropic) API integration for the in-app AI Design Assistant.
/// Supports standard chat and tool-calling (function calling) mode.
/// </summary>
public class AiService : IAiService
{
    private readonly HttpClient _httpClient;
    private string _apiKey = "";

    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string ModelId = "claude-sonnet-4-5";
    private const string AnthropicVersion = "2023-06-01";
    private const int MaxTokens = 2048;
    private const int MaxToolLoopIterations = 10;

    private const string SystemPrompt =
        "You are an AI assistant integrated into Lunima (Connect-A-PIC-Pro), a photonic integrated circuit design tool. " +
        "You help users design photonic circuits using natural language. " +
        "When the user asks you to build or create a circuit, use the available tools to place components and make connections. " +
        "Always call get_grid_state first to understand the current canvas state. " +
        "Use the available_types list from get_grid_state to find valid component names. " +
        "Typical grid coordinates are 0–5000 micrometers. Space components 150–300µm apart for compact designs. " +
        "IMPORTANT: NEVER call clear_grid unless the user explicitly asks you to clear, reset, or start fresh. " +
        "Always preserve existing components. Find empty space on the grid to place new circuits alongside existing ones. " +
        "AFTER placing or connecting components, ALWAYS do the following in order: " +
        "1. Call get_grid_state() to verify the components were actually placed and count them. " +
        "2. Call fit_to_view() so the user can immediately see the result. " +
        "3. Report the actual component count (e.g. 'Placed 5 components: 2 MMI, 2 phase shifters, 1 waveguide'). " +
        "If get_grid_state() shows fewer components than expected, report the discrepancy honestly — do NOT claim success. " +
        "Be concise and report what you did. Keep responses under 300 words.";

    /// <summary>
    /// Appends a directive to answer in the active UI language onto the base system
    /// prompt. English is the no-op default; other shipped languages map to their
    /// English name so the model replies in the same language as the interface.
    /// </summary>
    private static string BuildSystemPrompt()
    {
        var language = LocalizationService.Instance.ActiveLanguageCode switch
        {
            "de" => "German",
            "es" => "Spanish",
            "zh-Hans" => "Chinese",
            _ => "English"
        };
        return $"{SystemPrompt} Always respond in {language}.";
    }

    /// <inheritdoc/>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    /// <summary>
    /// Initializes the AI service with a shared HTTP client.
    /// </summary>
    public AiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    public void SetApiKey(string apiKey)
    {
        _apiKey = apiKey ?? "";
    }

    /// <inheritdoc/>
    public async Task<string> SendMessageAsync(
        string userMessage,
        IReadOnlyList<(string Role, string Content)> history,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
            return "Please configure your Claude API key in the AI Assistant settings below.";

        var messages = BuildTextMessageList(userMessage, history);
        var requestBody = new
        {
            model = ModelId,
            max_tokens = MaxTokens,
            system = BuildSystemPrompt(),
            messages
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var request = BuildHttpRequest(json);

        try
        {
            var response = await _httpClient.SendAsync(request, ct);
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return $"API error ({(int)response.StatusCode}): Check your API key and try again.";

            return ParseResponseText(responseJson);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex) { return $"Network error: {ex.Message}"; }
        catch (Exception ex) { return $"Unexpected error: {ex.Message}"; }
    }

    /// <inheritdoc/>
    public async Task<string> SendMessageWithToolsAsync(
        string userMessage,
        IReadOnlyList<(string Role, string Content)> history,
        IReadOnlyList<AiToolDefinition> tools,
        Func<string, string, Task<string>> executeToolAsync,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
            return "Please configure your Claude API key in the AI Assistant settings below.";

        var toolDefs = BuildToolDefinitions(tools);
        var messages = new List<object>(BuildTextMessageList(userMessage, history));

        for (int iteration = 0; iteration < MaxToolLoopIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            var requestBody = new
            {
                model = ModelId,
                max_tokens = MaxTokens,
                system = BuildSystemPrompt(),
                tools = toolDefs,
                messages
            };

            var responseJson = await PostJsonAsync(requestBody, ct);
            if (responseJson == null) return "Network error during tool call.";

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorEl))
                return $"API error: {errorEl.GetRawText()}";

            var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
            var contentArray = root.TryGetProperty("content", out var ca) ? ca : default;

            var textParts = ExtractTextParts(contentArray);
            var toolUseParts = ExtractToolUseParts(contentArray);

            if (stopReason == "end_turn" || toolUseParts.Count == 0)
                return textParts.Count > 0 ? string.Join("\n", textParts) : "Done.";

            // Add assistant message preserving tool_use blocks for API continuity
            var assistantContent = BuildAssistantContentBlocks(textParts, toolUseParts);
            messages.Add(new { role = "assistant", content = assistantContent });

            // Execute each tool and collect results
            var toolResults = new List<object>();
            foreach (var (toolId, toolName, toolInputJson) in toolUseParts)
            {
                ct.ThrowIfCancellationRequested();
                string toolResult;
                try { toolResult = await executeToolAsync(toolName, toolInputJson); }
                catch (Exception ex) { toolResult = $"Tool error: {ex.Message}"; }

                toolResults.Add(new
                {
                    type = "tool_result",
                    tool_use_id = toolId,
                    content = toolResult
                });
            }

            messages.Add(new { role = "user", content = toolResults });
        }

        return "Reached maximum tool iterations. Please try a simpler request.";
    }

    private async Task<string?> PostJsonAsync(object requestBody, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(requestBody);
        using var request = BuildHttpRequest(json);
        try
        {
            var response = await _httpClient.SendAsync(request, ct);
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static List<object> BuildTextMessageList(
        string userMessage,
        IReadOnlyList<(string Role, string Content)> history)
    {
        var messages = history
            .Select(h => (object)new { role = h.Role, content = h.Content })
            .ToList();
        messages.Add(new { role = "user", content = userMessage });
        return messages;
    }

    private static object[] BuildToolDefinitions(IReadOnlyList<AiToolDefinition> tools) =>
        tools.Select(t => (object)new
        {
            name = t.Name,
            description = t.Description,
            input_schema = t.InputSchema
        }).ToArray();

    private static List<string> ExtractTextParts(JsonElement contentArray)
    {
        var parts = new List<string>();
        if (contentArray.ValueKind != JsonValueKind.Array) return parts;

        foreach (var item in contentArray.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var type) && type.GetString() == "text"
                && item.TryGetProperty("text", out var text))
            {
                var t = text.GetString();
                if (!string.IsNullOrWhiteSpace(t)) parts.Add(t);
            }
        }
        return parts;
    }

    private static List<(string Id, string Name, string InputJson)> ExtractToolUseParts(
        JsonElement contentArray)
    {
        var parts = new List<(string, string, string)>();
        if (contentArray.ValueKind != JsonValueKind.Array) return parts;

        foreach (var item in contentArray.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "tool_use")
                continue;

            var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
            var inputJson = item.TryGetProperty("input", out var inputEl)
                ? inputEl.GetRawText() : "{}";

            parts.Add((id, name, inputJson));
        }
        return parts;
    }

    private static object[] BuildAssistantContentBlocks(
        List<string> textParts,
        List<(string Id, string Name, string InputJson)> toolUseParts)
    {
        var blocks = new List<object>();

        foreach (var text in textParts)
            blocks.Add(new { type = "text", text });

        foreach (var (id, name, inputJson) in toolUseParts)
        {
            using var doc = JsonDocument.Parse(inputJson);
            blocks.Add(new { type = "tool_use", id, name, input = doc.RootElement.Clone() });
        }

        return blocks.ToArray();
    }

    private HttpRequestMessage BuildHttpRequest(string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);
        return request;
    }

    private static string ParseResponseText(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var text = doc.RootElement
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString();
            return text ?? "No response received.";
        }
        catch
        {
            return "Could not parse AI response.";
        }
    }
}
