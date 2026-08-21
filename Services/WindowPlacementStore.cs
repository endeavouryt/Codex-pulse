using System.IO;
using System.Text.Json;
using System.Windows;

namespace CodexPulse.Services;

internal sealed class WindowPlacementStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexPulse",
        "window.json");

    public void Restore(Window window)
    {
        try
        {
            if (!File.Exists(_path))
            {
                SetDefaultPosition(window);
                return;
            }

            var placement = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(_path));
            if (placement is null || !double.IsFinite(placement.Left) || !double.IsFinite(placement.Top))
            {
                SetDefaultPosition(window);
                return;
            }

            window.Left = ClampLeft(placement.Left, window.Width);
            window.Top = ClampTop(placement.Top, window.Height);
        }
        catch
        {
            SetDefaultPosition(window);
        }
    }

    public void Save(Window window)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            var placement = new WindowPlacement { Left = window.Left, Top = window.Top };
            File.WriteAllText(_path, JsonSerializer.Serialize(placement, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Position persistence is best-effort and should never affect the widget.
        }
    }

    private static void SetDefaultPosition(Window window)
    {
        window.Left = SystemParameters.WorkArea.Right - window.Width - 30;
        window.Top = SystemParameters.WorkArea.Bottom - window.Height - 24;
    }

    private static double ClampLeft(double left, double width)
    {
        var min = SystemParameters.VirtualScreenLeft + 8;
        var max = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - width - 8;
        return Math.Clamp(left, min, Math.Max(min, max));
    }

    private static double ClampTop(double top, double height)
    {
        var min = SystemParameters.VirtualScreenTop + 8;
        var max = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - height - 8;
        return Math.Clamp(top, min, Math.Max(min, max));
    }

    private sealed class WindowPlacement
    {
        public double Left { get; set; }
        public double Top { get; set; }
    }
}
