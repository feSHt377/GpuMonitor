using System.Runtime.InteropServices;
using System.Windows;

namespace GpuMonitor;

public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;
    private const string MutexName = "GpuMonitor_SingleInstance_5A0E3C1F";

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            // 已有实例在运行，找到并激活它
            var hwnd = FindWindow(null, "GPU Monitor");
            if (hwnd != IntPtr.Zero)
            {
                if (IsIconic(hwnd))
                    ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
            Environment.Exit(0);
            return;
        }

        base.OnStartup(e);
    }
}

