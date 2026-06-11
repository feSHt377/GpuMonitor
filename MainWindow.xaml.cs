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
    private static readonly string ConfigPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "GpuMonitor", "config.json");

    // 配置数据结构
    private record AppConfig(string Host, string User, string Password, int Port, double IntervalSeconds,
        int? WindowX = null, int? WindowY = null,
        string OverlayEdge = "Right", bool OverlayCompact = false, double OverlayOpacity = 0.7,
        string OverlayAlignment = "Center");

    public MainWindow()
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] === GPU Monitor 启动 ===");
        InitializeComponent();

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
        // 隐藏无数据提示
        NoDataBorder.Visibility = Visibility.Collapsed;

        // 显示驱动版本
        if (!string.IsNullOrEmpty(data.DriverVersion))
        {
            TxtDriverInfo.Visibility = Visibility.Visible;
            TxtDriverInfo.Text = $"🖥 驱动 {data.DriverVersion} | CUDA {data.CudaVersion}";
        }

        // 清除旧的 GPU 卡片（保留 NoDataBorder 和 TxtDriverInfo）
        var toRemove = new List<UIElement>();
        foreach (var child in GpuStackPanel.Children)
        {
            if (child is Border b && b != NoDataBorder)
                toRemove.Add(b);
            if (child is TextBlock t && t != TxtNoData && t != TxtDriverInfo)
                toRemove.Add(t);
        }
        foreach (var item in toRemove)
            GpuStackPanel.Children.Remove(item);

        // 渲染每张 GPU 卡片
        foreach (var gpu in data.Gpus)
        {
            var card = CreateGpuCard(gpu);
            GpuStackPanel.Children.Add(card);
        }
    }

    private Border CreateGpuCard(GpuInfo gpu)
    {
        // 防御：跳过无效的GPU条目
        if (string.IsNullOrWhiteSpace(gpu.Name) || gpu.Name == "N/A")
        {
            Console.WriteLine($"[UI] 跳过无效GPU: Index={gpu.Index}, Name='{gpu.Name}'");
            return new Border { Height = 0 }; // 返回空元素
        }

        // 温度颜色
        var tempColor = gpu.Temperature switch
        {
            < 50 => "#51CF66",   // 绿色 - 低温
            < 70 => "#FCC419",   // 黄色 - 中温
            < 85 => "#FF922B",   // 橙色 - 高温
            _ => "#FF6B6B"       // 红色 - 危险
        };

        // 利用率颜色
        var utilColor = gpu.GpuUtilization switch
        {
            < 30 => "#51CF66",
            < 70 => "#FCC419",
            < 90 => "#FF922B",
            _ => "#FF6B6B"
        };

        // 风扇转速颜色
        var fanColor = gpu.FanSpeed switch
        {
            0 => "#5C5F66",       // 灰色 - 停转/被动散热
            < 30 => "#51CF66",   // 绿色 - 低转速
            < 60 => "#4DABF7",   // 蓝色 - 中转速
            < 85 => "#FCC419",   // 黄色 - 高转速
            _ => "#FF922B"       // 橙色 - 满速
        };

        var card = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2C2E33")!),
            CornerRadius = new CornerRadius(6),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#373A40")!),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var stack = new StackPanel();

        // GPU 名称
        stack.Children.Add(new TextBlock
        {
            Text = $"⚡ GPU {gpu.Index}: {gpu.Name}",
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C1C2C5")!),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 8)
        });

        // 指标网格
        var metricsGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            }
        };

        // 温度
        metricsGrid.Children.Add(CreateMetricBlock("🌡 温度", $"{gpu.Temperature}°C", tempColor));
        Grid.SetColumn(metricsGrid.Children[^1], 0);
        Grid.SetRow(metricsGrid.Children[^1], 0);

        // 利用率
        metricsGrid.Children.Add(CreateMetricBlock("📊 利用率", $"{gpu.GpuUtilization}%", utilColor));
        Grid.SetColumn(metricsGrid.Children[^1], 1);
        Grid.SetRow(metricsGrid.Children[^1], 0);

        // 功耗（第二行）
        if (!string.IsNullOrEmpty(gpu.PowerUsage))
        {
            metricsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            metricsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            metricsGrid.Children.Add(CreateMetricBlock("⚡ 功耗", gpu.PowerUsage, "#4DABF7"));
            Grid.SetColumn(metricsGrid.Children[^1], 0);
            Grid.SetRow(metricsGrid.Children[^1], 1);

            // 风扇
            metricsGrid.Children.Add(CreateMetricBlock("🌀 风扇", $"{gpu.FanSpeed}%", fanColor));
            Grid.SetColumn(metricsGrid.Children[^1], 1);
            Grid.SetRow(metricsGrid.Children[^1], 1);
        }

        stack.Children.Add(metricsGrid);

        // 显存进度条
        if (!string.IsNullOrEmpty(gpu.MemoryUsage))
        {
            var memParts = gpu.MemoryUsage.Split(" / ");
            if (memParts.Length == 2)
            {
                stack.Children.Add(CreateMemoryBar(memParts[0], memParts[1]));
            }
            else
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"💾 显存: {gpu.MemoryUsage}",
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#909296")!),
                    FontSize = 11,
                    Margin = new Thickness(0, 6, 0, 0)
                });
            }
        }

        // 性能状态标签
        if (!string.IsNullOrEmpty(gpu.PerformanceState))
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"🏷 性能状态: {gpu.PerformanceState} | 模式: {gpu.ComputeMode}",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5C5F66")!),
                FontSize = 10,
                Margin = new Thickness(0, 6, 0, 0)
            });
        }

        card.Child = stack;
        return card;
    }

    private static Border CreateMetricBlock(string label, string value, string colorHex)
    {
        var border = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#25262B")!),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 4, 4)
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#909296")!),
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 2)
        });
        stack.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)!),
            FontSize = 18,
            FontWeight = FontWeights.Bold
        });

        border.Child = stack;
        return border;
    }

    private static Border CreateMemoryBar(string used, string total)
    {
        // 尝试解析数值
        var usedVal = ParseMemoryMiB(used);
        var totalVal = ParseMemoryMiB(total);
        var percent = totalVal > 0 ? (double)usedVal / totalVal * 100 : 0;

        var barColor = percent switch
        {
            < 50 => "#51CF66",
            < 80 => "#FCC419",
            < 95 => "#FF922B",
            _ => "#FF6B6B"
        };

        var container = new Border
        {
            Margin = new Thickness(0, 8, 0, 0)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 标签
        grid.Children.Add(new TextBlock
        {
            Text = $"💾 显存: {used} / {total}",
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#909296")!),
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 3)
        });

        // 进度条
        var barOuter = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#25262B")!),
            CornerRadius = new CornerRadius(3),
            Height = 8
        };

        var barInner = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(barColor)!),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = Math.Max(8, Math.Min(percent / 100 * 320, 320))
        };

        barOuter.Child = barInner;
        Grid.SetRow(barOuter, 1);
        grid.Children.Add(barOuter);

        container.Child = grid;
        return container;
    }

    private static double ParseMemoryMiB(string memStr)
    {
        // 支持 "8000MiB", "8GiB", "8192MiB" 等格式，统一转为 MiB
        memStr = memStr.Trim().ToUpperInvariant();

        if (memStr.EndsWith("MIB") && double.TryParse(memStr[..^3], out var mib))
            return mib;
        if (memStr.EndsWith("GIB") && double.TryParse(memStr[..^3], out var gib))
            return gib * 1024;
        if (memStr.EndsWith("MB") && double.TryParse(memStr[..^2], out var mb))
            return mb * 0.953674; // MB to MiB

        // 纯数字尝试
        if (double.TryParse(memStr, out var num))
            return num;

        return 0;
    }

    private void UpdateStatus(string message, Color color)
    {
        TxtStatus.Text = message;
        StatusDot.Fill = new SolidColorBrush(color);
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

    private double GetIntervalSeconds()
    {
        // 最小刷新间隔 0.1 秒，避免过于频繁的 SSH 请求
        const double MinInterval = 0.1;
        const double DefaultInterval = 3.0;
        if (double.TryParse(TxtInterval.Text.Trim(), out var sec) && sec >= MinInterval)
            return sec;
        // 如果输入值小于最小值，使用最小值
        if (double.TryParse(TxtInterval.Text.Trim(), out var smallSec) && smallSec > 0)
            return MinInterval;
        return DefaultInterval;
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
            _ssh.Host, _ssh.UserName, _ssh.Password, _ssh.Port, GetIntervalSeconds(),
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
