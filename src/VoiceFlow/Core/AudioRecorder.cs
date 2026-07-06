using NAudio.Wave;

namespace VoiceFlow.Core;

/// <summary>
/// Captures microphone audio as 16 kHz mono PCM float samples (the format
/// Whisper expects) and reports live input levels for the overlay bars.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    public const int SampleRate = 16000;

    private WaveInEvent? _waveIn;
    private List<float> _samples = new();
    private readonly object _lock = new();

    /// <summary>Peak level of the latest buffer, 0..1.</summary>
    public event Action<float>? LevelChanged;

    public bool IsRecording { get; private set; }

    public static List<(int Number, string Name)> ListDevices()
    {
        var list = new List<(int, string)> { (-1, "System default microphone") };
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            try { list.Add((i, WaveInEvent.GetCapabilities(i).ProductName)); }
            catch { }
        }
        return list;
    }

    public void Start(int deviceNumber)
    {
        Stop();
        lock (_lock) { _samples = new List<float>(SampleRate * 30); }

        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 50
        };
        _waveIn.DataAvailable += OnData;
        _waveIn.StartRecording();
        IsRecording = true;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        float peak = 0f;
        int count = e.BytesRecorded / 2;
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
            {
                short s = BitConverter.ToInt16(e.Buffer, i * 2);
                float f = s / 32768f;
                _samples.Add(f);
                float abs = Math.Abs(f);
                if (abs > peak) peak = abs;
            }
        }
        LevelChanged?.Invoke(peak);
    }

    /// <summary>Stops capture and returns everything recorded so far.</summary>
    public float[] Stop()
    {
        var w = _waveIn;
        _waveIn = null;
        IsRecording = false;
        if (w != null)
        {
            try { w.DataAvailable -= OnData; w.StopRecording(); } catch { }
            try { w.Dispose(); } catch { }
        }
        lock (_lock)
        {
            var result = _samples.ToArray();
            _samples = new List<float>();
            return result;
        }
    }

    public void Dispose() => Stop();
}
