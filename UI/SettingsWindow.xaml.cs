using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using Clipt.Native;
using Clipt.Services;

namespace Clipt.UI;

public partial class SettingsWindow : Window
{
    private readonly App _app;
    private string _hotkey;
    private CancellationTokenSource? _testCts;

    public SettingsWindow(App app)
    {
        InitializeComponent();
        _app = app;
        Icon = IconFactory.AppImage;
        Logo.Source = IconFactory.AppImage;
        VersionText.Text = $"Clipt {typeof(App).Assembly.GetName().Version?.ToString(3)} · data in {AppSettings.DataDir}";

        var s = app.Settings;
        _hotkey = s.Hotkey;
        HotkeyBox.Text = s.Hotkey;
        WinVSwitch.IsChecked = s.UseWinV;
        PasteSwitch.IsChecked = s.PasteDirectly;
        StartupSwitch.IsChecked = s.StartWithWindows;
        (s.Theme switch { "Dark" => ThemeDark, "Light" => ThemeLight, _ => ThemeSystem }).IsChecked = true;

        RetentionBox.Text = s.RetentionDays.ToString();
        MaxItemsBox.Text = s.MaxItems.ToString();
        ImagesSwitch.IsChecked = s.CaptureImages;
        OcrSwitch.IsChecked = s.OcrImages;

        DetectSwitch.IsChecked = s.DetectSensitive;
        (s.SensitiveHandling == "Skip" ? SensitiveSkip : SensitiveHide).IsChecked = true;
        TtlBox.Text = s.SensitiveTtlMinutes.ToString();
        PrivateWindowsSwitch.IsChecked = s.SkipPrivateWindows;
        CaptureSwitch.IsChecked = s.HideFromScreenCapture;
        foreach (var a in s.ExcludedApps ?? []) _excluded.Add(new ExcludedRow(a));
        ExcludedList.ItemsSource = _excluded;
        UpdatePrivacyPanels();

        AiSwitch.IsChecked = s.AiEnabled;
        (s.AiProvider == "Ollama" ? ProviderOllama : ProviderClaude).IsChecked = true;
        ApiKeyBox.Password = s.AnthropicKey ?? "";
        ClaudeModelBox.Text = s.AnthropicModel;
        OllamaEndpointBox.Text = s.OllamaEndpoint;
        OllamaModelBox.Text = s.OllamaModel;
        TranslateBox.Text = s.TranslateTo;
        UpdateAiPanels();

        SourceInitialized += (_, _) =>
        {
            WindowEffects.HideCaptionButtons(this);
            ApplyTheme(app.IsDark);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !HotkeyBox.IsKeyboardFocused) Close();
        };
        Closed += (_, _) => _testCts?.Cancel();
    }

    public void ApplyTheme(bool dark)
    {
        bool backdrop = WindowEffects.Apply(this, Backdrop.Mica, dark);
        Root.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, backdrop ? "Bg.Settings" : "Bg.Fallback");
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.Tab)
        {
            if (key == Key.Tab) e.Handled = false; // allow focus navigation
            return;
        }
        var mods = Keyboard.Modifiers;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= ModifierKeys.Windows;
        if (mods == ModifierKeys.None)
        {
            HotkeyBox.Text = "Add Ctrl, Alt, Shift or Win";
            return;
        }
        var hk = new Hotkey(mods, key);
        _hotkey = hk.ToString();
        HotkeyBox.Text = _hotkey;
    }

    // ---------- privacy ----------

    public sealed class ExcludedRow(ExcludedApp app)
    {
        public ExcludedApp App { get; } = app;
        public string Name => App.Name;
        public string Exe => App.Exe;
        public System.Windows.Media.ImageSource? Icon => AppIconCache.Get(App.Path);
        public bool HasNoIcon => Icon is null;
    }

    private readonly System.Collections.ObjectModel.ObservableCollection<ExcludedRow> _excluded = [];

    private void Detect_Changed(object sender, RoutedEventArgs e) => UpdatePrivacyPanels();

    private void UpdatePrivacyPanels()
    {
        if (SensitivePanel is null || TtlPanel is null) return;
        SensitivePanel.Visibility = DetectSwitch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TtlPanel.Visibility = SensitiveHide.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var picker = new AppPickerWindow(_app, _excluded.Select(r => r.Exe).ToList()) { Owner = this };
        if (picker.ShowDialog() == true && picker.Result is { } entry &&
            !_excluded.Any(r => r.Exe.Equals(entry.Exe, StringComparison.OrdinalIgnoreCase)))
        {
            _excluded.Add(new ExcludedRow(new ExcludedApp(entry.Exe, entry.Name, entry.Path)));
        }
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ExcludedRow row }) _excluded.Remove(row);
    }

    private void AiSwitch_Changed(object sender, RoutedEventArgs e) => UpdateAiPanels();
    private void Provider_Changed(object sender, RoutedEventArgs e) => UpdateAiPanels();

    private void UpdateAiPanels()
    {
        if (AiPanel is null) return;
        AiPanel.Visibility = AiSwitch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        bool ollama = ProviderOllama.IsChecked == true;
        ClaudePanel.Visibility = ollama ? Visibility.Collapsed : Visibility.Visible;
        OllamaPanel.Visibility = ollama ? Visibility.Visible : Visibility.Collapsed;
    }

    private AppSettings Collect()
    {
        var s = _app.Settings.Clone();
        if (Hotkey.TryParse(_hotkey, out _)) s.Hotkey = _hotkey;
        s.UseWinV = WinVSwitch.IsChecked == true;
        s.PasteDirectly = PasteSwitch.IsChecked == true;
        s.StartWithWindows = StartupSwitch.IsChecked == true;
        s.Theme = ThemeDark.IsChecked == true ? "Dark" : ThemeLight.IsChecked == true ? "Light" : "System";

        if (int.TryParse(RetentionBox.Text.Trim(), out var days) && days >= 0) s.RetentionDays = days;
        if (int.TryParse(MaxItemsBox.Text.Trim(), out var max) && max >= 10) s.MaxItems = max;
        s.CaptureImages = ImagesSwitch.IsChecked == true;
        s.OcrImages = OcrSwitch.IsChecked == true;

        s.DetectSensitive = DetectSwitch.IsChecked == true;
        s.SensitiveHandling = SensitiveSkip.IsChecked == true ? "Skip" : "Hide";
        if (int.TryParse(TtlBox.Text.Trim(), out var ttl) && ttl is >= 1 and <= 1440) s.SensitiveTtlMinutes = ttl;
        s.SkipPrivateWindows = PrivateWindowsSwitch.IsChecked == true;
        s.HideFromScreenCapture = CaptureSwitch.IsChecked == true;
        s.ExcludedApps = _excluded.Select(r => r.App).ToList();

        s.AiEnabled = AiSwitch.IsChecked == true;
        s.AiProvider = ProviderOllama.IsChecked == true ? "Ollama" : "Anthropic";
        s.AnthropicKey = ApiKeyBox.Password;
        if (!string.IsNullOrWhiteSpace(ClaudeModelBox.Text)) s.AnthropicModel = ClaudeModelBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(OllamaEndpointBox.Text)) s.OllamaEndpoint = OllamaEndpointBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(OllamaModelBox.Text)) s.OllamaModel = OllamaModelBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(TranslateBox.Text)) s.TranslateTo = TranslateBox.Text.Trim();
        return s;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _app.UpdateSettings(Collect());
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        TestButton.IsEnabled = false;
        TestResult.SetResourceReference(ForegroundProperty, "Fg.Secondary");
        TestResult.Text = "Connecting…";
        try
        {
            var reply = await AiService.TestAsync(Collect(), cts.Token);
            TestResult.Text = $"✓ Connected — “{reply.Trim()}”";
        }
        catch (Exception ex)
        {
            TestResult.SetResourceReference(ForegroundProperty, "Fg.Danger");
            TestResult.Text = ex.Message;
            TestResult.ToolTip = ex.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(AppSettings.DataDir) { UseShellExecute = true });

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "Delete your entire clipboard history? Pinned clips are kept.",
            "Clear history", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK) _app.Store.Clear(keepPinned: true);
    }

    private void Link_Navigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
