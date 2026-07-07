using System.Text;
using VoiceFlow.Core;
using Whisper.net;

// End-to-end pipeline check: WAV -> Whisper -> TextFormatter, plus formatter unit checks.
// Usage: SelfTest <model.bin> <audio.wav>

int failures = 0;

void Check(string name, string actual, string expected)
{
    bool ok = actual == expected;
    if (!ok) failures++;
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
    if (!ok) Console.WriteLine($"         expected: \"{expected}\"\n         actual:   \"{actual}\"");
}

Console.WriteLine("== Formatter unit checks ==");
var fs = new AppSettings();
fs.Replacements.Add(new ReplacementEntry { From = "voice flow", To = "VoiceFlow" });
fs.Snippets.Add(new SnippetEntry { Trigger = "insert my email", Expansion = "me@example.com" });

Check("filler removal + capitalize + punctuation",
    TextFormatter.Apply("um so this is uh a test of the system", fs),
    "So this is a test of the system.");
Check("snippet trigger",
    TextFormatter.Apply("Insert my email.", fs),
    "me@example.com");
Check("voice command new line",
    TextFormatter.Apply("first item new line second item", fs),
    "First item\nSecond item.");
Check("dictionary replacement",
    TextFormatter.Apply("i really like voice flow for dictation", fs),
    "I really like VoiceFlow for dictation.");
Check("duplicate word collapse",
    TextFormatter.Apply("the the meeting is is at noon", fs),
    "The meeting is at noon.");

Console.WriteLine("\n== Settings / AI cleanup provider ==");
var defaults = new AppSettings();
Check("default polish provider", defaults.PolishProvider, "anthropic");
Check("default polish model", defaults.AiPolishModel, "claude-haiku-4-5");
string settingsJson = System.Text.Json.JsonSerializer.Serialize(defaults);
bool hasPolishFields = settingsJson.Contains("PolishProvider") &&
                       settingsJson.Contains("PolishApiKey") &&
                       settingsJson.Contains("AiPolishModel");
Console.WriteLine($"  [{(hasPolishFields ? "PASS" : "FAIL")}] settings persist polish provider/key/model");
if (!hasPolishFields) failures++;

if (args.Length < 2)
{
    Console.WriteLine("No model/wav supplied — skipping Whisper test.");
    return failures;
}

Console.WriteLine("\n== Whisper end-to-end ==");
string modelPath = args[0], wavPath = args[1];
float[] samples = ReadWavMono16(wavPath);
Console.WriteLine($"Audio: {samples.Length / 16000.0:0.0}s");

// Mirrors the app's pipeline: preloaded factory + per-clip encoder context.
var sw = System.Diagnostics.Stopwatch.StartNew();
using var factory = WhisperFactory.FromPath(modelPath);
Console.WriteLine($"Model load (paid once at app startup): {sw.Elapsed.TotalSeconds:0.0}s");

int audioCtx = Math.Clamp((int)(samples.Length / 16000.0 / 30.0 * 1500) + 128, 256, 1500);
sw.Restart();
await using var processor = factory.CreateBuilder()
    .WithLanguage("en")
    .WithThreads(Math.Clamp(Environment.ProcessorCount, 1, 8))
    .WithAudioContextSize(audioCtx)
    .Build();

var sb = new StringBuilder();
await foreach (var seg in processor.ProcessAsync(samples))
    sb.Append(seg.Text);
sw.Stop();

string raw = sb.ToString();
Console.WriteLine($"Transcribed in {sw.Elapsed.TotalSeconds:0.0}s (audio_ctx {audioCtx}/1500, {Environment.ProcessorCount} threads)");
Console.WriteLine("RAW:       " + raw.Trim());
string formatted = TextFormatter.Apply(raw, new AppSettings());
Console.WriteLine("FORMATTED: " + formatted);

bool mentionsHello = formatted.Contains("hello", StringComparison.OrdinalIgnoreCase);
bool mentionsTest = formatted.Contains("test", StringComparison.OrdinalIgnoreCase);
bool noFiller = !System.Text.RegularExpressions.Regex.IsMatch(formatted, @"\bum\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
Console.WriteLine($"  [{(mentionsHello ? "PASS" : "FAIL")}] contains 'hello'");
Console.WriteLine($"  [{(mentionsTest ? "PASS" : "FAIL")}] contains 'test'");
Console.WriteLine($"  [{(noFiller ? "PASS" : "FAIL")}] fillers removed");
if (!mentionsHello || !mentionsTest || !noFiller) failures++;

Console.WriteLine(failures == 0 ? "\nALL CHECKS PASSED" : $"\n{failures} CHECK(S) FAILED");
return failures;

static float[] ReadWavMono16(string path)
{
    byte[] bytes = File.ReadAllBytes(path);
    // find the "data" chunk (skips fmt/LIST chunks)
    int pos = 12;
    while (pos + 8 <= bytes.Length)
    {
        string id = Encoding.ASCII.GetString(bytes, pos, 4);
        int size = BitConverter.ToInt32(bytes, pos + 4);
        if (id == "data")
        {
            int count = size / 2;
            var samples = new float[count];
            for (int i = 0; i < count; i++)
                samples[i] = BitConverter.ToInt16(bytes, pos + 8 + i * 2) / 32768f;
            return samples;
        }
        pos += 8 + size + (size % 2);
    }
    throw new InvalidDataException("No data chunk in wav");
}
