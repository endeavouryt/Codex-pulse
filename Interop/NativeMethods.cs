using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexPulse.Interop;

internal static class NativeMethods
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaRedirectionBitmapAlpha = 39;
    private const int DwmcpRound = 2;
    private const int DwmsbtTransientWindow = 3;
    private const int DwmColorNone = -2;
    private const int WcaAccentPolicy = 19;
    private const int AccentEnableAcrylicBlurBehind = 4;
    private const uint AcrylicTintColor = 0x66FFFBF8;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int width,
        int height);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo lastInputInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    public static void ApplyToolWindowStyle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var currentStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        var toolWindowStyle = (currentStyle | WsExToolWindow) & ~WsExAppWindow;
        if (toolWindowStyle == currentStyle)
        {
            return;
        }

        _ = SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(toolWindowStyle));
        _ = SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, index)
            : new IntPtr(GetWindowLong32(hwnd, index));
    }

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, index, value)
            : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
    }

    public static int? GetForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || GetWindowThreadProcessId(hwnd, out var processId) == 0 || processId == 0)
        {
            return null;
        }

        return checked((int)processId);
    }

    public static bool HasRecentUserInput(TimeSpan threshold)
    {
        var lastInputUtc = GetLastUserInputUtc();
        return lastInputUtc.HasValue &&
               threshold > TimeSpan.Zero &&
               DateTimeOffset.UtcNow - lastInputUtc.Value <= threshold;
    }

    public static DateTimeOffset? GetLastUserInputUtc()
    {
        var lastInputInfo = new LastInputInfo
        {
            Size = (uint)Marshal.SizeOf<LastInputInfo>()
        };
        if (!GetLastInputInfo(ref lastInputInfo))
        {
            return null;
        }

        // LASTINPUTINFO stores the low 32 bits of the system tick count. The
        // unchecked subtraction also handles the normal 32-bit wraparound.
        var elapsedMilliseconds = unchecked((uint)Environment.TickCount64 - lastInputInfo.Time);
        return DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(elapsedMilliseconds);
    }

    public static void ApplyWindows11Backdrop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } compositionTarget)
            {
                compositionTarget.BackgroundColor = Colors.Transparent;
            }

            var cornerPreference = DwmcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));

            var borderColor = DwmColorNone;
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(int));

            var useDarkMode = 0;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDarkMode, sizeof(int));

            var backdropType = DwmsbtTransientWindow;
            _ = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdropType, sizeof(int));

            // WPF renders a premultiplied-alpha redirection bitmap. Without this
            // Windows treats the transparent client area as fully opaque and the
            // DWM backdrop is hidden behind a dark surface.
            if (Environment.OSVersion.Version.Build >= 26100)
            {
                var useRedirectionAlpha = 1;
                _ = DwmSetWindowAttribute(
                    hwnd,
                    DwmwaRedirectionBitmapAlpha,
                    ref useRedirectionAlpha,
                    sizeof(int));
            }

            // Extend the DWM material through the borderless client area so the
            // Acrylic backdrop belongs to the window, not only its non-client frame.
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);

            ApplyAcrylicTint(hwnd);
        }
        catch (DllNotFoundException)
        {
            // The visual still works using the WPF glass surface on older Windows versions.
        }
        catch (EntryPointNotFoundException)
        {
            // Ignore unsupported DWM attributes.
        }
    }

    private static void ApplyAcrylicTint(IntPtr hwnd)
    {
        // SetWindowCompositionAttribute exposes the Acrylic tint/alpha controls
        // that DWMWA_SYSTEMBACKDROP_TYPE intentionally leaves to the system.
        // The moderate alpha keeps the host backdrop visible while preserving a
        // light surface on dark backgrounds. The color is near-white in AABBGGRR format.
        var policy = new AccentPolicy
        {
            AccentState = AccentEnableAcrylicBlurBehind,
            AccentFlags = 0,
            GradientColor = AcrylicTintColor,
            AnimationId = 0
        };
        var policySize = Marshal.SizeOf<AccentPolicy>();
        var policyPointer = Marshal.AllocHGlobal(policySize);
        try
        {
            Marshal.StructureToPtr(policy, policyPointer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = policyPointer,
                SizeOfData = policySize
            };
            _ = SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(policyPointer);
        }
    }

    public static void ApplyRoundedWindowRegion(IntPtr hwnd, int width, int height, int radius)
    {
        if (hwnd == IntPtr.Zero || width <= 0 || height <= 0 || radius <= 0)
        {
            return;
        }

        var region = CreateRoundRectRgn(
            0,
            0,
            width + 1,
            height + 1,
            radius * 2,
            radius * 2);
        if (region == IntPtr.Zero)
        {
            return;
        }

        if (SetWindowRgn(hwnd, region, true) == 0)
        {
            _ = DeleteObject(region);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
        IntPtr hwnd,
        ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }
}
