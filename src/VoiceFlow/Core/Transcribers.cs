using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Whisper.net;

namespace VoiceFlow.Core;

/// <summary>
/// On-device transcription via whisper.cpp (Whisper.net bindings).
/// The factory (loaded model) is cached and preloaded at startup; a processor
/// is built per clip so the encoder context can be sized to the clip length —
/// whisper.cpp otherwise pads every clip to a full 30 s window, which is the
/// main reason short dictations feel slow on CPU.
/// </summary>
public sealed class WhisperTranscriber : IDisposable
{
    private WhisperFactory? _factory;
    private string? _factoryPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static string ModelPath(string size) =>
        Path.Combine(Services.ModelsDir, $"ggml-{size}.bin");

    public static bool ModelExists(string size) => File.Exists(ModelPath(size));

    /// <summary>Loads the model up front so the first dictation doesn't pay for it.</summary>
    public async Task PreloadAsync()
    {
        await _gate.WaitAsync();
        try { EnsureFactory(); }
        finally { _gate.Release(); }
    }

    private void EnsureFactory()
    {
        string modelPath = ModelPath(Services.Settings.Current.ModelSize);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Speech model missing. Open BONGA and download it from the Home screen.");
        if (_factory == null || _factoryPath != modelPath)
        {
            _factory?.Dispose();
            _factory = WhisperFactory.FromPath(modelPath);
            _factoryPath = modelPath;
        }
    }

    /// <summary>Encoder frames needed for this clip (50 frames/s, 30 s = 1500) plus safety margin.</summary>
    public static int AudioContextFor(int sampleCount)
    {
        double seconds = sampleCount / (double)AudioRecorder.SampleRate;
        return Math.Clamp((int)(seconds / 30.0 * 1500) + 128, 256, 1500);
    }

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct)
    {
        var s = Services.Settings.Current;
        string prompt = s.DictionaryWords.Count > 0 ? string.Join(", ", s.DictionaryWords) : "";

        await _gate.WaitAsync(ct);
        try
        {
            EnsureFactory();
            var builder = _factory!.CreateBuilder()
                .WithLanguage(string.IsNullOrWhiteSpace(s.Language) ? "auto" : s.Language)
                .WithThreads(Math.Clamp(Environment.ProcessorCount, 1, 8))
                .WithAudioContextSize(AudioContextFor(samples.Length));
            if (prompt.Length > 0)
                builder = builder.WithPrompt("Glossary: " + prompt + ".");

            await using var processor = builder.Build();
            var sb = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, ct))
                sb.Append(segment.Text);
            return sb.ToString();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _factory?.Dispose();
    }
}

/// <summary>Optional cloud engine: any OpenAI-compatible /audio/transcriptions endpoint.</summary>
public static class CloudTranscriber
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public static async Task<string> TranscribeAsync(float[] samples, AppSettings s)
    {
        byte[] wav = BuildWav(samples);
        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "audio.wav");
        form.Add(new StringContent(s.CloudSttModel), "model");
        if (!string.IsNullOrWhiteSpace(s.Language) && s.Language != "auto")
            form.Add(new StringContent(s.Language), "language");

        using var req = new HttpRequestMessage(HttpMethod.Post,
            s.CloudApiBase.TrimEnd('/') + "/audio/transcriptions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.CloudApiKey);
        req.Content = form;

        using var resp = await Http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Cloud transcription failed ({(int)resp.StatusCode}): {Truncate(body, 200)}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
    }

    /// <summary>16 kHz mono 16-bit PCM WAV from float samples.</summary>
    public static byte[] BuildWav(float[] samples)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        int dataLen = samples.Length * 2;
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataLen);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);                       // PCM
        bw.Write((short)1);                       // mono
        bw.Write(AudioRecorder.SampleRate);
        bw.Write(AudioRecorder.SampleRate * 2);   // byte rate
        bw.Write((short)2);                       // block align
        bw.Write((short)16);                      // bits
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataLen);
        foreach (var f in samples)
            bw.Write((short)Math.Clamp(f * 32767f, short.MinValue, short.MaxValue));
        bw.Flush();
        return ms.ToArray();
    }

    private static string Truncate(string s, int len) => s.Length <= len ? s : s[..len] + "…";
}

/// <summary>Optional LLM pass that polishes the formatted text (OpenAI-compatible chat API).</summary>
public static class AiPolisher
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private const string SystemPrompt =
        "You clean up dictated speech. Fix grammar and phrasing lightly, remove any remaining " +
        "filler words, keep the speaker's meaning, tone and language. Do not add content, do not " +
        "answer questions in the text, do not translate. Return ONLY the cleaned text.";

    public static async Task<string> PolishAsync(string text, AppSettings s)
    {
        var payload = new
        {
            model = s.AiPolishModel,
            temperature = 0.2,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = text }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            s.CloudApiBase.TrimEnd('/') + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.CloudApiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI polish failed ({(int)resp.StatusCode})");

        using var doc = JsonDocument.Parse(body);
        string? result = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content").GetString();
        return string.IsNullOrWhiteSpace(result) ? text : result.Trim();
    }
}
