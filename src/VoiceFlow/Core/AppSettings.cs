using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceFlow.Core;

public class SnippetEntry
{
    public string Trigger { get; set; } = "";
    public string Expansion { get; set; } = "";

    [JsonIgnore]
    public string Display =>
        $"“{Trigger}”  →  {(Expansion.Length > 60 ? Expansion[..60].Replace('\n', ' ') + "…" : Expansion.Replace('\n', ' '))}";
}

public class ReplacementEntry
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";

    [JsonIgnore]
    public string Display => $"{From}  →  {To}";
}

public class AppSettings
{
    // Hotkey preset key name: RightCtrl, RightAlt, F8, F9, Pause, ScrollLock, CapsLock
    public string HotkeyPreset { get; set; } = "RightCtrl";

    // -1 = default input device
    public int MicDeviceNumber { get; set; } = -1;

    // "auto" or ISO code like "en", "es", "de"...
    public string Language { get; set; } = "auto";

    // tiny | base-q5_1 | base | small-q5_1 | small
    public string ModelSize { get; set; } = "base-q5_1";

    // local | cloud
    public string Engine { get; set; } = "local";

    // paste | type
    public string InsertionMode { get; set; } = "paste";

    public bool RemoveFillers { get; set; } = true;
    public bool VoiceCommands { get; set; } = true;       // "new line", "new paragraph"
    public bool AutoCapitalize { get; set; } = true;
    public bool AutoPunctuate { get; set; } = true;       // ensure trailing punctuation
    public bool ShowFlowBar { get; set; } = true;
    public bool SoundFeedback { get; set; } = true;
    public bool LaunchAtStartup { get; set; } = false;

    // Personal dictionary: words/names Whisper should recognize (used as decode prompt bias)
    public List<string> DictionaryWords { get; set; } = new();

    // Hard text replacements applied after transcription (misheard -> correct)
    public List<ReplacementEntry> Replacements { get; set; } = new();

    // Voice snippets: say the trigger phrase, get the expansion inserted
    public List<SnippetEntry> Snippets { get; set; } = new();

    // Optional cloud engine + AI polish (any OpenAI-compatible endpoint)
    public string CloudApiBase { get; set; } = "https://api.openai.com/v1";
    public string CloudApiKey { get; set; } = "";
    public string CloudSttModel { get; set; } = "whisper-1";
    public bool AiPolish { get; set; } = false;
    public string AiPolishModel { get; set; } = "gpt-4o-mini";
}

public class SettingsStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public AppSettings Current { get; private set; }

    public event Action? Changed;

    public SettingsStore(string path)
    {
        _path = path;
        Current = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
        }
        catch { /* corrupted settings -> start fresh */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOpts));
        }
        catch { }
        Changed?.Invoke();
    }
}
