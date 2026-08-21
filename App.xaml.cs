using System.Threading;
using System.Windows;
using CodexPulse.Services;
using WpfApplication = System.Windows.Application;

namespace CodexPulse;

public partial class App : WpfApplication
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string mutexName = "CodexPulse.SingleInstance.v1";
        _singleInstanceMutex = new Mutex(true, mutexName, out var createdNew);
        _ownsSingleInstanceMutex = createdNew;

        if (!createdNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        new StartupSettings().EnsureApplied();
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
