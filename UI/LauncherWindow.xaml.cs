using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Clipt.Models;
using Clipt.Native;
using Clipt.Services;

namespace Clipt.UI;

public partial class LauncherWindow : Window
{
    private readonly LauncherViewModel _vm;
    private readonly App _app;
    private IntPtr _hwnd;
    private IntPtr _target;

    public event Action? SettingsRequested;

    public LauncherWindow(App app, LauncherViewModel vm)
    {
        InitializeComponent();
        _app = app;
        DataContext = _vm = vm;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            WindowEffects.MakeToolWindow(this);
            SetCaptureProtection(_captureProtection);
        };
        Deactivated += (_, _) => { if (IsVisible) HideLauncher(); };
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewTextInput += OnPreviewTextInput;

        foreach (var list in new[] { ClipList, ActionList })
        {
            list.PreviewMouseLeftButtonDown += SelectRowUnderMouse;
            list.PreviewMouseRightButtonDown += SelectRowUnderMouse;
            var l = list;
            l.Loaded += (_, _) =>
            {
                if (l.Template.FindName("Scroller", l) is ScrollViewer sv) SmoothScroll.Enable(sv, () => RowHeight(l));
            };
        }
    }

    private bool _captureProtection = true;

    /// <summary>Excludes the launcher from screenshots, screen recording and screen sharing (Teams, Zoom, OBS…).</summary>
    public void SetCaptureProtection(bool enabled)
    {
        _captureProtection = enabled;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            Win32.SetWindowDisplayAffinity(hwnd, enabled ? Win32.WDA_EXCLUDEFROMCAPTURE : Win32.WDA_NONE);
    }

    public void ApplyTheme(bool dark)
    {
        bool backdrop = WindowEffects.Apply(this, Backdrop.Acrylic, dark);
        Root.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, backdrop ? "Bg.Overlay" : "Bg.Fallback");
    }

    // ---------- show / hide ----------

    public void Toggle()
    {
        if (IsVisible && IsActive) HideLauncher();
        else ShowLauncher();
    }

    public void ShowLauncher()
    {
        var fg = Win32.GetForegroundWindow();
        if (fg != _hwnd && fg != IntPtr.Zero) _target = fg;

        _vm.OnShow();
        if (!IsVisible)
        {
            PositionOnActiveMonitor();
            Show();
            PositionOnActiveMonitor(); // second pass: correct size after any DPI change
        }
        Activate();
        Win32.ForceForeground(_hwnd);
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        AnimateIn();
    }

    public void HideLauncher()
    {
        _vm.CancelAi();
        _vm.ConcealAll();
        Hide();
        ClipItem.ReleasePreviews();
        _trimTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(5),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle, (_, _) => TrimMemory(), Dispatcher);
        _trimTimer.Stop();
        _trimTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _trimTimer;

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
        Win32.SetProcessWorkingSetSize(Win32.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
    }

    private void PositionOnActiveMonitor()
    {
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).EnsureHandle();

        IntPtr monitor;
        if (_target != IntPtr.Zero) monitor = Win32.MonitorFromWindow(_target, Win32.MONITOR_DEFAULTTONEAREST);
        else
        {
            Win32.GetCursorPos(out var pt);
            monitor = Win32.MonitorFromPoint(pt, Win32.MONITOR_DEFAULTTONEAREST);
        }
        var info = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref info)) return;
        var work = info.rcWork;

        // Move onto the monitor first so Windows applies its DPI, then centre using the real size.
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, work.Left + 40, work.Top + 40, 0, 0,
            Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        Win32.GetWindowRect(_hwnd, out var r);
        int x = work.Left + (work.Width - r.Width) / 2;
        int y = work.Top + (int)((work.Height - r.Height) * 0.28);
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    private void AnimateIn()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110)));
        RootShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
    }

    // ---------- keyboard ----------

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Typing anywhere goes to the search box (Spotlight behaviour).
        if (!SearchBox.IsKeyboardFocused && e.Text.Length > 0 && !char.IsControl(e.Text[0]))
        {
            SearchBox.Focus();
            SearchBox.SelectedText = e.Text;
            SearchBox.CaretIndex = SearchBox.Text.Length;
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control);
        bool shift = mods.HasFlag(ModifierKeys.Shift);
        bool alt = mods.HasFlag(ModifierKeys.Alt);
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

            case Key.C when ctrl && SearchBox.SelectionLength == 0 && _vm.IsBrowse && _vm.Selected is not null:
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
            await PasteService.SendPasteAsync(target);
    }

    private void OpenSelected()
    {
        var item = _vm.Selected;
        if (item is null) return;
        if (item.IsMasked) return;
        if (item.Kind == ClipKind.Image)
        {
            SaveImageAs(item);
            return;
        }
        string? target = item.Kind switch
        {
            ClipKind.Link => item.NormalizedUrl,
            ClipKind.Files => item.Paths.FirstOrDefault(),
            _ => null,
        };
        if (target is null) return;
        // Only hand real web links to the shell; never execute arbitrary copied text.
        if (item.Kind == ClipKind.Link && !(Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
                                            uri.Scheme is "http" or "https" or "mailto" or "ftp"))
            return;
        try
        {
            // Files are revealed in Explorer, never launched: a copied .exe/.bat must not run from here.
            if (item.Kind == ClipKind.Files)
            {
                if (File.Exists(target)) Process.Start("explorer.exe", $"/select,\"{target}\"");
                else if (Directory.Exists(target)) Process.Start("explorer.exe", $"\"{target}\"");
                else { _vm.StatusText = "That file no longer exists"; return; }
            }
            else Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            HideLauncher();
        }
        catch (Exception ex) { _vm.StatusText = ex.Message; }
    }

    /// <summary>Images are stored encrypted, so "open" means exporting a copy where the user chooses.</summary>
    private void SaveImageAs(ClipItem item)
    {
        var bytes = _app.Store.ReadImage(item.ImagePath);
        if (bytes is null) return;
        HideLauncher();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Clipt image {item.CreatedAt.ToLocalTime():yyyy-MM-dd HHmmss}.png",
            Filter = "PNG image|*.png",
        };
        if (dlg.ShowDialog() == true) File.WriteAllBytes(dlg.FileName, bytes);
    }

    private void OpenSettings()
    {
        HideLauncher();
        SettingsRequested?.Invoke();
    }

    // ---------- mouse / menu ----------

    /// <summary>
    /// Rows are deliberately not focusable (typing must always reach the search box), and WPF only selects a
    /// ListBoxItem on click if it can focus it, so mouse selection is done here. Not marked handled, so
    /// double-click and the context menu still work, now on the row that was actually clicked.
    /// </summary>
    private void SelectRowUnderMouse(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        var hit = e.OriginalSource as DependencyObject;
        while (hit is not null and not ListBoxItem)
            hit = hit is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(hit)
                : LogicalTreeHelper.GetParent(hit);
        if (hit is ListBoxItem row && list.ItemContainerGenerator.ItemFromContainer(row) is { } item &&
            item != DependencyProperty.UnsetValue)
        {
            list.SelectedItem = item;
        }
    }

    private static double RowHeight(ListBox list)
    {
        for (int i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement row && row.ActualHeight > 0)
                return row.ActualHeight + row.Margin.Top + row.Margin.Bottom;
        }
        return 50;
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedItem is not null) lb.ScrollIntoView(lb.SelectedItem);
    }

    private void ClipList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.Selected is not null) _ = PasteAsync(_vm.Selected, PasteMode.Rich, copyOnly: false);
    }

    private void ActionList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedAction is not null) _ = _vm.RunAiAsync(_vm.SelectedAction);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void Menu_Paste(object sender, RoutedEventArgs e) { if (_vm.Selected is { } s) _ = PasteAsync(s, PasteMode.Rich, false); }
    private void Menu_PastePlain(object sender, RoutedEventArgs e) { if (_vm.Selected is { } s) _ = PasteAsync(s, PasteMode.PlainText, false); }
    private void Menu_Copy(object sender, RoutedEventArgs e) { if (_vm.Selected is { } s) _ = PasteAsync(s, PasteMode.Rich, true); }
    private void Menu_Open(object sender, RoutedEventArgs e) => OpenSelected();
    private void Menu_Pin(object sender, RoutedEventArgs e) => _vm.TogglePin();
    private void Menu_Delete(object sender, RoutedEventArgs e) => _vm.DeleteSelected();
    private void Menu_Ai(object sender, RoutedEventArgs e)
    {
        _vm.EnterAi();
        SearchBox.Focus();
    }
    private void Menu_Reveal(object sender, RoutedEventArgs e) => _vm.ToggleReveal();
    private void Menu_Sensitive(object sender, RoutedEventArgs e) => _vm.ToggleSensitive();
    private void Menu_ExcludeApp(object sender, RoutedEventArgs e) => _vm.ExcludeSelectedApp();

    private void ClipMenu_Opened(object sender, RoutedEventArgs e)
    {
        var item = _vm.Selected;
        RevealMenuItem.Visibility = item?.Sensitive == true ? Visibility.Visible : Visibility.Collapsed;
        RevealMenuItem.Header = item?.IsRevealed == true ? "Hide content" : "Show content";
        SensitiveMenuItem.Header = item?.Sensitive == true ? "Unmark as sensitive" : "Mark as sensitive";
        OpenMenuItem.Header = item?.Kind == ClipKind.Image ? "Save image as…" : "Open";
        OpenMenuItem.IsEnabled = item is { IsMasked: false, Kind: ClipKind.Link or ClipKind.Files or ClipKind.Image };
        AiMenuItem.IsEnabled = item is { Sensitive: false } && _app.Ai.Enabled;

        var app = item?.SourceApp;
        bool canExclude = !string.IsNullOrEmpty(item?.SourcePath);
        ExcludeMenuItem.Visibility = canExclude ? Visibility.Visible : Visibility.Collapsed;
        ExcludeMenuItem.Header = $"Never record from {app}";
    }
}
