using System.Windows;
using VoiceFlow.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace VoiceFlow;

public partial class App : Application
{
    private static Mutex? _mutex;
    private WF.NotifyIcon? _tray;
    private WF.ToolStripMenuItem? _trayDictateItem;
    private SD.Icon? _iconIdle, _iconRec, _iconProc;
    private OverlayWindow? _overlay;
    private MainWindow? _mainWindow;

    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "Bonga_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("BONGA is already running — look for it in the system tray.", "BONGA");
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show("Unexpected error: " + ex.Exception.Message, "BONGA");
            ex.Handled = true;
        };

        Services.Init();
        WireHotkeys();
        SetupTray();

        _overlay = new OverlayWindow();
        _overlay.Show();

        _mainWindow = new MainWindow();
        _mainWindow.Show();

        try
        {
            Services.Hook.Start();
        }
        catch (Exception hex)
        {
            MessageBox.Show("Could not install the global dictation hotkey: " + hex.Message, "BONGA");
        }

        Services.Controller.StateChanged += UpdateTray;

        // Load the Whisper model up front (and reload when settings change)
        // so the first dictation doesn't stall on model loading.
        Services.Controller.Preload();
        Services.Settings.Changed += () => Services.Controller.Preload();
    }

    private void WireHotkeys()
    {
        var c = Services.Controller;
        var h = Services.Hook;

        h.IsRecording = () => c.State == DictationState.Recording;

        h.TriggerDown += () =>
        {
            if (c.State == DictationState.Idle)
                c.StartRecording();
            else if (c.State == DictationState.Recording && c.HandsFree)
                _ = c.StopAndInsertAsync();
        };

        h.TriggerUp += heldMs =>
        {
            if (c.State != DictationState.Recording || c.HandsFree) return;
            if (heldMs < 350)
                c.MarkHandsFree();      // quick tap -> hands-free session
            else
                _ = c.StopAndInsertAsync();
        };

        h.CancelRequested += () => c.Cancel();
    }

    // ---------- Tray ----------

    private void SetupTray()
    {
        _iconIdle = MakeIcon(SD.Color.FromArgb(0x7C, 0x6C, 0xFF));
        _iconRec = MakeIcon(SD.Color.FromArgb(0xFF, 0x54, 0x70));
        _iconProc = MakeIcon(SD.Color.FromArgb(0xFF, 0xC2, 0x4B));

        var menu = new WF.ContextMenuStrip();
        var openItem = new WF.ToolStripMenuItem("Open BONGA");
        openItem.Font = new SD.Font(openItem.Font, SD.FontStyle.Bold);
        openItem.Click += (_, _) => ShowMain();
        menu.Items.Add(openItem);

        _trayDictateItem = new WF.ToolStripMenuItem("Start hands-free dictation");
        _trayDictateItem.Click += (_, _) => ToggleDictation();
        menu.Items.Add(_trayDictateItem);

        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Quit BONGA", null, (_, _) => QuitApp());

        _tray = new WF.NotifyIcon
        {
            Icon = _iconIdle,
            Text = "BONGA — ready",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    private void UpdateTray()
    {
        if (_tray == null) return;
        switch (Services.Controller.State)
        {
            case DictationState.Recording:
                _tray.Icon = _iconRec;
                _tray.Text = "BONGA — listening…";
                if (_trayDictateItem != null) _trayDictateItem.Text = "Stop dictation";
                break;
            case DictationState.Processing:
                _tray.Icon = _iconProc;
                _tray.Text = "BONGA — transcribing…";
                if (_trayDictateItem != null) _trayDictateItem.Text = "Transcribing…";
                break;
            default:
                _tray.Icon = _iconIdle;
                _tray.Text = "BONGA — ready";
                if (_trayDictateItem != null) _trayDictateItem.Text = "Start hands-free dictation";
                break;
        }
    }

    private void ToggleDictation()
    {
        var c = Services.Controller;
        if (c.State == DictationState.Idle)
            c.StartRecording(handsFree: true);
        else if (c.State == DictationState.Recording)
            _ = c.StopAndInsertAsync();
    }

    private void ShowMain()
    {
        _mainWindow ??= new MainWindow();
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void QuitApp()
    {
        IsExiting = true;
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        Services.ShutdownServices();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Services.ShutdownServices();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Draws the mic tray icon at runtime (no bundled assets needed).</summary>
    private static SD.Icon MakeIcon(SD.Color color)
    {
        using var bmp = new SD.Bitmap(32, 32);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SD.SolidBrush(color);
            using var p = new SD.Pen(color, 3);
            g.FillEllipse(b, 11, 2, 10, 10);      // mic capsule top
            g.FillRectangle(b, 11, 7, 10, 9);     // capsule body
            g.FillEllipse(b, 11, 12, 10, 8);      // capsule bottom
            g.DrawArc(p, 7, 8, 18, 16, 20, 140);  // cradle
            g.DrawLine(p, 16, 24, 16, 28);        // stem
            g.DrawLine(p, 11, 29, 21, 29);        // base
        }
        return SD.Icon.FromHandle(bmp.GetHicon());
    }
}
