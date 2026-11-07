using System.Windows;
using Forms = System.Windows.Forms;

namespace SimpleRGB.Platform;

/// <summary>Notification-area icon with a small menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _lightsItem;

    public TrayIcon(Action open, Action toggleLights, Func<bool> lightsOff, Action exit)
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/SimpleRGB.ico"))!.Stream;
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        var openItem = new Forms.ToolStripMenuItem("Open SimpleRGB", null, (_, _) => open()) { Font = new System.Drawing.Font(Forms.Control.DefaultFont, System.Drawing.FontStyle.Bold) };
        _lightsItem = new Forms.ToolStripMenuItem("Turn lights off", null, (_, _) => toggleLights());
        menu.Items.Add(openItem);
        menu.Items.Add(_lightsItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => exit()));
        menu.Opening += (_, _) => _lightsItem.Text = lightsOff() ? "Turn lights on" : "Turn lights off";

        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "SimpleRGB",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };
    }

    public void ShowHint(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
