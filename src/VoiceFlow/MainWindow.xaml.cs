using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VoiceFlow.Core;

namespace VoiceFlow;

public partial class MainWindow : Window
{
    private static readonly (string Code, string Name)[] Languages =
    {
        ("auto", "Auto-detect (100+ languages)"),
        ("en", "English"), ("es", "Spanish"), ("fr", "French"), ("de", "German"),
        ("it", "Italian"), ("pt", "Portuguese"), ("nl", "Dutch"), ("pl", "Polish"),
        ("uk", "Ukrainian"), ("ru", "Russian"), ("tr", "Turkish"), ("ar", "Arabic"),
        ("hi", "Hindi"), ("zh", "Chinese"), ("ja", "Japanese"), ("ko", "Korean"),
        ("sv", "Swedish"), ("da", "Danish"), ("no", "Norwegian"), ("fi", "Finnish"),
    };

    private bool _downloading;

    public MainWindow()
    {
        InitializeComponent();
        PopulateSettingsUi();
        LoadSettingsToUi();
        RefreshHistory();
        RefreshStats();
        RefreshDictionary();
        RefreshSnippets();
        UpdateModelCard();
        UpdateHotkeyHint();
        UpdateStatus();

        Services.History.Changed += () => Dispatcher.BeginInvoke(() => { RefreshHistory(); RefreshStats(); });
        Services.Controller.StateChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Close button hides to tray; the app keeps running (quit from tray).
        if (!((App)Application.Current).IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    // ---------- Navigation ----------

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        string tag = (string)((Button)sender).Tag;
        HomePanel.Visibility = tag == "Home" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = tag == "History" ? Visibility.Visible : Visibility.Collapsed;
        DictionaryPanel.Visibility = tag == "Dictionary" ? Visibility.Visible : Visibility.Collapsed;
        SnippetsPanel.Visibility = tag == "Snippets" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Status ----------

    private void UpdateStatus()
    {
        switch (Services.Controller.State)
        {
            case DictationState.Recording:
                StatusDot.Fill = (Brush)Application.Current.Resources["RecordBrush"];
                StatusText.Text = "Listening…";
                break;
            case DictationState.Processing:
                StatusDot.Fill = (Brush)Application.Current.Resources["AccentBrush"];
                StatusText.Text = "Transcribing…";
                break;
            default:
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x6B, 0xCB, 0x77));
                StatusText.Text = "Ready";
                break;
        }
    }

    private void UpdateHotkeyHint()
    {
        string label = KeyboardHook.LabelFor(Services.Settings.Current.HotkeyPreset);
        HotkeyHint.Text =
            $"Hold {label} and speak — release it and your words appear, polished, in any app. " +
            "Quick-tap for hands-free mode (press again to finish). Esc cancels.";
    }

    // ---------- Home / model ----------

    private void UpdateModelCard()
    {
        var s = Services.Settings.Current;
        var info = ModelDownloader.Get(s.ModelSize);
        if (s.Engine == "cloud")
        {
            ModelStatusText.Text = "Cloud engine selected — no local model needed.";
            DownloadModelBtn.Visibility = Visibility.Collapsed;
        }
        else if (WhisperTranscriber.ModelExists(s.ModelSize))
        {
            ModelStatusText.Text = $"✓ {info?.Label ?? s.ModelSize} is installed. Dictation is ready — everything runs on this PC.";
            DownloadModelBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            ModelStatusText.Text = $"The speech model isn't downloaded yet. One download (~{(info?.ApproxBytes ?? 0) / 1_000_000} MB) and BONGA works offline forever.";
            DownloadModelBtn.Content = "Download " + (info?.Label ?? s.ModelSize);
            DownloadModelBtn.Visibility = Visibility.Visible;
        }
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading) return;
        _downloading = true;
        DownloadModelBtn.IsEnabled = false;
        ModelProgress.Visibility = Visibility.Visible;
        string size = Services.Settings.Current.ModelSize;
        try
        {
            var progress = new Progress<double>(p =>
            {
                ModelProgress.Value = p;
                ModelStatusText.Text = $"Downloading {size} model… {p:P0}";
            });
            await ModelDownloader.DownloadAsync(size, progress, CancellationToken.None);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Download failed: " + ex.Message, "BONGA");
        }
        finally
        {
            _downloading = false;
            DownloadModelBtn.IsEnabled = true;
            ModelProgress.Visibility = Visibility.Collapsed;
            UpdateModelCard();
            Services.Controller.Preload();
        }
    }

    private void RefreshStats()
    {
        var (dictations, words, minutes) = Services.History.Stats();
        StatDictations.Text = dictations.ToString("N0");
        StatWords.Text = words.ToString("N0");
        StatSaved.Text = minutes >= 90 ? $"{minutes / 60:0.#} h" : $"{minutes:0} min";
    }

    // ---------- History ----------

    private void RefreshHistory()
    {
        var entries = Services.History.Entries.ToList();
        HistoryList.ItemsSource = entries;
        HistoryEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopyHistory_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is HistoryEntry entry)
        {
            try { Clipboard.SetDataObject(entry.Text, true); } catch { }
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Delete all dictation history?", "BONGA",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Services.History.Clear();
    }

    // ---------- Dictionary ----------

    private void RefreshDictionary()
    {
        var s = Services.Settings.Current;
        WordsList.ItemsSource = s.DictionaryWords.ToList();
        RepsList.ItemsSource = s.Replacements.ToList();
    }

    private void AddWord_Click(object sender, RoutedEventArgs e) => AddWord();

    private void NewWordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddWord();
    }

    private void AddWord()
    {
        string word = NewWordBox.Text.Trim();
        if (word.Length == 0) return;
        var s = Services.Settings.Current;
        if (!s.DictionaryWords.Contains(word, StringComparer.OrdinalIgnoreCase))
        {
            s.DictionaryWords.Add(word);
            Services.Settings.Save();
        }
        NewWordBox.Clear();
        RefreshDictionary();
    }

    private void RemoveWord_Click(object sender, RoutedEventArgs e)
    {
        if (WordsList.SelectedItem is string word)
        {
            Services.Settings.Current.DictionaryWords.RemoveAll(w => w == word);
            Services.Settings.Save();
            RefreshDictionary();
        }
    }

    private void AddRep_Click(object sender, RoutedEventArgs e)
    {
        string from = RepFromBox.Text.Trim(), to = RepToBox.Text.Trim();
        if (from.Length == 0 || to.Length == 0) return;
        Services.Settings.Current.Replacements.Add(new ReplacementEntry { From = from, To = to });
        Services.Settings.Save();
        RepFromBox.Clear();
        RepToBox.Clear();
        RefreshDictionary();
    }

    private void RemoveRep_Click(object sender, RoutedEventArgs e)
    {
        if (RepsList.SelectedItem is ReplacementEntry entry)
        {
            Services.Settings.Current.Replacements.Remove(entry);
            Services.Settings.Save();
            RefreshDictionary();
        }
    }

    // ---------- Snippets ----------

    private void RefreshSnippets()
    {
        SnippetsList.ItemsSource = Services.Settings.Current.Snippets.ToList();
    }

    private void AddSnippet_Click(object sender, RoutedEventArgs e)
    {
        string trigger = SnipTriggerBox.Text.Trim(), expansion = SnipExpansionBox.Text;
        if (trigger.Length == 0 || expansion.Trim().Length == 0) return;
        Services.Settings.Current.Snippets.Add(new SnippetEntry { Trigger = trigger, Expansion = expansion });
        Services.Settings.Save();
        SnipTriggerBox.Clear();
        SnipExpansionBox.Clear();
        RefreshSnippets();
    }

    private void RemoveSnippet_Click(object sender, RoutedEventArgs e)
    {
        if (SnippetsList.SelectedItem is SnippetEntry entry)
        {
            Services.Settings.Current.Snippets.Remove(entry);
            Services.Settings.Save();
            RefreshSnippets();
        }
    }

    // ---------- Settings ----------

    private void PopulateSettingsUi()
    {
        foreach (var p in KeyboardHook.Presets)
            HotkeyCombo.Items.Add(new ComboBoxItem { Content = p.Label, Tag = p.Name });

        foreach (var (number, name) in AudioRecorder.ListDevices())
            MicCombo.Items.Add(new ComboBoxItem { Content = name, Tag = number.ToString() });

        foreach (var (code, name) in Languages)
            LangCombo.Items.Add(new ComboBoxItem { Content = name, Tag = code });

        foreach (var m in ModelDownloader.Models)
        {
            string suffix = WhisperTranscriber.ModelExists(m.Size) ? "  ✓ installed" : "";
            ModelCombo.Items.Add(new ComboBoxItem { Content = m.Label + suffix, Tag = m.Size });
        }

        EngineCombo.Items.Add(new ComboBoxItem { Content = "On-device (private, works offline)", Tag = "local" });
        EngineCombo.Items.Add(new ComboBoxItem { Content = "Cloud API (OpenAI-compatible)", Tag = "cloud" });

        InsertCombo.Items.Add(new ComboBoxItem { Content = "Paste (fast, recommended)", Tag = "paste" });
        InsertCombo.Items.Add(new ComboBoxItem { Content = "Type keystrokes (for terminals)", Tag = "type" });
    }

    private void LoadSettingsToUi()
    {
        var s = Services.Settings.Current;
        SelectByTag(HotkeyCombo, s.HotkeyPreset);
        SelectByTag(MicCombo, s.MicDeviceNumber.ToString());
        SelectByTag(LangCombo, s.Language);
        SelectByTag(ModelCombo, s.ModelSize);
        SelectByTag(EngineCombo, s.Engine);
        SelectByTag(InsertCombo, s.InsertionMode);
        FillersCheck.IsChecked = s.RemoveFillers;
        CommandsCheck.IsChecked = s.VoiceCommands;
        CapsCheck.IsChecked = s.AutoCapitalize;
        PunctCheck.IsChecked = s.AutoPunctuate;
        FlowBarCheck.IsChecked = s.ShowFlowBar;
        SoundCheck.IsChecked = s.SoundFeedback;
        StartupCheck.IsChecked = s.LaunchAtStartup;
        ApiBaseBox.Text = s.CloudApiBase;
        ApiKeyBox.Text = s.CloudApiKey;
        SttModelBox.Text = s.CloudSttModel;
        PolishCheck.IsChecked = s.AiPolish;
        PolishModelBox.Text = s.AiPolishModel;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var s = Services.Settings.Current;
        s.HotkeyPreset = GetTag(HotkeyCombo, s.HotkeyPreset);
        s.MicDeviceNumber = int.TryParse(GetTag(MicCombo, "-1"), out int mic) ? mic : -1;
        s.Language = GetTag(LangCombo, "auto");
        s.ModelSize = GetTag(ModelCombo, "base");
        s.Engine = GetTag(EngineCombo, "local");
        s.InsertionMode = GetTag(InsertCombo, "paste");
        s.RemoveFillers = FillersCheck.IsChecked == true;
        s.VoiceCommands = CommandsCheck.IsChecked == true;
        s.AutoCapitalize = CapsCheck.IsChecked == true;
        s.AutoPunctuate = PunctCheck.IsChecked == true;
        s.ShowFlowBar = FlowBarCheck.IsChecked == true;
        s.SoundFeedback = SoundCheck.IsChecked == true;
        s.LaunchAtStartup = StartupCheck.IsChecked == true;
        s.CloudApiBase = ApiBaseBox.Text.Trim();
        s.CloudApiKey = ApiKeyBox.Text.Trim();
        s.CloudSttModel = SttModelBox.Text.Trim();
        s.AiPolish = PolishCheck.IsChecked == true;
        s.AiPolishModel = PolishModelBox.Text.Trim();

        Services.Settings.Save();
        Services.Hook.ApplyPreset(s.HotkeyPreset);
        StartupManager.Set(s.LaunchAtStartup);
        UpdateModelCard();
        UpdateHotkeyHint();

        SavedHint.Visibility = Visibility.Visible;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { t.Stop(); SavedHint.Visibility = Visibility.Collapsed; };
        t.Start();
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if ((string)item.Tag == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string GetTag(ComboBox combo, string fallback) =>
        combo.SelectedItem is ComboBoxItem item && item.Tag is string tag ? tag : fallback;
}
