using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CodexPulse.Interop;

namespace CodexPulse.Services;

internal sealed class ChatGptPresenceMonitor : IDisposable
{
    private static readonly TimeSpan RecentUserInputWindow = TimeSpan.FromSeconds(5);
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
    public bool IsFocused { get; private set; }
    public int? FocusedProcessId { get; private set; }
    public bool UserInputRecently { get; private set; }
    public DateTimeOffset? LastUserInputUtc { get; private set; }

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
        var processNames = GetConfiguredProcessNames();
        var isRunning = IsChatGptProcessRunning(processNames);
        var foregroundProcessId = NativeMethods.GetForegroundProcessId();
        var isFocused = foregroundProcessId.HasValue &&
                        IsConfiguredChatGptProcess(foregroundProcessId.Value, processNames);

        IsRunning = isRunning;
        IsFocused = isFocused;
        FocusedProcessId = isFocused ? foregroundProcessId : null;
        var lastUserInputUtc = isFocused ? NativeMethods.GetLastUserInputUtc() : null;
        UserInputRecently = lastUserInputUtc.HasValue &&
                            DateTimeOffset.UtcNow - lastUserInputUtc.Value <= RecentUserInputWindow;
        LastUserInputUtc = UserInputRecently ? lastUserInputUtc : null;
        if (_lastPresence == isRunning)
        {
            return;
        }

        _lastPresence = isRunning;
        PresenceChanged?.Invoke(this, isRunning);
    }

    private static bool IsChatGptProcessRunning(IReadOnlyList<string> processNames)
    {
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

    private static bool IsConfiguredChatGptProcess(int processId, IReadOnlyList<string> processNames)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return processNames.Any(name =>
                string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> GetConfiguredProcessNames()
    {
        var configuredNames = Environment.GetEnvironmentVariable("CODEX_PULSE_CHATGPT_PROCESS_NAMES");
        return string.IsNullOrWhiteSpace(configuredNames)
            ? new[] { "ChatGPT", "ChatGPTDesktop" }
            : configuredNames
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => Path.GetFileNameWithoutExtension(name))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    public void Dispose()
    {
        Stop();
    }
}
