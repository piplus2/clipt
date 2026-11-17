using System.Windows.Forms;

namespace Clipt.Platform.Windows;

internal sealed class WindowsTray : ITray
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _open;
    private readonly ToolStripMenuItem _pause;

    public WindowsTray(App app)
    {
        _open = new ToolStripMenuItem("Open Clipt", null, (_, _) => app.ShowLauncher()) { Font = new System.Drawing.Font(SystemInformation.MenuFont, System.Drawing.FontStyle.Bold) };
        _pause = new ToolStripMenuItem("Pause recording", null, (_, _) => app.TogglePause()) { CheckOnClick = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_open);
        menu.Items.Add(_pause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => app.OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Clipt", null, (_, _) => app.Quit());

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Clipt",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) app.ShowLauncher(); };
    }

    public void Update(string hotkey, bool paused)
    {
        _open.ShortcutKeyDisplayString = hotkey;
        _pause.Checked = paused;
        _icon.Text = paused ? "Clipt — paused" : $"Clipt — {hotkey}";
    }

    public void Notify(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.None);

    /// <summary>The Clipt logo (drawn in code, see UI/IconFactory) as a Windows icon.</summary>
    private static System.Drawing.Icon CreateIcon()
    {
        using var ms = new MemoryStream(UI.IconFactory.Png(32));
        using var bmp = new System.Drawing.Bitmap(ms);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
