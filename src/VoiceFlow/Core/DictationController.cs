using System.Media;

namespace VoiceFlow.Core;

public enum DictationState { Idle, Recording, Processing }

/// <summary>
/// The heart of the app: record -> transcribe -> format -> inject -> log.
/// All public methods are expected to be called on the UI dispatcher thread
/// (the keyboard hook and tray marshal onto it already).
/// </summary>
public sealed class DictationController : IDisposable
{
    private readonly AudioRecorder _recorder = new();
    private readonly WhisperTranscriber _whisper = new();

    private string _targetApp = "";

    public DictationState State { get; private set; } = DictationState.Idle;
    public bool HandsFree { get; private set; }

    public event Action? StateChanged;
    /// <summary>Raised on the audio thread with mic peak 0..1.</summary>
    public event Action<float>? LevelChanged;
    public event Action<string>? Error;
    public event Action<HistoryEntry>? Inserted;

    public DictationController()
    {
        _recorder.LevelChanged += l => LevelChanged?.Invoke(l);
    }

    /// <summary>Loads the Whisper model in the background (startup / settings change).</summary>
    public void Preload()
    {
        var s = Services.Settings.Current;
        if (s.Engine == "cloud" || !WhisperTranscriber.ModelExists(s.ModelSize)) return;
        _ = Task.Run(async () =>
        {
            try { await _whisper.PreloadAsync(); } catch { }
        });
    }

    public void StartRecording(bool handsFree = false)
    {
        if (State != DictationState.Idle) return;
        var s = Services.Settings.Current;

        if (s.Engine != "cloud" && !WhisperTranscriber.ModelExists(s.ModelSize))
        {
            Error?.Invoke("Speech model not downloaded yet — open BONGA to download it.");
            return;
        }

        _targetApp = TextInjector.GetForegroundApp();
        try
        {
            _recorder.Start(s.MicDeviceNumber);
        }
        catch (Exception ex)
        {
            Error?.Invoke("Microphone error: " + ex.Message);
            return;
        }

        HandsFree = handsFree;
        State = DictationState.Recording;
        StateChanged?.Invoke();
        if (s.SoundFeedback)
            try { SystemSounds.Asterisk.Play(); } catch { }
    }

    /// <summary>A quick tap converts the in-progress hold into hands-free mode.</summary>
    public void MarkHandsFree()
    {
        if (State != DictationState.Recording) return;
        HandsFree = true;
        StateChanged?.Invoke();
    }

    public void Cancel()
    {
        if (State != DictationState.Recording) return;
        _recorder.Stop();
        State = DictationState.Idle;
        StateChanged?.Invoke();
    }

    public async Task StopAndInsertAsync()
    {
        if (State != DictationState.Recording) return;

        float[] samples = _recorder.Stop();
        double duration = samples.Length / (double)AudioRecorder.SampleRate;
        var startedAt = DateTime.Now;

        State = DictationState.Processing;
        StateChanged?.Invoke();
        try
        {
            if (duration < 0.4) return; // accidental tap — nothing useful recorded

            var s = Services.Settings.Current;

            string raw = s.Engine == "cloud" && !string.IsNullOrWhiteSpace(s.CloudApiKey)
                ? await CloudTranscriber.TranscribeAsync(samples, s)
                : await Task.Run(() => _whisper.TranscribeAsync(samples, CancellationToken.None));

            string text = TextFormatter.Apply(raw, s);

            if (s.AiPolish && !string.IsNullOrWhiteSpace(s.CloudApiKey) && text.Length > 0)
            {
                try { text = await AiPolisher.PolishAsync(text, s); }
                catch { /* polish is best-effort; keep local formatting */ }
            }

            if (string.IsNullOrWhiteSpace(text)) return;

            await Task.Run(() => TextInjector.Insert(text, s.InsertionMode));

            var entry = new HistoryEntry
            {
                Timestamp = startedAt,
                Text = text,
                App = _targetApp,
                DurationSeconds = Math.Round(duration, 1),
                Words = CountWords(text)
            };
            Services.History.Add(entry);
            Inserted?.Invoke(entry);
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex.Message);
        }
        finally
        {
            State = DictationState.Idle;
            StateChanged?.Invoke();
        }
    }

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    public void Dispose()
    {
        _recorder.Dispose();
        _whisper.Dispose();
    }
}
