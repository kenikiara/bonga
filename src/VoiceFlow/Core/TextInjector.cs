using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace VoiceFlow.Core;

/// <summary>
/// Inserts text into whatever application currently has keyboard focus,
/// either by clipboard paste (fast, reliable for long text — clipboard is
/// restored afterwards) or by synthesizing Unicode key events.
/// </summary>
public static class TextInjector
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const ushort VK_RETURN = 0x0D;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>Friendly name of the current foreground app (for history).</summary>
    public static string GetForegroundApp()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "";
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0)
            {
                using var proc = Process.GetProcessById((int)pid);
                string name = proc.ProcessName;
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            var sb = new StringBuilder(128);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return ""; }
    }

    /// <summary>Blocking; call from a background thread.</summary>
    public static void Insert(string text, string mode)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (mode == "type")
            TypeText(text);
        else
            PasteText(text);
    }

    private static void PasteText(string text)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        string? saved = null;
        dispatcher.Invoke(() =>
        {
            try
            {
                if (Clipboard.ContainsText()) saved = Clipboard.GetText();
                Clipboard.SetDataObject(text, true);
            }
            catch { }
        });

        Thread.Sleep(80);
        SendKeyCombo();
        Thread.Sleep(400); // give the target app time to read the clipboard

        if (saved != null)
        {
            dispatcher.Invoke(() =>
            {
                try { Clipboard.SetDataObject(saved, true); } catch { }
            });
        }
    }

    private static void SendKeyCombo()
    {
        var inputs = new INPUT[4];
        inputs[0] = Key(VK_CONTROL, false);
        inputs[1] = Key(VK_V, false);
        inputs[2] = Key(VK_V, true);
        inputs[3] = Key(VK_CONTROL, true);
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void TypeText(string text)
    {
        var batch = new List<INPUT>(128);
        foreach (char c in text)
        {
            if (c == '\r') continue;
            if (c == '\n')
            {
                batch.Add(Key(VK_RETURN, false));
                batch.Add(Key(VK_RETURN, true));
            }
            else
            {
                batch.Add(Unicode(c, false));
                batch.Add(Unicode(c, true));
            }

            if (batch.Count >= 100)
            {
                Flush(batch);
                Thread.Sleep(10);
            }
        }
        Flush(batch);
    }

    private static void Flush(List<INPUT> batch)
    {
        if (batch.Count == 0) return;
        SendInput((uint)batch.Count, batch.ToArray(), Marshal.SizeOf<INPUT>());
        batch.Clear();
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } }
    };

    private static INPUT Unicode(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0)
            }
        }
    };
}
