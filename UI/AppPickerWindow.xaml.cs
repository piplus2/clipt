using System.Windows;
using System.Windows.Input;
using Clipt.Native;
using Clipt.Services;

namespace Clipt.UI;

/// <summary>Lets anyone pick an app by name and icon; "Browse…" remains for power users.</summary>
public partial class AppPickerWindow : Window
{
    private List<AppEntry> _all = [];

    public AppEntry? Result { get; private set; }

    public AppPickerWindow(App app, IReadOnlyCollection<string> alreadyExcluded)
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            WindowEffects.HideCaptionButtons(this);
            bool backdrop = WindowEffects.Apply(this, Backdrop.Mica, app.IsDark);
            Root.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, backdrop ? "Bg.Settings" : "Bg.Fallback");
        };
        AppList.SelectionChanged += (_, _) => AddButton.IsEnabled = AppList.SelectedItem is not null;
        PreviewKeyDown += OnKey;

        var recent = app.Store.KnownSourceApps();
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            var apps = await Task.Run(() => AppCatalog.Collect(recent));
            _all = apps.Where(a => !alreadyExcluded.Contains(a.Exe, StringComparer.OrdinalIgnoreCase)).ToList();
            LoadingText.Visibility = Visibility.Collapsed;
            Filter();
        };
    }

    private void Filter()
    {
        var q = SearchBox.Text.Trim();
        Placeholder.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppList.ItemsSource = q.Length == 0
            ? _all
            : _all.Where(a => a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                              a.Exe.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (AppList.Items.Count > 0) AppList.SelectedIndex = 0;
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Filter();

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.Down when AppList.Items.Count > 0:
                AppList.SelectedIndex = Math.Min(AppList.SelectedIndex + 1, AppList.Items.Count - 1);
                AppList.ScrollIntoView(AppList.SelectedItem);
                e.Handled = true; break;
            case Key.Up when AppList.Items.Count > 0:
                AppList.SelectedIndex = Math.Max(AppList.SelectedIndex - 1, 0);
                AppList.ScrollIntoView(AppList.SelectedItem);
                e.Handled = true; break;
        }
    }

    private void Accept(AppEntry? entry)
    {
        if (entry is null) return;
        Result = entry;
        DialogResult = true;
    }

    private void Add_Click(object sender, RoutedEventArgs e) => Accept(AppList.SelectedItem as AppEntry);
    private void AppList_DoubleClick(object sender, MouseButtonEventArgs e) => Accept(AppList.SelectedItem as AppEntry);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a program",
            Filter = "Programs (*.exe)|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dlg.ShowDialog(this) == true) Accept(AppCatalog.FromPath(dlg.FileName));
    }
}
