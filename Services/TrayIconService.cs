using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace CodexPulse.Services;

internal sealed class TrayIconService : IDisposable
{
    private readonly Window _window;
    private readonly Forms.ContextMenuStrip _contextMenu;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private bool _disposed;

    public TrayIconService(Window window)
    {
        _window = window;
        _icon = (Icon)SystemIcons.Application.Clone();

        var openItem = new Forms.ToolStripMenuItem("打开 Pulse");
        openItem.Click += (_, _) => ShowWindow();

        var exitItem = new Forms.ToolStripMenuItem("退出 Codex Pulse");
        exitItem.Click += (_, _) => ExitApplication();

        _contextMenu = new Forms.ContextMenuStrip();
        _contextMenu.Items.Add(openItem);
        _contextMenu.Items.Add(new Forms.ToolStripSeparator());
        _contextMenu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "Codex Pulse",
            ContextMenuStrip = _contextMenu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();
    }

    private void ShowWindow()
    {
        if (_disposed)
        {
            return;
        }

        if (!_window.Dispatcher.CheckAccess())
        {
            _ = _window.Dispatcher.BeginInvoke(new Action(ShowWindow));
            return;
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        _ = _window.Activate();
    }

    private void ExitApplication()
    {
        if (_disposed)
        {
            return;
        }

        if (_window.Dispatcher.CheckAccess())
        {
            _window.Close();
            return;
        }

        _ = _window.Dispatcher.BeginInvoke(new Action(_window.Close));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
        _icon.Dispose();
    }
}
