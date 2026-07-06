using System.IO;

namespace VoiceFlow.Core;

/// <summary>Simple service locator holding the app-wide singletons.</summary>
public static class Services
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bonga");

    public static string ModelsDir => Path.Combine(DataDir, "models");

    public static SettingsStore Settings { get; private set; } = null!;
    public static HistoryStore History { get; private set; } = null!;
    public static DictationController Controller { get; private set; } = null!;
    public static KeyboardHook Hook { get; private set; } = null!;

    public static void Init()
    {
        // Migrate data from the pre-rename "VoiceFlow" folder
        string oldDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceFlow");
        if (Directory.Exists(oldDir) && !Directory.Exists(DataDir))
        {
            try { Directory.Move(oldDir, DataDir); } catch { }
        }

        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ModelsDir);
        Settings = new SettingsStore(Path.Combine(DataDir, "settings.json"));
        History = new HistoryStore(Path.Combine(DataDir, "history.json"));
        Controller = new DictationController();
        Hook = new KeyboardHook();
    }

    public static void ShutdownServices()
    {
        try { Hook?.Dispose(); } catch { }
        try { Controller?.Dispose(); } catch { }
    }
}
