using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Clipt.Services;

namespace Clipt.UI;

/// <summary>Lets anyone pick an app by name and icon; "Browse…" remains for power users.</summary>
public partial class AppPickerWindow : Window
{
    private readonly App _app;
    private List<AppEntry> _all = [];

    public AppPickerWindow() : this(App.Current, []) { } // XAML previewer

    public AppPickerWindow(App app, IReadOnlyCollection<string> alreadyExcluded)
    {
        InitializeComponent();
        _app = app;
        Icon = IconFactory.WindowIcon;
        AppList.SelectionChanged += (_, _) => AddButton.IsEnabled = AppList.SelectedItem is not null;
        AppList.DoubleTapped += (_, _) => Accept(AppList.SelectedItem as AppEntry);
        SearchBox.TextChanged += (_, _) => Filter();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);

        var recent = app.Store.KnownSourceApps();
        Opened += async (_, _) =>
        {
            bool mica = ActualTransparencyLevel == WindowTransparencyLevel.Mica;
            Root.Bind(Avalonia.Controls.Border.BackgroundProperty, this.GetResourceObservable(mica ? "Bg.Settings" : "Bg.Fallback"));
            SearchBox.Focus();
            var apps = await Task.Run(() => AppCatalog.Collect(app.Platform, recent));
            _all = apps.Where(a => !alreadyExcluded.Contains(a.Exe, StringComparer.OrdinalIgnoreCase)).ToList();
            LoadingText.IsVisible = false;
            Filter();
        };
    }

    private void Filter()
    {
        var q = SearchBox.Text?.Trim() ?? "";
        AppList.ItemsSource = q.Length == 0
            ? _all
            : _all.Where(a => a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                              a.Exe.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (AppList.ItemCount > 0) AppList.SelectedIndex = 0;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(null); e.Handled = true; break;
            case Key.Enter: Accept(AppList.SelectedItem as AppEntry); e.Handled = true; break;
            case Key.Down when AppList.ItemCount > 0:
                AppList.SelectedIndex = Math.Min(AppList.SelectedIndex + 1, AppList.ItemCount - 1);
                if (AppList.SelectedItem is { } down) AppList.ScrollIntoView(down);
                e.Handled = true; break;
            case Key.Up when AppList.ItemCount > 0:
                AppList.SelectedIndex = Math.Max(AppList.SelectedIndex - 1, 0);
                if (AppList.SelectedItem is { } up) AppList.ScrollIntoView(up);
                e.Handled = true; break;
        }
    }

    private void Accept(AppEntry? entry)
    {
        if (entry is not null) Close(entry);
    }

    private void Add_Click(object? sender, RoutedEventArgs e) => Accept(AppList.SelectedItem as AppEntry);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var filter = _app.Platform.ProgramFileFilter;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a program",
            AllowMultiple = false,
            FileTypeFilter = filter is { } f ? [new FilePickerFileType(f.name) { Patterns = f.patterns }] : null,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(
                OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) : "/usr/bin"),
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            Accept(AppCatalog.FromPath(_app.Platform, path));
    }
}
