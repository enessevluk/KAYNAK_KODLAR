using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RavenMapPanel;

internal static class WindowActivation
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    public static void BringToFront(Window window)
    {
        try
        {
            if (!window.IsVisible)
                window.Show();

            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;

            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero)
            {
                ShowWindow(handle, SW_RESTORE);
                SetForegroundWindow(handle);
            }

            window.Topmost = true;
            window.Topmost = false;
            window.Activate();
            window.Focus();
        }
        catch
        {
            // Focus is best-effort. Never block authentication because Windows refused focus stealing.
        }
    }
}
