using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using VoiceFlow.Core;

namespace VoiceFlow;

/// <summary>
/// The "Flow Bar": a small always-on-top pill at the bottom-center of the
/// screen. Shows live waveform bars while recording, a processing state,
/// and brief result/error flashes. Never steals focus (WS_EX_NOACTIVATE).
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly List<Rectangle> _bars = new();
    private readonly DispatcherTimer _animTimer;
    private readonly DispatcherTimer _flashTimer;
    private volatile float _level;
    private float _smoothLevel;
    private double _phase;
    private DateTime _recordStart;

    public OverlayWindow()
    {
        InitializeComponent();

        var accent = (Brush)Application.Current.Resources["AccentBrush"];
        for (int i = 0; i < 14; i++)
        {
            var r = new Rectangle
            {
                Width = 3,
                Height = 4,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Margin = new Thickness(1.5, 0, 1.5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = accent
            };
            _bars.Add(r);
            BarsPanel.Children.Add(r);
        }

        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _animTimer.Tick += (_, _) => AnimateBars();

        _flashTimer = new DispatcherTimer();
        _flashTimer.Tick += (_, _) => { _flashTimer.Stop(); UpdateState(); };

        var c = Services.Controller;
        c.StateChanged += () => Dispatcher.BeginInvoke(UpdateState);
        c.LevelChanged += l => _level = l;                      // audio thread; timer reads it
        c.Inserted += e => Dispatcher.BeginInvoke(() =>
            Flash($"✓ {e.Words} word{(e.Words == 1 ? "" : "s")} inserted", "#FF9AF0B4", 1400));
        c.Error += msg => Dispatcher.BeginInvoke(() =>
            Flash("⚠ " + (msg.Length > 90 ? msg[..90] + "…" : msg), "#FFFF8C9E", 3200));

        Services.Settings.Changed += () => Dispatcher.BeginInvoke(UpdateState);

        SizeChanged += (_, _) => Reposition();
        Loaded += (_, _) => UpdateState();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE,
            GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private void UpdateState()
    {
        var state = Services.Controller.State;
        bool flashActive = _flashTimer.IsEnabled;

        IdlePanel.Visibility = Visibility.Collapsed;
        RecordPanel.Visibility = Visibility.Collapsed;
        ProcessingPanel.Visibility = Visibility.Collapsed;
        if (!flashActive) FlashText.Visibility = Visibility.Collapsed;

        switch (state)
        {
            case DictationState.Recording:
                RecordPanel.Visibility = Visibility.Visible;
                Pill.BorderBrush = (Brush)Application.Current.Resources["RecordBrush"];
                _recordStart = DateTime.Now;
                _smoothLevel = 0;
                if (!_animTimer.IsEnabled) _animTimer.Start();
                ShowBar();
                break;

            case DictationState.Processing:
                _animTimer.Stop();
                ProcessingPanel.Visibility = Visibility.Visible;
                Pill.BorderBrush = (Brush)Application.Current.Resources["AccentBrush"];
                ShowBar();
                break;

            default: // Idle
                _animTimer.Stop();
                Pill.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
                if (flashActive)
                {
                    ShowBar();
                }
                else if (Services.Settings.Current.ShowFlowBar)
                {
                    IdlePanel.Visibility = Visibility.Visible;
                    IdleText.Text = "BONGA — " + KeyboardHook.LabelFor(Services.Settings.Current.HotkeyPreset);
                    ShowBar();
                }
                else
                {
                    Hide();
                }
                break;
        }
        Reposition();
    }

    private void ShowBar()
    {
        if (!IsVisible) Show();
        Reposition();
    }

    private void Flash(string text, string colorHex, int ms)
    {
        FlashText.Text = text;
        FlashText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
        IdlePanel.Visibility = Visibility.Collapsed;
        RecordPanel.Visibility = Visibility.Collapsed;
        ProcessingPanel.Visibility = Visibility.Collapsed;
        FlashText.Visibility = Visibility.Visible;
        ShowBar();
        _flashTimer.Stop();
        _flashTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _flashTimer.Start();
    }

    private void AnimateBars()
    {
        _phase += 0.45;
        float target = _level;
        _smoothLevel = Math.Max(target, _smoothLevel * 0.82f);
        double amp = Math.Min(1.0, _smoothLevel * 3.2); // mic peaks are usually well under 1.0

        for (int i = 0; i < _bars.Count; i++)
        {
            double wave = 0.3 + 0.7 * Math.Abs(Math.Sin(_phase + i * 0.65));
            _bars[i].Height = 4 + 22 * amp * wave;
        }

        var elapsed = DateTime.Now - _recordStart;
        TimerText.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    }

    private void Reposition()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth) / 2;
        Top = wa.Bottom - ActualHeight - 14;
    }

    private async void Pill_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var c = Services.Controller;
        if (c.State == DictationState.Idle)
            c.StartRecording(handsFree: true);
        else if (c.State == DictationState.Recording)
            await c.StopAndInsertAsync();
    }

    // The "✕" on the idle pill: dismiss the Flow Bar so it isn't always on
    // screen. It still pops up while you dictate; re-enable the resting bar
    // from the tray menu or Settings. The Button swallows the mouse press, so
    // this never falls through to Pill_MouseDown and starts a recording.
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Services.Settings.Current.ShowFlowBar = false;
        Services.Settings.Save();   // fires Changed -> UpdateState -> Hide()
    }
}
