using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace GpuMonitor;

/// <summary>
/// GPU 指标颜色转换器 —— 温度 / 利用率 / 风扇转速 → 颜色
/// </summary>
public class GpuColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int num) return Brushes.Gray;
        var mode = parameter as string ?? "";

        return mode.ToLower() switch
        {
            "temp" => BrushCache.GetTempBrush(num),
            "util" => BrushCache.GetUtilBrush(num),
            "fan" => BrushCache.GetFanBrush(num),
            _ => Brushes.Gray
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 静态 Brush 缓存 —— 避免每次刷新创建大量 SolidColorBrush
/// </summary>
public static class BrushCache
{
    private static readonly Dictionary<string, SolidColorBrush> _cache = new();
    private static readonly object _lock = new();

    public static SolidColorBrush Get(string hex)
    {
        if (_cache.TryGetValue(hex, out var b)) return b;
        lock (_lock)
        {
            if (_cache.TryGetValue(hex, out b)) return b;
            b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
            b.Freeze(); // 冻结以提升性能
            _cache[hex] = b;
            return b;
        }
    }

    public static SolidColorBrush GetTempBrush(int temp) => temp switch
    {
        < 50 => Get("#51CF66"),
        < 70 => Get("#FCC419"),
        < 85 => Get("#FF922B"),
        _ => Get("#FF6B6B")
    };

    public static SolidColorBrush GetUtilBrush(int util) => util switch
    {
        < 30 => Get("#51CF66"),
        < 70 => Get("#FCC419"),
        < 90 => Get("#FF922B"),
        _ => Get("#FF6B6B")
    };

    public static SolidColorBrush GetFanBrush(int fan) => fan switch
    {
        0 => Get("#5C5F66"),
        < 30 => Get("#51CF66"),
        < 60 => Get("#4DABF7"),
        < 85 => Get("#FCC419"),
        _ => Get("#FF922B")
    };
}
