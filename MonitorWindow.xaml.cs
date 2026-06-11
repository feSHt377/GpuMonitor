using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GpuMonitor;

/// <summary>
/// 点击穿透的 GPU 监控覆盖层——类似 Steam 帧率显示
/// </summary>
public partial class MonitorWindow : Window
{
    private readonly DispatcherTimer _timer = new();
    private SshService _ssh = null!; // 由主窗口注入
    private bool _isUpdating;
    private CancellationTokenSource? _cts;

    // 覆盖层设置
    private string _overlayEdge = "Right";
    private bool _compactMode;
    private double _opacity = 0.7;
    private string _alignment = "Center";

    // P/Invoke: 设置窗口为点击穿透
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    public MonitorWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // 设置为点击穿透
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        _timer.Tick += async (_, _) =>
        {
            try { await RefreshData(); }
            catch { }
        };
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _cts?.Cancel();
        _timer.Stop();
    }

    /// <summary>
    /// 应用配置并开始刷新（由主窗口调用）
    /// </summary>
    public void ApplyConfigAndStart(SshService ssh, int intervalSec,
        string overlayEdge = "Right", bool compactMode = false, double opacity = 0.7,
        string alignment = "Center")
    {
        _ssh = ssh;
        _timer.Interval = TimeSpan.FromSeconds(intervalSec);
        _overlayEdge = overlayEdge;
        _compactMode = compactMode;
        _opacity = Math.Clamp(opacity, 0.15, 0.95);
        _alignment = alignment;

        ApplyPositionAndLayout();
        _timer.Start();
        _ = RefreshData();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [OVERLAY] 已启动, 边={overlayEdge}, 紧凑={compactMode}, 透明度={_opacity:F1}");
    }

    private void ApplyPositionAndLayout()
    {
        var workArea = SystemParameters.WorkArea;

        if (_compactMode)
        {
            Width = 420;
            Height = 28;
            RootBorder.CornerRadius = new CornerRadius(4);
            RootBorder.Padding = new Thickness(6, 3, 6, 3);
        }
        else
        {
            Width = 200;
            Height = 120;
            RootBorder.CornerRadius = new CornerRadius(6);
            RootBorder.Padding = new Thickness(8, 6, 8, 6);
        }

        // 背景透明度
        RootBorder.Background = new SolidColorBrush(
            Color.FromArgb((byte)(_opacity * 255), 0, 0, 0));

        // 停靠位置
        switch (_overlayEdge.ToLower())
        {
            case "left":
                Left = workArea.Left + 5;
                Top = (workArea.Height - Height) / 2 + workArea.Top;
                break;
            case "top":
                Left = (workArea.Width - Width) / 2 + workArea.Left;
                Top = workArea.Top + 5;
                break;
            case "bottom":
                Left = (workArea.Width - Width) / 2 + workArea.Left;
                Top = workArea.Bottom - Height - 5;
                break;
            default: // right
                Left = workArea.Right - Width - 5;
                Top = (workArea.Height - Height) / 2 + workArea.Top;
                break;
        }
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [OVERLAY] 位置: Left={Left:F0}, Top={Top:F0}, W={Width}, H={Height}");
    }

    private void LoadConfig()
    {
        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GpuMonitor", "config.json");

            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                _ssh.Host = root.TryGetProperty("Host", out var h) ? h.GetString() ?? "" : "";
                _ssh.UserName = root.TryGetProperty("User", out var u) ? u.GetString() ?? "" : "";
                _ssh.Password = root.TryGetProperty("Password", out var p) ? p.GetString() ?? "" : "";
                _ssh.Port = root.TryGetProperty("Port", out var port) && port.TryGetInt32(out var pv) ? pv : 22;
                var interval = root.TryGetProperty("IntervalSeconds", out var iv) && iv.TryGetDouble(out var ivv) ? ivv : 3.0;
                _timer.Interval = TimeSpan.FromSeconds(interval);
            }
        }
        catch { }
    }

    private async Task RefreshData()
    {
        if (_isUpdating) return;
        _isUpdating = true;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var result = await _ssh.QueryGpuAsync(token);
            if (token.IsCancellationRequested) return;

            await Dispatcher.InvokeAsync(() => Render(result));
        }
        catch { }
        finally
        {
            _isUpdating = false;
        }
    }

    private void Render(NvidiaSmiOutput data)
    {
        if (!data.Success || data.Gpus.Count == 0)
            return;

        var gpu = data.Gpus[0];
        if (string.IsNullOrWhiteSpace(gpu.Name) || gpu.Name == "N/A") return;

        var textAlign = _alignment.ToLower() switch
        {
            "left" => TextAlignment.Left,
            "right" => TextAlignment.Right,
            _ => TextAlignment.Center
        };

        if (_compactMode)
        {
            NormalPanel.Visibility = Visibility.Collapsed;
            CompactPanel.Visibility = Visibility.Visible;
            CompactPanel.HorizontalAlignment = textAlign switch
            {
                TextAlignment.Left => HorizontalAlignment.Left,
                TextAlignment.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Center
            };
            TxtCompactName.Text = gpu.Name.Length > 14 ? gpu.Name[..14] : gpu.Name;
            TxtCompactTemp.Text = $"{gpu.Temperature}°C";
            TxtCompactTemp.Foreground = BrushCache.GetTempBrush(gpu.Temperature);
            TxtCompactUtil.Text = $"{gpu.GpuUtilization}%";
            TxtCompactUtil.Foreground = BrushCache.GetUtilBrush(gpu.GpuUtilization);

            if (!string.IsNullOrEmpty(gpu.MemoryUsage))
            {
                var mp = gpu.MemoryUsage.Split(" / ");
                TxtCompactMem.Text = mp.Length == 2 ? $"{mp[0]}/{mp[1]}" : gpu.MemoryUsage;
            }
            TxtCompactPower.Text = !string.IsNullOrEmpty(gpu.PowerUsage) ? gpu.PowerUsage : "";
        }
        else
        {
            NormalPanel.Visibility = Visibility.Visible;
            CompactPanel.Visibility = Visibility.Collapsed;
            NormalPanel.HorizontalAlignment = textAlign switch
            {
                TextAlignment.Left => HorizontalAlignment.Left,
                TextAlignment.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Center
            };
            TxtGpuName.Text = gpu.Name.Length > 28 ? gpu.Name[..28] : gpu.Name;
            TxtTemp.Text = $"{gpu.Temperature}°C";
            TxtTemp.Foreground = BrushCache.GetTempBrush(gpu.Temperature);
            TxtUtil.Text = $"{gpu.GpuUtilization}%";
            TxtUtil.Foreground = BrushCache.GetUtilBrush(gpu.GpuUtilization);

            if (!string.IsNullOrEmpty(gpu.MemoryUsage))
            {
                var memParts = gpu.MemoryUsage.Split(" / ");
                if (memParts.Length == 2)
                    TxtMem.Text = $"{FormatMem(ParseMem(memParts[0]))} / {FormatMem(ParseMem(memParts[1]))}";
                else TxtMem.Text = gpu.MemoryUsage;
            }
            TxtPowerFan.Text = !string.IsNullOrEmpty(gpu.PowerUsage)
                ? $"{gpu.PowerUsage}  🌀{gpu.FanSpeed}%"
                : $"🌀{gpu.FanSpeed}%";
        }
    }

    private static double ParseMem(string s)
    {
        s = s.Trim().ToUpperInvariant();
        if (s.EndsWith("MIB") && double.TryParse(s[..^3], out var m)) return m;
        if (s.EndsWith("GIB") && double.TryParse(s[..^3], out var g)) return g * 1024;
        if (s.EndsWith("MB") && double.TryParse(s[..^2], out var mb)) return mb * 0.953674;
        return double.TryParse(s, out var n) ? n : 0;
    }

    private static string FormatMem(double mib) => mib >= 1024 ? $"{mib / 1024:F1}GiB" : $"{mib:F0}MiB";
}
