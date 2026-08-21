using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace CodexPulse.Services;

internal sealed class ChatGptPresenceMonitor : IDisposable
{
    private readonly DispatcherTimer _timer;
    private bool? _lastPresence;
    private bool _started;

    public ChatGptPresenceMonitor()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += Timer_Tick;
    }

    public event EventHandler<bool>? PresenceChanged;

    public bool IsRunning { get; private set; }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        Refresh();
        _timer.Start();
    }

    public void Stop()
    {
        _started = false;
        _timer.Stop();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        Refresh();
    }

    private void Refresh()
    {
        var isRunning = IsChatGptProcessRunning();
        IsRunning = isRunning;
        if (_lastPresence == isRunning)
        {
            return;
        }

        _lastPresence = isRunning;
        PresenceChanged?.Invoke(this, isRunning);
    }

    private static bool IsChatGptProcessRunning()
    {
        var configuredNames = Environment.GetEnvironmentVariable("CODEX_PULSE_CHATGPT_PROCESS_NAMES");
        var processNames = string.IsNullOrWhiteSpace(configuredNames)
            ? new[] { "ChatGPT", "ChatGPTDesktop" }
            : configuredNames
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => Path.GetFileNameWithoutExtension(name))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        foreach (var processName in processNames)
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    using (process)
                    {
                        if (!process.HasExited)
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // A process may exit or deny access during the scan.
            }
        }

        return false;
    }

    public void Dispose()
    {
        Stop();
    }
}
