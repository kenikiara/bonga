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
    private WF.ToolStripMenuItem? _trayFlowBarItem;
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
        _iconIdle = MakeIcon(SD.Color.FromArgb(0x00, 0x6F, 0xFF));  // brand blue
        _iconRec = MakeIcon(SD.Color.FromArgb(0xE8, 0x18, 0x2A));   // recording red
        _iconProc = MakeIcon(SD.Color.FromArgb(0xFB, 0xBC, 0x00));  // transcribing gold

        var menu = new WF.ContextMenuStrip();
        var openItem = new WF.ToolStripMenuItem("Open BONGA");
        openItem.Font = new SD.Font(openItem.Font, SD.FontStyle.Bold);
        openItem.Click += (_, _) => ShowMain();
        menu.Items.Add(openItem);

        _trayDictateItem = new WF.ToolStripMenuItem("Start hands-free dictation");
        _trayDictateItem.Click += (_, _) => ToggleDictation();
        menu.Items.Add(_trayDictateItem);

        var flowBarItem = new WF.ToolStripMenuItem("Show Flow Bar")
        {
            CheckOnClick = true,
            Checked = Services.Settings.Current.ShowFlowBar
        };
        flowBarItem.CheckedChanged += (_, _) =>
        {
            if (Services.Settings.Current.ShowFlowBar == flowBarItem.Checked) return;
            Services.Settings.Current.ShowFlowBar = flowBarItem.Checked;
            Services.Settings.Save();
        };
        menu.Items.Add(flowBarItem);
        _trayFlowBarItem = flowBarItem;

        // Keep the tick in sync when the setting changes elsewhere (the pill's
        // ✕ button or the Settings dashboard).
        Services.Settings.Changed += () =>
        {
            if (_trayFlowBarItem != null &&
                _trayFlowBarItem.Checked != Services.Settings.Current.ShowFlowBar)
                _trayFlowBarItem.Checked = Services.Settings.Current.ShowFlowBar;
        };

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

    internal void QuitApp()
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

    /// <summary>
    /// Draws the tray badge at runtime: a rounded square in the state colour with a white
    /// microphone. Colour signals status (blue idle / red recording / gold transcribing).
    /// </summary>
    private static SD.Icon MakeIcon(SD.Color color)
    {
        using var bmp = new SD.Bitmap(32, 32);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            using (var bg = new SD.SolidBrush(color))
            using (var path = RoundRect(1, 1, 30, 30, 7))
                g.FillPath(bg, path);

            using var w = new SD.SolidBrush(SD.Color.White);
            using var pen = new SD.Pen(SD.Color.White, 2.4f);
            using (var cap = RoundRect(12, 6, 8, 12, 4))
                g.FillPath(w, cap);                 // mic capsule
            g.DrawArc(pen, 9, 12, 14, 13, 20, 140); // cradle
            g.DrawLine(pen, 16, 24, 16, 27);        // stem
            g.DrawLine(pen, 12, 28, 20, 28);        // base
        }
        return SD.Icon.FromHandle(bmp.GetHicon());
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
