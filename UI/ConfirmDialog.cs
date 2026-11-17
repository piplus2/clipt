using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Clipt.UI;

/// <summary>A small OK / Cancel question in Clipt's style.</summary>
public static class ConfirmDialog
{
    public static Task<bool> ShowAsync(Window owner, string title, string message, string confirm)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = IconFactory.WindowIcon,
        };
        var ok = new Button { Content = confirm, MinWidth = 90, IsDefault = true, Classes = { "btn", "danger" } };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, Margin = new Thickness(0, 0, 8, 0), Classes = { "btn" } };
        ok.Click += (_, _) => dialog.Close(true);
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
        return dialog.ShowDialog<bool>(owner);
    }
}
