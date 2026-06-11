using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GpuMonitor;

public partial class MainWindow : Window
{
    private readonly SshService _ssh = new();
    private readonly DispatcherTimer _timer = new();
    private bool _isUpdating;
    private CancellationTokenSource? _cts;
    private MonitorWindow? _monitorWindow;
    private bool _isMonitorOn;

    // 数据绑定——UI 通过 ItemsControl 自动更新，无需手动创建/销毁控件
    public ObservableCollection<GpuInfo> GpuList { get; } = new();
    private static readonly string ConfigPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "GpuMonitor", "config.json");

    // 配置数据结构
    private record AppConfig(string Host, string User, string Password, int Port, int IntervalSeconds,
        int? WindowX = null, int? WindowY = null,
        string OverlayEdge = "Right", bool OverlayCompact = false, double OverlayOpacity = 0.7,
        string OverlayAlignment = "Center");

    public MainWindow()
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] === GPU Monitor 启动 ===");
        InitializeComponent();
        DataContext = this;

        LoadConfig();

        _timer.Tick += async (_, _) =>
        {
            try { await RefreshGpuData(); }
            catch (Exception ex) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [TIMER] 未捕获异常: {ex.Message}"); }
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [UI] Window_Loaded");

        // 注册全局热键 Ctrl+Shift+G
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);
        RegisterHotKey(hwnd);

        // 系统托盘（必须在 HWND 就绪后创建）
        SetupSystemTray(hwnd);

        if (string.IsNullOrWhiteSpace(_ssh.Host) || string.IsNullOrWhiteSpace(_ssh.UserName))
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [UI] 无已保存配置，展开设置面板");
            SettingsPanel.Visibility = Visibility.Visible;
        }
        else
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [UI] 已加载配置: {_ssh.UserName}@{_ssh.Host}:{_ssh.Port}, 间隔={GetIntervalSeconds()}s");
            await Task.Delay(300);
            StartMonitoring();
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _cts?.Cancel();
        _timer.Stop();
    }

    #region 窗口拖动

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            // 双击标题栏切换设置面板
            BtnSettings_Click(sender, e);
        }
        else if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    #endregion

    #region 按钮事件

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void BtnPin_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        BtnPin.Content = Topmost ? "📌" : "📍";
        BtnPin.ToolTip = Topmost ? "已置顶" : "未置顶 (点击置顶)";
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        // 最小化到系统托盘
        Hide();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        SaveConfig();
        SettingsPanel.Visibility = Visibility.Collapsed;
        StartMonitoring();

        // 如果监控覆盖层已开启，自动重启以应用新设置
        if (_isMonitorOn && _monitorWindow != null)
        {
            _monitorWindow.Close();
            _monitorWindow = null;
            OpenMonitorOverlay(applySettings: true);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [OVERLAY] 设置已更新，覆盖层已重启(不最小化)");
        }
    }

    private async void BtnTestConn_Click(object sender, RoutedEventArgs e)
    {
        BtnTestConn.IsEnabled = false;
        BtnTestConn.Content = "⏳ 测试中...";
        UpdateStatus("正在测试连接...", Colors.Orange);

        ApplyConfigFromUi();
        var (success, message) = await _ssh.TestConnectionAsync();

        BtnTestConn.IsEnabled = true;
        BtnTestConn.Content = "🔗 测试连接";

        if (success)
        {
            UpdateStatus("连接成功！", Colors.Green);

            // 连接成功，延迟刷新一下 GPU 数据
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                await Dispatcher.InvokeAsync(async () => await RefreshGpuData());
            });
        }
        else
        {
            UpdateStatus($"连接失败: {message}", Colors.Red);
        }
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        BtnRefresh.IsEnabled = false;
        await RefreshGpuData();
        BtnRefresh.IsEnabled = true;
    }

    private void BtnMonitor_Click(object sender, RoutedEventArgs e)
    {
        ToggleMonitorOverlay();
    }

    #endregion

    #region 核心逻辑

    private void StartMonitoring()
    {
        ApplyConfigFromUi();

        if (string.IsNullOrWhiteSpace(_ssh.Host) || string.IsNullOrWhiteSpace(_ssh.UserName))
            return;

        var interval = GetIntervalSeconds();
        _timer.Stop(); // 先停止，确保新间隔生效
        _timer.Interval = TimeSpan.FromSeconds(interval);
        _timer.Start();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [TIMER] 刷新间隔更新为 {interval}秒");

        _ = RefreshGpuData();
    }

    private async Task RefreshGpuData()
    {
        if (_isUpdating)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [REFRESH] 跳过（上一次刷新仍在进行中）");
            return;
        }
        _isUpdating = true;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [REFRESH] 开始获取 GPU 数据...");
            UpdateStatus("正在获取 GPU 数据...", Colors.Orange);

            var result = await _ssh.QueryGpuAsync(token);

            if (token.IsCancellationRequested) return;

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [REFRESH] 收到结果: Success={result.Success}, GPU数={result.Gpus.Count}");

            await Dispatcher.InvokeAsync(() =>
            {
                if (result.Success)
                {
                    UpdateStatus($"已连接 | {result.Gpus.Count} 张GPU", Colors.Green);
                    TxtLastUpdate.Text = DateTime.Now.ToString("HH:mm:ss");
                    RenderGpuCards(result);
                }
                else
                {
                    UpdateStatus($"错误: {result.ErrorMessage}", Colors.Red);
                }
            });
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [REFRESH] 已被取消");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [REFRESH] 异常: {ex.GetType().Name}: {ex.Message}");
            await Dispatcher.InvokeAsync(() =>
            {
                UpdateStatus($"异常: {ex.Message}", Colors.Red);
            });
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void RenderGpuCards(NvidiaSmiOutput data)
    {
        NoDataBorder.Visibility = data.Gpus.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (!string.IsNullOrEmpty(data.DriverVersion))
        {
            TxtDriverInfo.Visibility = Visibility.Visible;
            TxtDriverInfo.Text = $"🖥 驱动 {data.DriverVersion} | CUDA {data.CudaVersion}";
        }

        // 直接更新数据源，ItemsControl 自动刷新 UI
        GpuList.Clear();
        foreach (var gpu in data.Gpus)
        {
            if (string.IsNullOrWhiteSpace(gpu.Name) || gpu.Name == "N/A") continue;
            GpuList.Add(gpu);
        }
    }

    private void UpdateStatus(string message, Color color)
    {
        TxtStatus.Text = message;
        StatusDot.Fill = BrushCache.Get(color.ToString());
    }

    #endregion

    #region 配置管理

    private void ApplyConfigFromUi()
    {
        _ssh.Host = TxtHost.Text.Trim();
        _ssh.UserName = TxtUser.Text.Trim();
        _ssh.Password = TxtPassword.Password;
        if (int.TryParse(TxtPort.Text.Trim(), out var port))
            _ssh.Port = port;

        var interval = GetIntervalSeconds();
        _timer.Interval = TimeSpan.FromSeconds(interval);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [CONFIG] 刷新间隔={interval:F1}s");
    }

    private int GetIntervalSeconds()
    {
        if (double.TryParse(TxtInterval.Text.Trim(), out var sec) && sec >= 1)
            return (int)sec;
        return 3;
    }

    private void SaveConfig()
    {
        ApplyConfigFromUi();

        // 保留已有的窗口位置信息
        int? existingX = null, existingY = null;
        try
        {
            if (File.Exists(ConfigPath))
            {
                var existing = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath));
                if (existing != null)
                {
                    existingX = existing.WindowX;
                    existingY = existing.WindowY;
                }
            }
        }
        catch { }

        var config = new AppConfig(
            _ssh.Host,
            _ssh.UserName,
            _ssh.Password,
            _ssh.Port,
            GetIntervalSeconds(),
            existingX,
            existingY,
            GetComboTag(CmbOverlayEdge, "Right"),
            ChkCompact.IsChecked ?? false,
            double.TryParse(GetComboTag(CmbOpacity, "0.7"), out var op) ? op : 0.7,
            GetComboTag(CmbAlignment, "Center")
        );

        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(ConfigPath,
                JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 忽略保存错误 */ }
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json);
                if (config != null)
                {
                    TxtHost.Text = config.Host;
                    TxtUser.Text = config.User;
                    TxtPassword.Password = config.Password ?? "";
                    TxtPort.Text = config.Port.ToString();
                    TxtInterval.Text = config.IntervalSeconds.ToString();
                    // 覆盖层设置
                    SetComboByTag(CmbOverlayEdge, config.OverlayEdge);
                    ChkCompact.IsChecked = config.OverlayCompact;
                    SetComboByTag(CmbOpacity, config.OverlayOpacity.ToString("F1"));
                    SetComboByTag(CmbAlignment, config.OverlayAlignment);
                    ApplyConfigFromUi();
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [CONFIG] 已加载: {ConfigPath}");
                }
            }
            else
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [CONFIG] 未找到配置文件: {ConfigPath}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [CONFIG] 加载失败: {ex.Message}");
        }
    }

    #endregion

    #region 系统托盘 + 监控覆盖层开关 + 热键

    private const int HOTKEY_ID = 9001;
    private const int WM_HOTKEY = 0x0312;
    private const int WM_TRAYICON = 0x8001;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    // Shell_NotifyIcon（简化版）
    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);
    private const uint NIM_ADD = 0;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1;
    private const uint NIF_ICON = 2;
    private const uint NIF_TIP = 4;

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    private IntPtr _trayHwnd;
    private IntPtr _trayIconHandle; // 用于释放 GDI 资源

    private void SetupSystemTray(IntPtr hwnd)
    {
        _trayHwnd = hwnd;

        // 从 icon.png 加载自定义托盘图标
        var iconPath = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath) ?? ".",
            "icon.png");
        var hIcon = IntPtr.Zero;
        if (File.Exists(iconPath))
        {
            try
            {
                using var bmp = new System.Drawing.Bitmap(iconPath);
                hIcon = bmp.GetHicon();
                _trayIconHandle = hIcon;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [TRAY] ✓ 已加载自定义图标");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [TRAY] 图标加载失败: {ex.Message}");
            }
        }

        if (hIcon == IntPtr.Zero)
            hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512); // 回退默认

        var nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = hIcon,
            szTip = "GPU Monitor"
        };
        Shell_NotifyIcon(NIM_ADD, ref nid);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [TRAY] 托盘图标已创建");
    }

    private void RegisterHotKey(IntPtr hwnd)
    {
        const uint MOD_CTRL = 0x0002;
        const uint MOD_SHIFT = 0x0004;
        const uint VK_G = 0x47;
        RegisterHotKey(hwnd, HOTKEY_ID, MOD_CTRL | MOD_SHIFT, VK_G);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [HOTKEY] Ctrl+Shift+G 切换覆盖层");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            ToggleMonitorOverlay();
            handled = true;
        }
        if (msg == WM_TRAYICON && lParam.ToInt32() == 0x0205) // WM_RBUTTONUP
        {
            ShowTrayContextMenu();
            handled = true;
        }
        if (msg == WM_TRAYICON && lParam.ToInt32() == 0x0203) // WM_LBUTTONDBLCLK
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ShowTrayContextMenu()
    {
        var menu = new ContextMenu();
        var showItem = new MenuItem { Header = "显示主窗口" };
        showItem.Click += (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); };
        menu.Items.Add(showItem);

        var monitorItem = new MenuItem { Header = "切换覆盖层 (Ctrl+Shift+G)" };
        monitorItem.Click += (_, _) => ToggleMonitorOverlay();
        menu.Items.Add(monitorItem);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => ExitApp();
        menu.Items.Add(exitItem);

        menu.IsOpen = true;
    }

    private void ExitApp()
    {
        // 清理托盘图标
        var nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _trayHwnd,
            uID = 1
        };
        Shell_NotifyIcon(NIM_DELETE, ref nid);

        // 释放 GDI 图标句柄
        if (_trayIconHandle != IntPtr.Zero)
        {
            DestroyIcon(_trayIconHandle);
            _trayIconHandle = IntPtr.Zero;
        }

        _monitorWindow?.Close();
        _cts?.Cancel();
        _timer.Stop();
        Environment.Exit(0);
    }

    private void ToggleMonitorOverlay()
    {
        if (_isMonitorOn)
        {
            _monitorWindow?.Close();
            _monitorWindow = null;
            _isMonitorOn = false;
            BtnMonitor.Content = "📺";
            BtnMonitor.ToolTip = "监控覆盖层 (已关闭)";
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [OVERLAY] 已关闭");
        }
        else
        {
            OpenMonitorOverlay(applySettings: true);
            // 主窗口自动最小化到托盘
            Hide();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [OVERLAY] 已开启，主窗口已最小化");
        }
    }

    private void OpenMonitorOverlay(bool applySettings)
    {
        if (applySettings) ApplyConfigFromUi();
        _monitorWindow = new MonitorWindow();
        _monitorWindow.Closed += (_, _) =>
        {
            _isMonitorOn = false;
            _monitorWindow = null;
            BtnMonitor.Content = "📺";
            BtnMonitor.ToolTip = "监控覆盖层 (已关闭)";
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [OVERLAY] 已关闭");
        };
        _monitorWindow.Show();
        _monitorWindow.ApplyConfigAndStart(
            _ssh, GetIntervalSeconds(),
            GetComboTag(CmbOverlayEdge, "Right"),
            ChkCompact.IsChecked ?? false,
            double.TryParse(GetComboTag(CmbOpacity, "0.7"), out var op) ? op : 0.7,
            GetComboTag(CmbAlignment, "Center")
        );
        _isMonitorOn = true;
        BtnMonitor.Content = "📺✓";
        BtnMonitor.ToolTip = "监控覆盖层 (已开启) | Ctrl+Shift+G 切换";
    }

    #endregion

    #region 辅助方法

    private static void SetComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Tag?.ToString() == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static string GetComboTag(ComboBox combo, string defaultTag)
    {
        return (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? defaultTag;
    }

    #endregion
}
