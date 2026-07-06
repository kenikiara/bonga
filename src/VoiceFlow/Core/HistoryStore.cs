using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceFlow.Core;

public class HistoryEntry
{
    public DateTime Timestamp { get; set; }
    public string Text { get; set; } = "";
    public string App { get; set; } = "";
    public double DurationSeconds { get; set; }
    public int Words { get; set; }

    [JsonIgnore]
    public string Meta =>
        $"{Timestamp:g}{(string.IsNullOrEmpty(App) ? "" : "  ·  " + App)}  ·  {Words} word{(Words == 1 ? "" : "s")}";
}

/// <summary>Persists past dictations (capped) and derives usage stats.</summary>
public class HistoryStore
{
    private const int MaxEntries = 500;
    private readonly string _path;
    private readonly object _lock = new();

    public List<HistoryEntry> Entries { get; private set; } = new();

    public event Action? Changed;

    public HistoryStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(_path))
                Entries = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_path)) ?? new();
        }
        catch { Entries = new(); }
    }

    public void Add(HistoryEntry entry)
    {
        lock (_lock)
        {
            Entries.Insert(0, entry);
            if (Entries.Count > MaxEntries)
                Entries.RemoveRange(MaxEntries, Entries.Count - MaxEntries);
            Save();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock) { Entries.Clear(); Save(); }
        Changed?.Invoke();
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(Entries)); } catch { }
    }

    public (int Dictations, int Words, double MinutesSaved) Stats()
    {
        int dictations, words;
        lock (_lock)
        {
            dictations = Entries.Count;
            words = Entries.Sum(e => e.Words);
        }
        // Typing at ~40 wpm vs speaking at ~150 wpm
        double minutesSaved = Math.Max(0, words / 40.0 - words / 150.0);
        return (dictations, words, minutesSaved);
    }
}
