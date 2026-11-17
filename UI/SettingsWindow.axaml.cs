using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Clipt.Services;

namespace Clipt.UI;

public sealed class ExcludedRow(ExcludedApp app)
{
    public ExcludedApp App { get; } = app;
    public string Name => App.Name;
    public string Exe => App.Exe;
    public Bitmap? Icon => AppIconCache.Get(App.Path);
    public bool HasNoIcon => Icon is null;
}

public partial class SettingsWindow : Window
{
    private readonly App _app;
    private string _hotkey;
    private CancellationTokenSource? _testCts;
    private readonly ObservableCollection<ExcludedRow> _excluded = [];

    public SettingsWindow() : this(App.Current) { } // XAML previewer

    public SettingsWindow(App app)
    {
        InitializeComponent();
        _app = app;
        Icon = IconFactory.WindowIcon;
        var platform = app.Platform;
        VersionText.Text = $"Clipt {typeof(App).Assembly.GetName().Version?.ToString(3)} · data in {AppSettings.DataDir}";

        var s = app.Settings;
        _hotkey = s.Hotkey;
        HotkeyBox.Text = s.Hotkey;
        WinVRow.IsVisible = platform.SupportsWinVTakeover;
        WinVSwitch.IsChecked = s.UseWinV;
        PasteSwitch.IsChecked = s.PasteDirectly;
        StartupLabel.Text = platform.StartupLabel;
        StartupSwitch.IsChecked = s.StartWithWindows;
        (s.Theme switch { "Dark" => ThemeDark, "Light" => ThemeLight, _ => ThemeSystem }).IsChecked = true;

        RetentionBox.Text = s.RetentionDays.ToString();
        MaxItemsBox.Text = s.MaxItems.ToString();
        ImagesSwitch.IsChecked = s.CaptureImages;
        OcrSwitch.IsChecked = s.OcrImages;
        OcrDesc.Text = platform.OcrDescription;

        DetectSwitch.IsChecked = s.DetectSensitive;
        (s.SensitiveHandling == "Skip" ? SensitiveSkip : SensitiveHide).IsChecked = true;
        TtlBox.Text = s.SensitiveTtlMinutes.ToString();
        PrivateWindowsSwitch.IsChecked = s.SkipPrivateWindows;
        CaptureRow.IsVisible = platform.SupportsCaptureProtection;
        CaptureSwitch.IsChecked = s.HideFromScreenCapture;
        foreach (var a in s.ExcludedApps ?? []) _excluded.Add(new ExcludedRow(a));
        ExcludedList.ItemsSource = _excluded;
        SecurityText.Text = platform.KeyProtectionDescription +
            " Password managers that mark their copies as private are always skipped.";
        UpdatePrivacyPanels();

        AiSwitch.IsChecked = s.AiEnabled;
        (s.AiProvider == "Ollama" ? ProviderOllama : ProviderClaude).IsChecked = true;
        ApiKeyBox.Text = s.AnthropicKey ?? "";
        ClaudeModelBox.Text = s.AnthropicModel;
        OllamaEndpointBox.Text = s.OllamaEndpoint;
        OllamaModelBox.Text = s.OllamaModel;
        TranslateBox.Text = s.TranslateTo;
        UpdateAiPanels();

        HotkeyBox.AddHandler(KeyDownEvent, HotkeyBox_KeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && !HotkeyBox.IsFocused) Close();
        }, RoutingStrategies.Tunnel);
        Opened += (_, _) => ApplyTheme();
        Closed += (_, _) => _testCts?.Cancel();
    }

    /// <summary>Mica shows through on Windows 11; elsewhere the window is opaque.</summary>
    public void ApplyTheme()
    {
        bool mica = ActualTransparencyLevel == WindowTransparencyLevel.Mica;
        Root.Bind(Border.BackgroundProperty, this.GetResourceObservable(mica ? "Bg.Settings" : "Bg.Fallback"));
    }

    private void HotkeyBox_KeyDown(object? sender, KeyEventArgs e)
    {
        var key = e.Key;
        if (key == Key.Tab) return; // allow focus navigation
        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin)
            return;
        var mods = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta);
        if (mods == KeyModifiers.None)
        {
            HotkeyBox.Text = $"Add Ctrl, Alt, Shift or {Platform.PlatformDefaults.SuperKeyName}";
            return;
        }
        var hk = new Hotkey(mods, key);
        _hotkey = hk.ToString();
        HotkeyBox.Text = _hotkey;
    }

    // ---------- privacy ----------

    private void Detect_Changed(object? sender, RoutedEventArgs e) => UpdatePrivacyPanels();

    private void UpdatePrivacyPanels()
    {
        if (SensitivePanel is null || TtlPanel is null) return;
        SensitivePanel.IsVisible = DetectSwitch.IsChecked == true;
        TtlPanel.IsVisible = SensitiveHide.IsChecked == true;
    }

    private async void AddApp_Click(object? sender, RoutedEventArgs e)
    {
        var picker = new AppPickerWindow(_app, _excluded.Select(r => r.Exe).ToList());
        var entry = await picker.ShowDialog<AppEntry?>(this);
        if (entry is not null && !_excluded.Any(r => r.Exe.Equals(entry.Exe, StringComparison.OrdinalIgnoreCase)))
            _excluded.Add(new ExcludedRow(new ExcludedApp(entry.Exe, entry.Name, entry.Path)));
    }

    private void RemoveApp_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: ExcludedRow row }) _excluded.Remove(row);
    }

    private void AiSwitch_Changed(object? sender, RoutedEventArgs e) => UpdateAiPanels();
    private void Provider_Changed(object? sender, RoutedEventArgs e) => UpdateAiPanels();

    private void UpdateAiPanels()
    {
        if (AiPanel is null) return;
        AiPanel.IsVisible = AiSwitch.IsChecked == true;
        bool ollama = ProviderOllama.IsChecked == true;
        ClaudePanel.IsVisible = !ollama;
        OllamaPanel.IsVisible = ollama;
    }

    private AppSettings Collect()
    {
        var s = _app.Settings.Clone();
        if (Hotkey.TryParse(_hotkey, out _)) s.Hotkey = _hotkey;
        s.UseWinV = WinVSwitch.IsChecked == true;
        s.PasteDirectly = PasteSwitch.IsChecked == true;
        s.StartWithWindows = StartupSwitch.IsChecked == true;
        s.Theme = ThemeDark.IsChecked == true ? "Dark" : ThemeLight.IsChecked == true ? "Light" : "System";

        if (int.TryParse(RetentionBox.Text?.Trim(), out var days) && days >= 0) s.RetentionDays = days;
        if (int.TryParse(MaxItemsBox.Text?.Trim(), out var max) && max >= 10) s.MaxItems = max;
        s.CaptureImages = ImagesSwitch.IsChecked == true;
        s.OcrImages = OcrSwitch.IsChecked == true;

        s.DetectSensitive = DetectSwitch.IsChecked == true;
        s.SensitiveHandling = SensitiveSkip.IsChecked == true ? "Skip" : "Hide";
        if (int.TryParse(TtlBox.Text?.Trim(), out var ttl) && ttl is >= 1 and <= 1440) s.SensitiveTtlMinutes = ttl;
        s.SkipPrivateWindows = PrivateWindowsSwitch.IsChecked == true;
        s.HideFromScreenCapture = CaptureSwitch.IsChecked == true;
        s.ExcludedApps = _excluded.Select(r => r.App).ToList();

        s.AiEnabled = AiSwitch.IsChecked == true;
        s.AiProvider = ProviderOllama.IsChecked == true ? "Ollama" : "Anthropic";
        s.AnthropicKey = ApiKeyBox.Text;
        if (!string.IsNullOrWhiteSpace(ClaudeModelBox.Text)) s.AnthropicModel = ClaudeModelBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(OllamaEndpointBox.Text)) s.OllamaEndpoint = OllamaEndpointBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(OllamaModelBox.Text)) s.OllamaModel = OllamaModelBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(TranslateBox.Text)) s.TranslateTo = TranslateBox.Text.Trim();
        return s;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        _app.UpdateSettings(Collect());
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    private async void Test_Click(object? sender, RoutedEventArgs e)
    {
        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        TestButton.IsEnabled = false;
        TestResult.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Fg.Secondary"));
        TestResult.Text = "Connecting…";
        try
        {
            var reply = await AiService.TestAsync(Collect(), cts.Token);
            TestResult.Text = $"✓ Connected — “{reply.Trim()}”";
        }
        catch (Exception ex)
        {
            TestResult.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Fg.Danger"));
            TestResult.Text = ex.Message;
            ToolTip.SetTip(TestResult, ex.Message);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void OpenData_Click(object? sender, RoutedEventArgs e) => _app.Platform.Open(AppSettings.DataDir);

    private async void Clear_Click(object? sender, RoutedEventArgs e)
    {
        bool ok = await ConfirmDialog.ShowAsync(this, "Clear history", "Delete your entire clipboard history? Pinned clips are kept.", "Delete");
        if (ok) _app.Store.Clear(keepPinned: true);
    }

    private void ApiKeyLink_Click(object? sender, RoutedEventArgs e) =>
        _app.Platform.Open("https://console.anthropic.com/settings/keys");
}
