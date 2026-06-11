namespace GpuMonitor;

/// <summary>
/// GPU 信息数据模型，对应 nvidia-smi 输出的每一张显卡
/// </summary>
public class GpuInfo
{
    /// <summary>GPU 索引号</summary>
    public int Index { get; set; }

    /// <summary>GPU 名称（如 NVIDIA GeForce RTX 4090）</summary>
    public string Name { get; set; } = "";

    /// <summary>风扇转速百分比</summary>
    public int FanSpeed { get; set; }

    /// <summary>温度（摄氏度）</summary>
    public int Temperature { get; set; }

    /// <summary>性能状态（P0-P12）</summary>
    public string PerformanceState { get; set; } = "";

    /// <summary>当前功耗 / 最大功耗（瓦）</summary>
    public string PowerUsage { get; set; } = "";

    /// <summary>显存使用量 / 总显存</summary>
    public string MemoryUsage { get; set; } = "";

    /// <summary>GPU 利用率百分比</summary>
    public int GpuUtilization { get; set; }

    /// <summary>计算模式</summary>
    public string ComputeMode { get; set; } = "";

    /// <summary>GPU-Util 原始字符串</summary>
    public string GpuUtilizationRaw { get; set; } = "";
}

/// <summary>
/// nvidia-smi 整体输出信息
/// </summary>
public class NvidiaSmiOutput
{
    /// <summary>驱动版本</summary>
    public string DriverVersion { get; set; } = "";

    /// <summary>CUDA 版本</summary>
    public string CudaVersion { get; set; } = "";

    /// <summary>GPU 列表</summary>
    public List<GpuInfo> Gpus { get; set; } = new();

    /// <summary>原始输出文本</summary>
    public string RawOutput { get; set; } = "";

    /// <summary>是否获取成功</summary>
    public bool Success { get; set; }

    /// <summary>错误信息</summary>
    public string ErrorMessage { get; set; } = "";
}
