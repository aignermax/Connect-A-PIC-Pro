using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AiTools;

namespace CAP.Avalonia.ViewModels.AI;

/// <summary>
/// ViewModel for the in-app AI Design Assistant chat panel.
/// Manages conversation history, API key configuration, and communication
/// with the <see cref="IAiService"/>. When an <see cref="IAiToolRegistry"/> is
/// available, enables tool-calling so the AI can manipulate the circuit grid.
/// </summary>
public partial class AiAssistantViewModel : ObservableObject
{
    private readonly IAiService _aiService;
    private readonly UserPreferencesService _preferencesService;
    private readonly IAiToolRegistry? _toolRegistry;
    private readonly IUrlLauncher _urlLauncher;
    private CancellationTokenSource? _cancellationSource;

    private const int MaxHistoryMessages = 20;

    /// <summary>Gets the ordered chat history displayed in the panel.</summary>
    public ObservableCollection<AiChatMessage> Messages { get; } = new();

    [ObservableProperty] private string _userInput = "";
    [ObservableProperty] private bool _isTyping;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApiKeySet))]
    private string _apiKey = "";

    [ObservableProperty] private bool _isSettingsExpanded;
    [ObservableProperty] private string _statusText = "";

    /// <summary>
    /// True when an API key has been configured. Bound by the chat panel to
    /// decide whether to show the chat input or a "Set API key in Settings"
    /// shortcut button.
    /// </summary>
    public bool IsApiKeySet => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// Initializes the ViewModel, loads persisted API key, and shows a welcome message.
    /// </summary>
    public AiAssistantViewModel(
        IAiService aiService,
        UserPreferencesService preferencesService,
        IAiToolRegistry? toolRegistry = null,
        IUrlLauncher? urlLauncher = null)
    {
        _aiService = aiService;
        _preferencesService = preferencesService;
        _toolRegistry = toolRegistry;
        _urlLauncher = urlLauncher ?? PlatformShellLauncher.CreateDefault();

        var savedKey = _preferencesService.GetAiApiKey();
        if (!string.IsNullOrEmpty(savedKey))
        {
            _apiKey = savedKey;
            _aiService.SetApiKey(savedKey);
        }

        ShowWelcomeMessage();
    }

    /// <summary>
    /// Sends the current <see cref="UserInput"/> to the AI and appends the response.
    /// Uses tool-calling when a tool registry is available.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendMessage()
    {
        var text = UserInput.Trim();
        if (string.IsNullOrEmpty(text)) return;

        UserInput = "";
        Messages.Add(new AiChatMessage { Role = AiChatRole.User, Content = text });

        IsTyping = true;
        SendMessageCommand.NotifyCanExecuteChanged();
        StatusText = "AI is thinking...";

        _cancellationSource = new CancellationTokenSource();

        try
        {
            var history = BuildConversationHistory();
            string response;

            if (_toolRegistry != null)
            {
                var tools = BuildToolDefinitions();
                response = await _aiService.SendMessageWithToolsAsync(
                    text, history, tools,
                    executeToolAsync: ExecuteToolOnUiThreadAsync,
                    ct: _cancellationSource.Token);
            }
            else
            {
                response = await _aiService.SendMessageAsync(text, history, _cancellationSource.Token);
            }

            Messages.Add(new AiChatMessage { Role = AiChatRole.Assistant, Content = response });
            StatusText = "";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Request cancelled.";
        }
        finally
        {
            IsTyping = false;
            SendMessageCommand.NotifyCanExecuteChanged();
            _cancellationSource?.Dispose();
            _cancellationSource = null;
        }
    }

    private bool CanSend() => !IsTyping;

    /// <summary>Cancels an in-flight AI request.</summary>
    [RelayCommand]
    private void CancelRequest() => _cancellationSource?.Cancel();

    /// <summary>Clears chat history and resets the conversation.</summary>
    [RelayCommand]
    private void ClearHistory()
    {
        Messages.Clear();
        Messages.Add(new AiChatMessage
        {
            Role = AiChatRole.Assistant,
            Content = "Chat history cleared. How can I help you design your photonic circuit?"
        });
        StatusText = "";
    }

    /// <summary>
    /// Legacy "Save" command retained for the explicit-save flow. The actual
    /// persistence now happens on every edit through
    /// <see cref="OnApiKeyChanged"/>; this command only collapses the (no
    /// longer shown) expander and updates the status message.
    /// </summary>
    [RelayCommand]
    private void SaveApiKey()
    {
        var key = ApiKey.Trim();
        IsSettingsExpanded = false;
        StatusText = string.IsNullOrEmpty(key) ? "API key cleared." : "API key saved.";
    }

    partial void OnApiKeyChanged(string value)
    {
        // Auto-persist: the Settings-window TextBox has no explicit Save button,
        // so typing into it must immediately update the in-memory AI service
        // and on-disk preferences. Writes are idempotent and API keys change
        // rarely, so per-keystroke cost is negligible. Wrapped in try/catch
        // because this runs from inside an [ObservableProperty] setter;
        // an exception would propagate into Avalonia's binding pipeline and
        // only appear as a silent binding-error log — surface it as a
        // user-visible status message instead.
        var trimmed = value?.Trim() ?? string.Empty;
        try
        {
            _aiService.SetApiKey(trimmed);
            _preferencesService.SetAiApiKey(trimmed);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not save API key: {ex.Message}";
        }
    }

    /// <summary>Opens the Anthropic API keys page in the default browser.</summary>
    [RelayCommand]
    private void OpenApiKeyPage()
    {
        const string Url = "https://console.anthropic.com/settings/keys";
        try
        {
            _urlLauncher.Open(Url);
        }
        catch
        {
            // Non-fatal: the user can navigate manually. Surface the fallback URL so they are not blocked.
            StatusText = "Could not open browser. Visit: console.anthropic.com/settings/keys";
        }
    }

    /// <summary>
    /// Executes a tool on the Avalonia UI thread to safely modify ObservableCollections.
    /// </summary>
    private Task<string> ExecuteToolOnUiThreadAsync(string toolName, string inputJson) =>
        Dispatcher.UIThread.InvokeAsync(async () => await DispatchToolAsync(toolName, inputJson));

    private async Task<string> DispatchToolAsync(string toolName, string inputJson)
    {
        if (_toolRegistry == null) return "Tool registry not available.";

        var tool = _toolRegistry.GetTool(toolName);
        if (tool == null) return $"Unknown tool: {toolName}";

        StatusText = $"Executing: {toolName}...";
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            return await tool.ExecuteAsync(doc.RootElement);
        }
        catch (Exception ex)
        {
            return $"Tool '{toolName}' failed: {ex.Message}";
        }
        finally
        {
            StatusText = "";
        }
    }

    private IReadOnlyList<AiToolDefinition> BuildToolDefinitions()
    {
        if (_toolRegistry == null) return Array.Empty<AiToolDefinition>();
        return _toolRegistry.GetAllTools()
            .Select(t => new AiToolDefinition { Name = t.Name, Description = t.Description, InputSchema = t.InputSchema })
            .ToList();
    }

    private IReadOnlyList<(string Role, string Content)> BuildConversationHistory() =>
        Messages
            .TakeLast(MaxHistoryMessages)
            .Where(m => m.Role is AiChatRole.User or AiChatRole.Assistant)
            .Select(m => (m.IsUser ? "user" : "assistant", m.Content))
            .ToList();

    private void ShowWelcomeMessage()
    {
        var hasKey = _aiService.IsConfigured;
        var gridCapable = _toolRegistry != null;

        var welcome = (hasKey, gridCapable) switch
        {
            (false, _) =>
                "Hello! I'm your AI Design Assistant. Configure your Claude API key in the settings below to get started.",
            (true, true) =>
                "Hello! I'm your AI Design Assistant with grid manipulation capabilities. " +
                "Ask me to build a photonic circuit and I'll place components and connections for you!",
            (true, false) =>
                "Hello! I'm your AI Design Assistant. Describe a photonic circuit and I'll help you design it."
        };

        Messages.Add(new AiChatMessage { Role = AiChatRole.Assistant, Content = welcome });
    }
}
