using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Clipt.Models;
using Clipt.Services;

namespace Clipt.UI;

public partial class LauncherWindow : Window
{
    private readonly LauncherViewModel _vm;
    private readonly App _app;
    private nint _target;
    private bool _captureProtection = true;
    private bool _prepared;

    public event Action? SettingsRequested;

    public LauncherWindow() : this(App.Current, null!) { } // XAML previewer

    public LauncherWindow(App app, LauncherViewModel vm)
    {
        InitializeComponent();
        _app = app;
        DataContext = _vm = vm;
        Icon = IconFactory.WindowIcon;

        Opened += (_, _) =>
        {
            if (_prepared) return;
            _prepared = true;
            _app.Platform.PrepareLauncherWindow(this);
            SetCaptureProtection(_captureProtection);
            ApplyTheme();
        };
        Deactivated += (_, _) => { if (IsVisible && !_dialogOpen) HideLauncher(); };
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnPreviewTextInput, RoutingStrategies.Tunnel);

        foreach (var list in new[] { ClipList, ActionList })
        {
            list.AddHandler(PointerPressedEvent, SelectRowUnderPointer, RoutingStrategies.Tunnel);
            list.SelectionChanged += (_, _) => { if (list.SelectedItem is { } item) list.ScrollIntoView(item); };
        }
        ClipList.DoubleTapped += (_, _) => { if (_vm.Selected is not null) _ = PasteAsync(_vm.Selected, PasteMode.Rich, copyOnly: false); };
        ActionList.DoubleTapped += (_, _) => { if (_vm.SelectedAction is not null) _ = _vm.RunAiAsync(_vm.SelectedAction); };
        ClipMenu.Opening += (_, _) => UpdateClipMenu();
    }

    /// <summary>Excludes the launcher from screenshots, screen recording and screen sharing where the OS supports it.</summary>
    public void SetCaptureProtection(bool enabled)
    {
        _captureProtection = enabled;
        if (_prepared) _app.Platform.SetCaptureProtection(this, enabled);
    }

    /// <summary>Translucent over a system blur (Windows acrylic, KDE blur); opaque and rounded otherwise.</summary>
    public void ApplyTheme()
    {
        var level = ActualTransparencyLevel;
        bool blurred = level == WindowTransparencyLevel.AcrylicBlur || level == WindowTransparencyLevel.Blur || level == WindowTransparencyLevel.Mica;
        Root.Bind(Border.BackgroundProperty, this.GetResourceObservable(blurred ? "Bg.Overlay" : "Bg.Fallback"));
        // Windows 11 rounds the window itself; elsewhere round the content over a transparent window.
        Root.CornerRadius = OperatingSystem.IsWindows() ? default : new CornerRadius(12);
        Root.BorderThickness = OperatingSystem.IsWindows() ? default : new Thickness(1);
        Root.Bind(Border.BorderBrushProperty, this.GetResourceObservable("Stroke.Control"));
    }

    // ---------- show / hide ----------

    public void Toggle()
    {
        if (IsVisible && IsActive) HideLauncher();
        else ShowLauncher();
    }

    public void ShowLauncher()
    {
        var fg = _app.Platform.GetForegroundWindow();
        var own = TryGetPlatformHandle()?.Handle ?? 0;
        if (fg != 0 && fg != own) _target = fg;

        _vm.OnShow();
        if (!IsVisible)
        {
            PositionOnActiveMonitor();
            PrepareAnimation();
            Show();
        }
        Activate();
        _app.Platform.ForceForeground(this);
        SearchBox.Focus();
        AnimateIn();
    }

    public void HideLauncher()
    {
        _vm.CancelAi();
        _vm.ConcealAll();
        Hide();
        ClipItem.ReleasePreviews();
        _trimTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.ApplicationIdle, (_, _) => TrimMemory());
        _trimTimer.Stop();
        _trimTimer.Start();
    }

    private DispatcherTimer? _trimTimer;

    /// <summary>
    /// Clipt spends most of its life hidden in the tray: give back what the launcher used (decoded images,
    /// search results) instead of holding it until the next garbage collection happens to need it.
    /// </summary>
    private void TrimMemory()
    {
        _trimTimer?.Stop();
        if (IsVisible) return;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        _app.Platform.TrimWorkingSet();
    }

    /// <summary>Centres the launcher horizontally, a little above the middle, on the monitor of the app being used.</summary>
    private void PositionOnActiveMonitor()
    {
        var anchor = _app.Platform.GetAnchorPoint(_target);
        var screen = (anchor is { } a ? Screens.ScreenFromPoint(a) : null) ?? Screens.Primary;
        if (screen is null) return;
        var work = screen.WorkingArea;
        double scale = screen.Scaling;
        int w = (int)(Width * scale), h = (int)(Height * scale);
        Position = new PixelPoint(work.X + (work.Width - w) / 2, work.Y + (int)((work.Height - h) * 0.28));
    }

    private void PrepareAnimation()
    {
        var transitions = Root.Transitions;
        Root.Transitions = null;
        Root.Opacity = 0;
        Root.RenderTransform = TransformOperations.Parse("translateY(-8px)");
        Root.Transitions = transitions;
    }

    private void AnimateIn() => Dispatcher.UIThread.Post(() =>
    {
        Root.Opacity = 1;
        Root.RenderTransform = TransformOperations.Parse("translateY(0px)");
    }, DispatcherPriority.Background);

    // ---------- keyboard ----------

    private void OnPreviewTextInput(object? sender, TextInputEventArgs e)
    {
        // Typing anywhere goes to the search box (Spotlight behaviour).
        if (!SearchBox.IsFocused && e.Text is { Length: > 0 } t && !char.IsControl(t[0]))
        {
            SearchBox.Focus();
            SearchBox.SelectedText = t;
            SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var key = e.Key;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        e.Handled = true;

        switch (key)
        {
            case Key.Escape:
                if (_vm.Mode == LauncherMode.AiResult) _vm.BackToActions();
                else if (_vm.Mode == LauncherMode.AiActions) _vm.ExitAi();
                else if (_vm.Query.Length > 0) _vm.Query = "";
                else HideLauncher();
                break;

            case Key.Down: _vm.Move(1); break;
            case Key.Up: _vm.Move(-1); break;
            case Key.PageDown: _vm.Move(8); break;
            case Key.PageUp: _vm.Move(-8); break;
            case Key.Home when ctrl: _vm.MoveToEdge(false); break;
            case Key.End when ctrl: _vm.MoveToEdge(true); break;

            case Key.Enter:
                OnEnter(shift, ctrl);
                break;

            case Key.Tab when ctrl:
                if (_vm.IsBrowse) _vm.CycleFilter(shift ? -1 : 1);
                break;

            case Key.Tab:
                if (_vm.IsBrowse) _vm.EnterAi();
                else _vm.ExitAi();
                break;

            case Key.P when ctrl && _vm.IsBrowse: _vm.TogglePin(); break;
            case Key.R when ctrl && _vm.IsBrowse: _vm.ToggleReveal(); break;
            case Key.H when ctrl && _vm.IsBrowse: _vm.ToggleSensitive(); break;
            case Key.Delete when shift && _vm.IsBrowse: _vm.DeleteSelected(); break;
            case Key.O when ctrl && _vm.IsBrowse: OpenSelected(); break;
            case Key.OemComma when ctrl: OpenSettings(); break;

            case Key.C when ctrl && SearchBox.SelectionStart == SearchBox.SelectionEnd && _vm.IsBrowse && _vm.Selected is not null:
                _ = PasteAsync(_vm.Selected, PasteMode.Rich, copyOnly: true);
                break;

            case >= Key.D1 and <= Key.D9 when alt && _vm.IsBrowse:
                int n = key - Key.D1;
                if (n < _vm.Items.Count) _ = PasteAsync(_vm.Items[n], PasteMode.Rich, copyOnly: false);
                break;

            case >= Key.D1 and <= Key.D7 when ctrl && _vm.IsBrowse:
                _vm.SetFilter(key - Key.D1);
                break;

            default:
                e.Handled = false;
                break;
        }
    }

    private void OnEnter(bool shift, bool ctrl)
    {
        switch (_vm.Mode)
        {
            case LauncherMode.Browse when _vm.Selected is not null:
                _ = PasteAsync(_vm.Selected, shift ? PasteMode.PlainText : PasteMode.Rich, copyOnly: ctrl);
                break;
            case LauncherMode.AiActions when _vm.SelectedAction is not null:
                _ = _vm.RunAiAsync(_vm.SelectedAction);
                break;
            case LauncherMode.AiResult when _vm.HasAiResult:
                if (_vm.CreateClipFromAiResult() is { } clip)
                    _ = PasteAsync(clip, PasteMode.PlainText, copyOnly: ctrl);
                break;
        }
    }

    // ---------- actions ----------

    private async Task PasteAsync(ClipItem item, PasteMode mode, bool copyOnly)
    {
        if (!_app.Paste.CopyToClipboard(item, mode))
        {
            _vm.StatusText = "Couldn't access the clipboard — try again";
            return;
        }
        var target = _target;
        HideLauncher();
        if (!copyOnly && _app.Settings.PasteDirectly)
            await _app.Paste.SendPasteAsync(target);
    }

    private void OpenSelected()
    {
        var item = _vm.Selected;
        if (item is null || item.IsMasked) return;
        if (item.Kind == ClipKind.Image)
        {
            _ = SaveImageAsAsync(item);
            return;
        }
        string? target = item.Kind switch
        {
            ClipKind.Link => item.NormalizedUrl,
            ClipKind.Files => item.Paths.FirstOrDefault(),
            _ => null,
        };
        if (target is null) return;
        // Only hand real web links to the OS; never execute arbitrary copied text.
        if (item.Kind == ClipKind.Link && !(Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
                                            uri.Scheme is "http" or "https" or "mailto" or "ftp"))
            return;
        try
        {
            // Files are revealed in the file manager, never launched: a copied program or script must not run from here.
            if (item.Kind == ClipKind.Files)
            {
                if (!File.Exists(target) && !Directory.Exists(target)) { _vm.StatusText = "That file no longer exists"; return; }
                _app.Platform.Reveal(target);
            }
            else _app.Platform.Open(target);
            HideLauncher();
        }
        catch (Exception ex) { _vm.StatusText = ex.Message; }
    }

    private bool _dialogOpen;

    /// <summary>Images are stored encrypted, so "open" means exporting a copy where the user chooses.</summary>
    private async Task SaveImageAsAsync(ClipItem item)
    {
        var bytes = _app.Store.ReadImage(item.ImagePath);
        if (bytes is null) return;
        _dialogOpen = true;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save image",
                SuggestedFileName = $"Clipt image {item.CreatedAt.ToLocalTime():yyyy-MM-dd HHmmss}.png",
                DefaultExtension = "png",
                FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(bytes);
        }
        catch (Exception ex) { App.Log(ex); }
        finally
        {
            _dialogOpen = false;
            HideLauncher();
        }
    }

    private void OpenSettings()
    {
        HideLauncher();
        SettingsRequested?.Invoke();
    }

    // ---------- mouse / menu ----------

    /// <summary>
    /// Rows are deliberately not focusable (typing must always reach the search box), so mouse selection is done
    /// here, for both buttons. Not marked handled, so double-click and the context menu still work, now on the row
    /// that was actually clicked.
    /// </summary>
    private void SelectRowUnderPointer(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox list || e.Source is not Visual v) return;
        var row = v as ListBoxItem ?? v.FindAncestorOfType<ListBoxItem>();
        if (row?.DataContext is { } item && list.ItemsSource is System.Collections.IList items && items.Contains(item))
            list.SelectedItem = item;
    }

    private void Settings_Click(object? sender, RoutedEventArgs e) => OpenSettings();

    private void Menu_Paste(object? sender, RoutedEventArgs e) { if (_vm.Selected is { } s) _ = PasteAsync(s, PasteMode.Rich, false); }
    private void Menu_PastePlain(object? sender, RoutedEventArgs e) { if (_vm.Selected is { } s) _ = PasteAsync(s, PasteMode.PlainText, false); }
    private void Menu_Copy(object? sender, RoutedEventArgs e) { if (_vm.Selected is { } s) _ = PasteAsync(s, PasteMode.Rich, true); }
    private void Menu_Open(object? sender, RoutedEventArgs e) => OpenSelected();
    private void Menu_Pin(object? sender, RoutedEventArgs e) => _vm.TogglePin();
    private void Menu_Delete(object? sender, RoutedEventArgs e) => _vm.DeleteSelected();
    private void Menu_Ai(object? sender, RoutedEventArgs e)
    {
        _vm.EnterAi();
        SearchBox.Focus();
    }
    private void Menu_Reveal(object? sender, RoutedEventArgs e) => _vm.ToggleReveal();
    private void Menu_Sensitive(object? sender, RoutedEventArgs e) => _vm.ToggleSensitive();
    private void Menu_ExcludeApp(object? sender, RoutedEventArgs e) => _vm.ExcludeSelectedApp();

    private void UpdateClipMenu()
    {
        var item = _vm.Selected;
        RevealMenuItem.IsVisible = item?.Sensitive == true;
        RevealMenuItem.Header = item?.IsRevealed == true ? "Hide content" : "Show content";
        SensitiveMenuItem.Header = item?.Sensitive == true ? "Unmark as sensitive" : "Mark as sensitive";
        OpenMenuItem.Header = item?.Kind == ClipKind.Image ? "Save image as…" : "Open";
        OpenMenuItem.IsEnabled = item is { IsMasked: false, Kind: ClipKind.Link or ClipKind.Files or ClipKind.Image };
        AiMenuItem.IsEnabled = item is { Sensitive: false } && _app.Ai.Enabled;

        bool canExclude = !string.IsNullOrEmpty(item?.SourcePath);
        ExcludeMenuItem.IsVisible = canExclude;
        ExcludeMenuItem.Header = $"Never record from {item?.SourceApp}";
    }
}
