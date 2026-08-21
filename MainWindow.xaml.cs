using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CodexPulse.Interop;
using CodexPulse.Models;
using CodexPulse.Services;

namespace CodexPulse;

public partial class MainWindow : Window
{
    private readonly CodexDataService _dataService = new();
    private readonly WindowPlacementStore _placementStore = new();
    private readonly ChatGptPresenceMonitor _presenceMonitor = new();
    private readonly TrayIconService _trayIcon;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DoubleAnimation _spinnerAnimation;
    private bool _isRefreshing;
    private bool _placementRestored;
    private PulseStatus? _lastStatus;

    public MainWindow()
    {
        InitializeComponent();
        _trayIcon = new TrayIconService(this);

        _spinnerAnimation = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.05))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CircleEase { EasingMode = EasingMode.EaseInOut }
        };

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _presenceMonitor.PresenceChanged += PresenceMonitor_PresenceChanged;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _placementStore.Restore(this);
        _placementRestored = true;
        ApplyRoundedWindowRegion();
        _presenceMonitor.Start();
        await RefreshAsync();
        _refreshTimer.Start();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        NativeMethods.ApplyToolWindowStyle(handle);
        NativeMethods.ApplyWindows11Backdrop(handle);
        ApplyRoundedWindowRegion();
    }

    private void ApplyRoundedWindowRegion()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var dpi = VisualTreeHelper.GetDpi(this);
        var widthDip = ActualWidth > 0 ? ActualWidth : Width;
        var heightDip = ActualHeight > 0 ? ActualHeight : Height;
        var width = Math.Max(1, (int)Math.Round(widthDip * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Round(heightDip * dpi.DpiScaleY));
        var radius = Math.Max(1, (int)Math.Round(18 * dpi.DpiScaleX));
        NativeMethods.ApplyRoundedWindowRegion(handle, width, height, radius);
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            var snapshot = await _dataService.ReadAsync(CancellationToken.None);
            ApplySnapshot(snapshot);
        }
        catch (Exception ex)
        {
            ApplySnapshot(new PulseSnapshot
            {
                SourceName = "NO DATA",
                Detail = $"数据读取失败：{ex.Message}",
                Status = PulseStatus.Idle
            });
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void PresenceMonitor_PresenceChanged(object? sender, bool isRunning)
    {
        if (isRunning)
        {
            if (!IsVisible)
            {
                Show();
            }
        }
        else
        {
            Hide();
        }
    }

    private void ApplySnapshot(PulseSnapshot snapshot)
    {
        ContextValueText.Text = FormatPercent(snapshot.ContextRemainingPercent);
        QuotaValueText.Text = FormatPercent(snapshot.QuotaRemainingPercent);

        SetStatusIcon(snapshot.Status);
        PulseToolTip.Content = BuildToolTip(snapshot);
    }

    private void SetStatusIcon(PulseStatus status)
    {
        IdleIcon.Visibility = status == PulseStatus.Idle ? Visibility.Visible : Visibility.Collapsed;
        WorkingIcon.Visibility = status == PulseStatus.Working ? Visibility.Visible : Visibility.Collapsed;
        CompletedIcon.Visibility = status == PulseStatus.Completed ? Visibility.Visible : Visibility.Collapsed;

        if (_lastStatus != status)
        {
            SpinnerTransform.BeginAnimation(
                RotateTransform.AngleProperty,
                status == PulseStatus.Working ? _spinnerAnimation : null);
            _lastStatus = status;
        }
    }

    private static string FormatPercent(double? value)
    {
        return value.HasValue ? $"{Math.Round(value.Value):0}%" : "—";
    }

    private static string BuildToolTip(PulseSnapshot snapshot)
    {
        var ctx = FormatPercent(snapshot.ContextRemainingPercent);
        var qta = FormatPercent(snapshot.QuotaRemainingPercent);
        var status = snapshot.Status switch
        {
            PulseStatus.Working => "工作中",
            PulseStatus.Completed => "已完成",
            _ => "无任务"
        };

        return $"Codex Pulse\nCTX {ctx} · QTA {qta}\n状态：{status}\n来源：{snapshot.SourceName}\n{snapshot.Detail}";
    }

    private void GlassCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
                _placementStore.Save(this);
            }
            catch (InvalidOperationException)
            {
                // The window may be closing while a drag starts.
            }
        }
    }

    private async void RefreshMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _presenceMonitor.Stop();
        if (_placementRestored)
        {
            _placementStore.Save(this);
        }

        _trayIcon.Dispose();
        _presenceMonitor.Dispose();
        _dataService.Dispose();
    }
}
