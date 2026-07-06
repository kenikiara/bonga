using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace VoiceFlow.Core;

/// <summary>
/// Low-level keyboard hook implementing Wispr-Flow-style trigger semantics:
///  - Hold the trigger key -> push-to-talk (release to stop and transcribe)
///  - Quick tap             -> toggle hands-free dictation
///  - Esc while recording   -> cancel
///  - For bare-modifier triggers (Right Ctrl / Right Alt), pressing any other
///    key while holding cancels dictation so normal shortcuts still work.
/// Raises high-level events marshalled onto the UI dispatcher.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_ESCAPE = 0x1B;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>Known trigger presets: display name -> virtual key code.</summary>
    public static readonly (string Name, string Label, int Vk, bool IsModifier)[] Presets =
    {
        ("RightCtrl",  "Right Ctrl (hold to talk)", 0xA3, true),
        ("RightAlt",   "Right Alt (hold to talk)",  0xA5, true),
        ("F8",         "F8",                        0x77, false),
        ("F9",         "F9",                        0x78, false),
        ("Pause",      "Pause / Break",             0x13, false),
        ("ScrollLock", "Scroll Lock",               0x91, false),
        ("CapsLock",   "Caps Lock",                 0x14, false),
    };

    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _proc;   // kept referenced so the GC never collects it
    private Dispatcher? _dispatcher;

    private int _triggerVk = 0xA3;
    private bool _triggerIsModifier = true;
    private volatile bool _triggerHeld;
    private long _downTimestamp;

    /// <summary>Provider set by the app so the hook knows whether dictation is active.</summary>
    public Func<bool> IsRecording { get; set; } = () => false;

    /// <summary>Set false to disable the trigger (e.g. while user is re-binding).</summary>
    public volatile bool Enabled = true;

    public event Action? TriggerDown;
    public event Action<double>? TriggerUp;   // arg = hold duration in ms
    public event Action? CancelRequested;

    public void Start()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        ApplyPreset(Services.Settings.Current.HotkeyPreset);
        _proc = HookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
        if (_hookId == IntPtr.Zero)
            throw new InvalidOperationException("Failed to install keyboard hook: " + Marshal.GetLastWin32Error());
    }

    public void ApplyPreset(string presetName)
    {
        foreach (var p in Presets)
        {
            if (p.Name == presetName)
            {
                _triggerVk = p.Vk;
                _triggerIsModifier = p.IsModifier;
                return;
            }
        }
        // Unknown -> default
        _triggerVk = 0xA3;
        _triggerIsModifier = true;
    }

    public static string LabelFor(string presetName)
    {
        foreach (var p in Presets)
            if (p.Name == presetName) return p.Label;
        return presetName;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || !Enabled)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        int vk = (int)info.vkCode;
        int msg = (int)wParam;
        bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

        // Esc cancels an active dictation
        if (vk == VK_ESCAPE && isDown && IsRecording())
        {
            Post(() => CancelRequested?.Invoke());
            return (IntPtr)1; // swallow
        }

        if (vk == _triggerVk)
        {
            if (isDown && !_triggerHeld)
            {
                _triggerHeld = true;
                _downTimestamp = Environment.TickCount64;
                Post(() => TriggerDown?.Invoke());
            }
            else if (isUp && _triggerHeld)
            {
                _triggerHeld = false;
                double heldMs = Environment.TickCount64 - _downTimestamp;
                Post(() => TriggerUp?.Invoke(heldMs));
            }

            // Swallow dedicated keys (F8, CapsLock...) so they don't reach apps;
            // let real modifiers pass through so shortcuts keep working.
            if (!_triggerIsModifier)
                return (IntPtr)1;
        }
        else if (_triggerHeld && _triggerIsModifier && isDown)
        {
            // User is doing a normal shortcut (e.g. Ctrl+C) with the trigger
            // modifier held -> abort dictation and let the shortcut through.
            _triggerHeld = false;
            Post(() => CancelRequested?.Invoke());
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void Post(Action action)
    {
        try { _dispatcher?.BeginInvoke(action); } catch { }
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }
}
