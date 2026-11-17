using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Clipt.UI;

/// <summary>A small OK / Cancel question in Clipt's style.</summary>
public static class ConfirmDialog
{
    /// <param name="owner">Parent window; null (e.g. from the tray menu) shows a centred, top-most window.</param>
    public static Task<bool> ShowAsync(Window? owner, string title, string message, string confirm)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Topmost = owner is null,
            Icon = IconFactory.WindowIcon,
        };
        var ok = new Button { Content = confirm, MinWidth = 90, IsDefault = true, Classes = { "btn", "danger" } };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, Margin = new Thickness(0, 0, 8, 0), Classes = { "btn" } };
        bool confirmed = false;
        ok.Click += (_, _) => { confirmed = true; dialog.Close(true); };
        cancel.Click += (_, _) => dialog.Close(false);

        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13.5 };
        text.Bind(TextBlock.ForegroundProperty, dialog.GetResourceObservable("Fg.Primary"));
        var root = new Border
        {
            Padding = new Thickness(24, 20),
            Child = new StackPanel
            {
                Spacing = 20,
                Children =
                {
                    text,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
                },
            },
        };
        root.Bind(Border.BackgroundProperty, dialog.GetResourceObservable("Bg.Fallback"));
        dialog.Content = root;
        if (owner is not null) return dialog.ShowDialog<bool>(owner);

        // Without an owner there's no ShowDialog result: report what was clicked (closing the window = Cancel).
        var result = new TaskCompletionSource<bool>();
        dialog.Closed += (_, _) => result.TrySetResult(confirmed);
        dialog.Opened += (_, _) => App.Current.Platform.ForceForeground(dialog);
        dialog.Show();
        return result.Task;
    }
}
