using Microsoft.Win32;

namespace VoiceFlow.Core;

/// <summary>Toggles "launch at startup" via the HKCU Run registry key.</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Bonga";

    public static void Set(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (enable && Environment.ProcessPath is string exe)
                key.SetValue(ValueName, $"\"{exe}\"");
            else if (!enable)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { }
    }
}
