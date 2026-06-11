using System.Diagnostics;
using System.Text.RegularExpressions;

namespace GpuMonitor;

/// <summary>
/// SSH 服务——使用 Windows 自带 ssh.exe 远程执行 nvidia-smi
/// </summary>
public class SshService
{
    public string Host { get; set; } = "";
    public string UserName { get; set; } = "";
    public int Port { get; set; } = 22;
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// 执行 nvidia-smi 命令并解析结果
    /// </summary>
    public async Task<NvidiaSmiOutput> QueryGpuAsync()
    {
        var result = new NvidiaSmiOutput();

        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(UserName))
        {
            result.Success = false;
            result.ErrorMessage = "请先配置 SSH 连接信息（主机、用户名）";
            return result;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ssh",
                Arguments = BuildSshArgs("nvidia-smi"),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            var completed = process.WaitForExit(TimeSpan.FromSeconds(TimeoutSeconds));
            if (!completed)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                result.Success = false;
                result.ErrorMessage = $"SSH 连接超时（{TimeoutSeconds}秒）";
                return result;
            }

            var stdout = await outputTask;
            var stderr = await errorTask;

            if (process.ExitCode != 0 || !string.IsNullOrEmpty(stderr))
            {
                // nvidia-smi 输出到 stderr 有时也正常，尝试从 stdout 解析
                if (string.IsNullOrWhiteSpace(stdout) && !string.IsNullOrEmpty(stderr))
                {
                    stdout = stderr;
                }

                if (string.IsNullOrWhiteSpace(stdout))
                {
                    result.Success = false;
                    result.ErrorMessage = $"SSH 执行失败 (exit={process.ExitCode}): {stderr}";
                    return result;
                }
            }

            result = ParseNvidiaSmi(stdout);
            result.Success = result.Gpus.Count > 0;
            if (!result.Success && string.IsNullOrEmpty(result.ErrorMessage))
                result.ErrorMessage = "未检测到 GPU 信息";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = $"连接错误: {ex.Message}";
        }

        return result;
    }

    /// <summary>
    /// 测试 SSH 连接
    /// </summary>
    public async Task<(bool success, string message)> TestConnectionAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ssh",
                Arguments = BuildSshArgs("echo connected"),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            var completed = process.WaitForExit(TimeSpan.FromSeconds(TimeoutSeconds));

            if (!completed)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (false, "连接超时");
            }

            if (process.ExitCode == 0)
                return (true, "连接成功！");
            else
                return (false, $"连接失败: {stderr}");
        }
        catch (Exception ex)
        {
            return (false, $"连接错误: {ex.Message}");
        }
    }

    /// <summary>
    /// 解析 nvidia-smi 输出
    /// </summary>
    private static NvidiaSmiOutput ParseNvidiaSmi(string raw)
    {
        var output = new NvidiaSmiOutput { RawOutput = raw };

        // 提取驱动版本和 CUDA 版本
        var driverMatch = Regex.Match(raw, @"Driver Version:\s*([\d.]+)");
        if (driverMatch.Success)
            output.DriverVersion = driverMatch.Groups[1].Value;

        var cudaMatch = Regex.Match(raw, @"CUDA Version:\s*([\d.]+)");
        if (cudaMatch.Success)
            output.CudaVersion = cudaMatch.Groups[1].Value;

        // 解析每一个 GPU
        // nvidia-smi 表格中 GPU 信息的行模式：
        // |   0  NVIDIA GeForce ...    Off | 00000000:01:00.0  On |                  N/A |
        // | 30%   65C    P2    150W / 300W |   8000MiB / 24576MiB |     85%      Default |
        // 这两行构成一张 GPU 的完整信息

        var lines = raw.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // 匹配第一行：GPU 索引和名称
            // |   0  NVIDIA GeForce RTX 4090 ...    Off |
            var gpuHeaderMatch = Regex.Match(line, @"\|\s+(\d+)\s+(.{3,}?)\s{2,}(\w+)\s+\|");
            if (gpuHeaderMatch.Success)
            {
                var gpu = new GpuInfo
                {
                    Index = int.Parse(gpuHeaderMatch.Groups[1].Value),
                    Name = gpuHeaderMatch.Groups[2].Value.Trim()
                };

                // 第二行包含风扇、温度、功耗、显存、利用率
                if (i + 1 < lines.Length)
                {
                    var detailLine = lines[i + 1];
                    ParseGpuDetailLine(detailLine, gpu);
                }

                output.Gpus.Add(gpu);
                i++; // 跳过已解析的第二行
            }
        }

        // 备用解析：尝试用 --query-compute-apps 或更灵活的方式
        if (output.Gpus.Count == 0)
        {
            // 尝试更宽松的匹配
            TryLooseParse(raw, output);
        }

        return output;
    }

    /// <summary>
    /// 解析 GPU 详情行：风扇 温度 性能 功耗 | 显存 | 利用率
    /// </summary>
    private static void ParseGpuDetailLine(string line, GpuInfo gpu)
    {
        // | 30%   65C    P2    150W / 300W |   8000MiB / 24576MiB |     85%      Default |
        var match = Regex.Match(line,
            @"\|\s*(\d+)%\s+(\d+)C\s+(P\d+)\s+(\d+W)\s*/\s*(\d+W)\s*\|\s*(\d+MiB)\s*/\s*(\d+MiB)\s*\|\s*(\d+)%\s+(.+)");

        if (match.Success)
        {
            gpu.FanSpeed = int.Parse(match.Groups[1].Value);
            gpu.Temperature = int.Parse(match.Groups[2].Value);
            gpu.PerformanceState = match.Groups[3].Value;
            gpu.PowerUsage = $"{match.Groups[4].Value} / {match.Groups[5].Value}";
            gpu.MemoryUsage = $"{match.Groups[6].Value} / {match.Groups[7].Value}";
            gpu.GpuUtilization = int.Parse(match.Groups[8].Value);
            gpu.ComputeMode = match.Groups[9].Value.Trim();
        }
        else
        {
            // 尝试宽松匹配
            var fanMatch = Regex.Match(line, @"(\d+)%");
            var tempMatch = Regex.Match(line, @"(\d+)C");
            var perfMatch = Regex.Match(line, @"(P\d+)");
            var powerMatch = Regex.Match(line, @"(\d+W)\s*/\s*(\d+W)");
            var memMatch = Regex.Match(line, @"(\d+MiB)\s*/\s*(\d+MiB)");
            var utilMatch = Regex.Match(line, @"(\d+)%");

            if (fanMatch.Success) gpu.FanSpeed = int.Parse(fanMatch.Groups[1].Value);
            if (tempMatch.Success) gpu.Temperature = int.Parse(tempMatch.Groups[1].Value);
            if (perfMatch.Success) gpu.PerformanceState = perfMatch.Groups[1].Value;
            if (powerMatch.Success) gpu.PowerUsage = $"{powerMatch.Groups[1].Value} / {powerMatch.Groups[2].Value}";
            if (memMatch.Success) gpu.MemoryUsage = $"{memMatch.Groups[1].Value} / {memMatch.Groups[2].Value}";
            if (utilMatch.Success) gpu.GpuUtilization = int.Parse(utilMatch.Groups[1].Value);
        }
    }

    /// <summary>
    /// 当标准表格解析失败时的备用宽松解析
    /// </summary>
    private static void TryLooseParse(string raw, NvidiaSmiOutput output)
    {
        // 查找所有 GPU 行
        var gpuMatches = Regex.Matches(raw, @"(\d+)\s+(NVIDIA\s+[\w\s]+?)\s+(On|Off)");
        foreach (Match m in gpuMatches)
        {
            output.Gpus.Add(new GpuInfo
            {
                Index = int.Parse(m.Groups[1].Value),
                Name = m.Groups[2].Value.Trim()
            });
        }
    }

    private string BuildSshArgs(string command)
    {
        var portPart = Port != 22 ? $" -p {Port}" : "";
        return $"-o StrictHostKeyChecking=no -o ConnectTimeout={TimeoutSeconds}{portPart} {UserName}@{Host} \"{command}\"";
    }
}
