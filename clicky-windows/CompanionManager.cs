using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using WpfApplication = System.Windows.Application;
using ClickyWindows.Services;

namespace ClickyWindows;

/// <summary>
/// The voice state of the companion. Drives UI in the overlay and tray panel.
/// </summary>
public enum CompanionVoiceState
{
    Idle,
    Listening,
    Processing,
    Responding
}

/// <summary>
/// Central state machine. Owns the full push-to-talk pipeline:
/// Ctrl+Alt press → mic → AssemblyAI → transcript + screenshot → Claude → TTS → cursor pointing
/// </summary>
public sealed class CompanionManager : INotifyPropertyChanged, IDisposable
{
    public const string WorkerBaseUrl = "https://clicky-proxy.shreshthsharma1904.workers.dev";

    private const string SystemPrompt =
        "You are Clicky, a friendly AI learning companion that lives next to the user's cursor on Windows. " +
        "You can see their screen and help them understand what they're looking at. " +
        "Be concise — your responses will be spoken aloud via text-to-speech. " +
        "When you want to point at a specific UI element in the screenshots, embed a tag like: " +
        "[POINT:x,y:description:Screen 1 (Primary)] where x,y are pixel coordinates in that screenshot " +
        "and the screen name matches the label shown with each image. " +
        "When the user explicitly asks you to click something, you can actually perform the click using: " +
        "[CLICK:x,y:description:Screen 1 (Primary)] — this will move the user's cursor and left-click. " +
        "Never CLICK destructive actions (close, delete, send, submit, purchase, sign out) unless the user " +
        "names that exact action. When unsure, prefer POINT over CLICK and ask for confirmation. " +
        "Only include one POINT or CLICK tag per response. Keep responses under 3 sentences when possible.";

    private CompanionVoiceState _voiceState = CompanionVoiceState.Idle;
    public CompanionVoiceState VoiceState
    {
        get => _voiceState;
        private set { _voiceState = value; OnPropertyChanged(); OnPropertyChanged(nameof(VoiceStateDisplayText)); }
    }

    private string _streamingResponseText = "";
    public string StreamingResponseText
    {
        get => _streamingResponseText;
        private set { _streamingResponseText = value; OnPropertyChanged(); }
    }

    private string _lastTranscript = "";
    public string LastTranscript
    {
        get => _lastTranscript;
        private set { _lastTranscript = value; OnPropertyChanged(); }
    }

    private float _audioPowerLevel = 0f;
    public float AudioPowerLevel
    {
        get => _audioPowerLevel;
        private set { _audioPowerLevel = value; OnPropertyChanged(); }
    }

    private string _selectedModel = "claude-sonnet-4-6";
    public string SelectedModel
    {
        get => _selectedModel;
        set
        {
            _selectedModel = value;
            _claudeApiClient.Model = value;
            OnPropertyChanged();
        }
    }

    private bool _isCursorEnabled = true;
    public bool IsCursorEnabled
    {
        get => _isCursorEnabled;
        set { _isCursorEnabled = value; OnPropertyChanged(); CursorEnabledChanged?.Invoke(value); }
    }

    public string VoiceStateDisplayText => VoiceState switch
    {
        CompanionVoiceState.Idle => "Ready — hold Ctrl+Alt to talk",
        CompanionVoiceState.Listening => "Listening...",
        CompanionVoiceState.Processing => "Thinking...",
        CompanionVoiceState.Responding => "Responding...",
        _ => ""
    };

    public event Action<bool>? CursorEnabledChanged;
    public event Action<PointingTarget>? PointingTargetDetected;
    public event Action<ClickTarget>? ClickTargetDetected;

    private readonly GlobalHotkeyMonitor _globalHotkeyMonitor;
    private readonly AudioCaptureService _audioCaptureService;
    private readonly AssemblyAiTranscriptionService _assemblyAiTranscriptionService;
    private readonly ClaudeApiClient _claudeApiClient;
    private readonly ElevenLabsTtsClient _elevenLabsTtsClient;

    private readonly List<(string UserMessage, string AssistantResponse)> _conversationHistory = new();
    private CancellationTokenSource? _currentResponseCancellationTokenSource;

    private readonly Dispatcher _uiDispatcher;
    private bool _disposed = false;

    private static readonly Regex PointTagRegex = new(
        @"\[POINT:(\d+(?:\.\d+)?)[:,](\d+(?:\.\d+)?):(?:([^:\]]+):)?([^\]]+)\]",
        RegexOptions.Compiled
    );

    private static readonly Regex ClickTagRegex = new(
        @"\[CLICK:(\d+(?:\.\d+)?)[:,](\d+(?:\.\d+)?):(?:([^:\]]+):)?([^\]]+)\]",
        RegexOptions.Compiled
    );

    public CompanionManager()
    {
        _uiDispatcher = WpfApplication.Current.Dispatcher;

        _globalHotkeyMonitor = new GlobalHotkeyMonitor();
        _audioCaptureService = new AudioCaptureService();
        _assemblyAiTranscriptionService = new AssemblyAiTranscriptionService(WorkerBaseUrl);
        _claudeApiClient = new ClaudeApiClient(WorkerBaseUrl, _selectedModel);
        _elevenLabsTtsClient = new ElevenLabsTtsClient(WorkerBaseUrl);
    }

    public void Start()
    {
        _globalHotkeyMonitor.HotkeyPressed += OnPushToTalkPressed;
        _globalHotkeyMonitor.HotkeyReleased += OnPushToTalkReleased;

        _audioCaptureService.AudioPowerLevelChanged += level =>
            _uiDispatcher.InvokeAsync(() => AudioPowerLevel = level);

        Console.WriteLine("🎯 Clicky: CompanionManager started");
    }

    public void Stop()
    {
        _globalHotkeyMonitor.HotkeyPressed -= OnPushToTalkPressed;
        _globalHotkeyMonitor.HotkeyReleased -= OnPushToTalkReleased;
        _audioCaptureService.StopCapture();
    }

    private void OnPushToTalkPressed()
    {
        _uiDispatcher.InvokeAsync(async () =>
        {
            _currentResponseCancellationTokenSource?.Cancel();
            _currentResponseCancellationTokenSource = null;

            _elevenLabsTtsClient.StopPlayback();

            StreamingResponseText = "";
            VoiceState = CompanionVoiceState.Listening;

            await _assemblyAiTranscriptionService.StartSessionAsync(
                onPartialTranscript: partialText =>
                    _uiDispatcher.InvokeAsync(() => LastTranscript = partialText),
                onFinalTranscript: finalText =>
                    _uiDispatcher.InvokeAsync(() => LastTranscript = finalText),
                onError: error =>
                    Console.WriteLine($"❌ AssemblyAI error: {error.Message}")
            );

            _audioCaptureService.AudioChunkAvailable += OnAudioChunkAvailable;
            _audioCaptureService.StartCapture();

            Console.WriteLine("🎙️ Push-to-talk: started listening");
        });
    }

    private void OnPushToTalkReleased()
    {
        _uiDispatcher.InvokeAsync(async () =>
        {
            _audioCaptureService.StopCapture();
            _audioCaptureService.AudioChunkAvailable -= OnAudioChunkAvailable;

            VoiceState = CompanionVoiceState.Processing;

            string finalTranscript = await _assemblyAiTranscriptionService.CloseSessionAndWaitForFinalTranscriptAsync();
            LastTranscript = finalTranscript;
            Console.WriteLine($"🎙️ Final transcript: \"{finalTranscript}\"");

            if (string.IsNullOrWhiteSpace(finalTranscript))
            {
                VoiceState = CompanionVoiceState.Idle;
                return;
            }

            await ProcessTranscriptAndRespondAsync(finalTranscript);
        });
    }

    private async void OnAudioChunkAvailable(byte[] pcm16Chunk)
    {
        await _assemblyAiTranscriptionService.SendAudioChunkAsync(pcm16Chunk);
    }

    private async Task ProcessTranscriptAndRespondAsync(string userTranscript)
    {
        var cancellationTokenSource = new CancellationTokenSource();
        _currentResponseCancellationTokenSource = cancellationTokenSource;

        try
        {
            var capturedScreens = await Task.Run(ScreenCaptureUtility.CaptureAllScreens);
            var screenshots = capturedScreens
                .Select(s => (s.JpegData, s.Label, s.Bounds.Width, s.Bounds.Height))
                .ToList();

            VoiceState = CompanionVoiceState.Responding;
            StreamingResponseText = "";

            string fullResponse = await _claudeApiClient.AnalyzeScreensAndAskAsync(
                screenshots,
                SystemPrompt,
                _conversationHistory,
                userTranscript,
                onTextChunk: responseText =>
                    _uiDispatcher.InvokeAsync(() =>
                        StreamingResponseText = StripPointTagsForDisplay(responseText)
                    ),
                cancellationToken: cancellationTokenSource.Token
            );

            _conversationHistory.Add((userTranscript, fullResponse));
            while (_conversationHistory.Count > 10)
                _conversationHistory.RemoveAt(0);

            var pointingTarget = ParsePointingTarget(fullResponse, capturedScreens);
            if (pointingTarget != null)
            {
                await _uiDispatcher.InvokeAsync(() =>
                    PointingTargetDetected?.Invoke(pointingTarget)
                );
            }

            var clickTarget = ParseClickTarget(fullResponse, capturedScreens);
            if (clickTarget != null)
            {
                await _uiDispatcher.InvokeAsync(() =>
                    ClickTargetDetected?.Invoke(clickTarget)
                );
            }

            string textForTts = StripPointTagsForDisplay(fullResponse);
            await _elevenLabsTtsClient.SpeakTextAsync(textForTts, cancellationTokenSource.Token);

            await _uiDispatcher.InvokeAsync(() => VoiceState = CompanionVoiceState.Idle);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("🌐 Claude response cancelled (user spoke again)");
            await _uiDispatcher.InvokeAsync(() => VoiceState = CompanionVoiceState.Idle);
        }
        catch (Exception responseException)
        {
            Console.WriteLine($"❌ Response pipeline error: {responseException}");
            await _uiDispatcher.InvokeAsync(() =>
            {
                StreamingResponseText = $"Error: {responseException.Message}";
                VoiceState = CompanionVoiceState.Idle;
            });
        }
    }

    private PointingTarget? ParsePointingTarget(
        string claudeResponse,
        IReadOnlyList<ScreenCaptureUtility.CapturedScreen> capturedScreens
    )
    {
        var match = PointTagRegex.Match(claudeResponse);
        if (!match.Success) return null;

        if (!double.TryParse(match.Groups[1].Value, out double screenshotX)) return null;
        if (!double.TryParse(match.Groups[2].Value, out double screenshotY)) return null;

        // Group 3 is optional description; group 4 is always screenName
        string screenName = match.Groups[4].Value.Trim();
        string description = match.Groups[3].Success && match.Groups[3].Value.Trim().Length > 0
            ? match.Groups[3].Value.Trim()
            : screenName;

        var targetScreen = capturedScreens.FirstOrDefault(s =>
            s.Label.Equals(screenName, StringComparison.OrdinalIgnoreCase)
        );

        // If Claude dropped the screen name and there's only one screen,
        // re-interpret group 4 as the description and use the single screen.
        if (targetScreen == null && capturedScreens.Count == 1)
        {
            targetScreen = capturedScreens[0];
            description = screenName;
            screenName = targetScreen.Label;
        }

        if (targetScreen == null)
        {
            Console.WriteLine(
                $"⚠️ POINT tag references unknown screen '{screenName}'"
            );
            return null;
        }

        double actualScreenX = targetScreen.Bounds.X + screenshotX;
        double actualScreenY = targetScreen.Bounds.Y + screenshotY;

        Console.WriteLine(
            $"🎯 POINT: ({screenshotX}, {screenshotY}) on '{screenName}' → " +
            $"screen coords ({actualScreenX}, {actualScreenY}) — \"{description}\""
        );

        return new PointingTarget(actualScreenX, actualScreenY, description, screenName);
    }

    private ClickTarget? ParseClickTarget(
        string claudeResponse,
        IReadOnlyList<ScreenCaptureUtility.CapturedScreen> capturedScreens
    )
    {
        var match = ClickTagRegex.Match(claudeResponse);
        if (!match.Success) return null;

        if (!double.TryParse(match.Groups[1].Value, out double screenshotX)) return null;
        if (!double.TryParse(match.Groups[2].Value, out double screenshotY)) return null;

        string screenName = match.Groups[4].Value.Trim();
        string description = match.Groups[3].Success && match.Groups[3].Value.Trim().Length > 0
            ? match.Groups[3].Value.Trim()
            : screenName;

        var targetScreen = capturedScreens.FirstOrDefault(s =>
            s.Label.Equals(screenName, StringComparison.OrdinalIgnoreCase)
        );

        if (targetScreen == null && capturedScreens.Count == 1)
        {
            targetScreen = capturedScreens[0];
            description = screenName;
            screenName = targetScreen.Label;
        }

        if (targetScreen == null)
        {
            Console.WriteLine($"⚠️ CLICK tag references unknown screen '{screenName}'");
            return null;
        }

        double actualScreenX = targetScreen.Bounds.X + screenshotX;
        double actualScreenY = targetScreen.Bounds.Y + screenshotY;

        Console.WriteLine(
            $"🖱️ CLICK: ({screenshotX}, {screenshotY}) on '{screenName}' → " +
            $"screen coords ({actualScreenX}, {actualScreenY}) — \"{description}\""
        );

        return new ClickTarget(actualScreenX, actualScreenY, description, screenName);
    }

    private static string StripPointTagsForDisplay(string text)
    {
        text = PointTagRegex.Replace(text, "");
        text = ClickTagRegex.Replace(text, "");
        return text.Trim();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _globalHotkeyMonitor.Dispose();
        _audioCaptureService.Dispose();
        _assemblyAiTranscriptionService.Dispose();
        _elevenLabsTtsClient.Dispose();
    }
}

public record PointingTarget(
    double ScreenX,
    double ScreenY,
    string Description,
    string ScreenName
);

public record ClickTarget(
    double ScreenX,
    double ScreenY,
    string Description,
    string ScreenName
);
