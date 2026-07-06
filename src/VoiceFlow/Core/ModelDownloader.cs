using System.IO;
using System.Net.Http;

namespace VoiceFlow.Core;

/// <summary>Downloads Whisper GGML models from Hugging Face into the models dir.</summary>
public static class ModelDownloader
{
    public record ModelInfo(string Size, string Label, string Url, long ApproxBytes);

    public static readonly ModelInfo[] Models =
    {
        new("tiny",        "Tiny — fastest, lower accuracy (~75 MB)",
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin", 77_691_713),
        new("base-q5_1",   "Base quantized — fast, recommended (~60 MB)",
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base-q5_1.bin", 60_294_150),
        new("base",        "Base — balanced (~142 MB)",
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin", 147_951_465),
        new("small-q5_1",  "Small quantized — high quality (~190 MB)",
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin", 190_085_487),
        new("small",       "Small — best quality, slowest (~466 MB)",
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin", 487_601_967),
    };

    public static ModelInfo? Get(string size) => Models.FirstOrDefault(m => m.Size == size);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public static async Task DownloadAsync(string size, IProgress<double>? progress, CancellationToken ct)
    {
        var info = Get(size) ?? throw new ArgumentException("Unknown model: " + size);
        string finalPath = WhisperTranscriber.ModelPath(size);
        if (File.Exists(finalPath)) return;

        string tmpPath = finalPath + ".tmp";
        using var resp = await Http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? info.ApproxBytes;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmpPath))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress?.Report(Math.Min(1.0, done / (double)total));
            }
        }

        File.Move(tmpPath, finalPath, overwrite: true);
        progress?.Report(1.0);
    }
}
