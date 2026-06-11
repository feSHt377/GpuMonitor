using System.Text.RegularExpressions;
using Renci.SshNet;

namespace GpuMonitor;

/// <summary>
/// SSH 服务——使用 SSH.NET 远程执行 nvidia-smi（支持密码认证）
/// </summary>
public class SshService
{
    // 静态编译正则（避免每次解析时重新编译）
    private static readonly Regex DriverVersionRegex = new(@"Driver Version:\s*([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex CudaVersionRegex = new(@"CUDA Version:\s*([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex GpuHeaderRegex = new(@"\|\s+(\d+)\s+(.{3,}?)\s{2,}(\w+)\s+\|", RegexOptions.Compiled);
    private static readonly Regex GpuDetailRegex = new(
        @"\|\s*(\d+)%\s+(\d+)C\s+(P\d+)\s+(\d+W)\s*/\s*(\d+W)\s*\|\s*(\d+MiB)\s*/\s*(\d+MiB)\s*\|\s*(\d+)%\s+(.+)",
        RegexOptions.Compiled);
    private static readonly Regex FanRegex = new(@"(\d+)%", RegexOptions.Compiled);
    private static readonly Regex TempRegex = new(@"(\d+)C", RegexOptions.Compiled);
    private static readonly Regex PerfRegex = new(@"(P\d+)", RegexOptions.Compiled);
    private static readonly Regex PowerRegex = new(@"(\d+W)\s*/\s*(\d+W)", RegexOptions.Compiled);
    private static readonly Regex MemRegex = new(@"(\d+MiB)\s*/\s*(\d+MiB)", RegexOptions.Compiled);
    private static readonly Regex LooseGpuRegex = new(@"(\d+)\s+(NVIDIA\s+[\w\s]+?)\s+(On|Off)", RegexOptions.Compiled);

    public string Host { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public int Port { get; set; } = 22;
    public int TimeoutSeconds { get; set; } = 8;

    public async Task<NvidiaSmiOutput> QueryGpuAsync(CancellationToken ct = default)
    {
        var result = new NvidiaSmiOutput();

        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(UserName))
        {
            result.Success = false;
            result.ErrorMessage = "请先配置 SSH 连接信息（主机、用户名、密码）";
            Log("[SKIP] 未配置 SSH 连接信息");
            return result;
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            result.Success = false;
            result.ErrorMessage = "请输入 SSH 密码";
            Log("[SKIP] 未提供密码");
            return result;
        }

        try
        {
            Log($"[SSH] 连接 {UserName}@{Host}:{Port} (密码认证) ...");
            Log($"[SSH] 超时设置: {TimeoutSeconds}秒");

            ct.ThrowIfCancellationRequested();

            using var client = new SshClient(Host, Port, UserName, Password);
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);

            Log("[SSH] 正在建立 SSH 连接...");
            await Task.Run(() => client.Connect(), ct);
            Log($"[SSH] ✓ SSH 连接已建立");

            if (!client.IsConnected)
            {
                result.Success = false;
                result.ErrorMessage = "SSH 连接失败";
                Log("[SSH] ✗ 连接失败");
                return result;
            }

            Log("[SSH] 执行命令: nvidia-smi");
            ct.ThrowIfCancellationRequested();

            using var cmd = client.CreateCommand("nvidia-smi");
            cmd.CommandTimeout = TimeSpan.FromSeconds(TimeoutSeconds);

            var stdout = await Task.Run(() => cmd.Execute(), ct);
            var stderr = cmd.Error ?? "";
            var exitCode = cmd.ExitStatus;
            Log($"[SSH] 命令完成: ExitCode={exitCode}, stdout={stdout?.Length ?? 0}字符, stderr={stderr.Length}字符");

            if (exitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            {
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    stdout = stderr;
                    Log("[SSH] stdout为空，使用stderr作为输出");
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = $"nvidia-smi 执行失败 (exit={exitCode}): {stderr}";
                    Log($"[SSH] ✗ 执行失败: {result.ErrorMessage}");
                    return result;
                }
            }

            client.Disconnect();
            Log("[SSH] 已断开连接");

            Log($"[PARSE] 开始解析 nvidia-smi 输出...");
            result = ParseNvidiaSmi(stdout ?? "");
            result.Success = result.Gpus.Count > 0;
            if (!result.Success && string.IsNullOrEmpty(result.ErrorMessage))
                result.ErrorMessage = "未检测到 GPU 信息";
            Log($"[PARSE] 完成: Success={result.Success}, GPU={result.Gpus.Count}, 驱动={result.DriverVersion}");
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.ErrorMessage = "请求已取消";
            Log("[SSH] ⚠ 请求已被取消");
        }
        catch (Renci.SshNet.Common.SshAuthenticationException ex)
        {
            result.Success = false;
            result.ErrorMessage = $"SSH 认证失败: {ex.Message}";
            Log($"[SSH] ✗ 认证失败: {ex.Message}");
        }
        catch (Renci.SshNet.Common.SshConnectionException ex)
        {
            result.Success = false;
            result.ErrorMessage = $"SSH 连接失败: {ex.Message}";
            Log($"[SSH] ✗ 连接失败: {ex.Message}");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = $"连接错误: {ex.Message}";
            Log($"[SSH] ✗ 异常: {ex.GetType().Name}: {ex.Message}");
        }

        return result;
    }

    public async Task<(bool success, string message)> TestConnectionAsync()
    {
        Log($"[TEST] 测试连接 {UserName}@{Host}:{Port} (密码认证) ...");

        if (string.IsNullOrWhiteSpace(Password))
        {
            Log("[TEST] ✗ 未提供密码");
            return (false, "请输入 SSH 密码");
        }

        try
        {
            using var client = new SshClient(Host, Port, UserName, Password);
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);

            Log("[TEST] 正在建立连接...");
            await Task.Run(() => client.Connect());

            if (client.IsConnected)
            {
                client.Disconnect();
                Log("[TEST] ✓ 连接成功");
                return (true, "连接成功！");
            }

            Log("[TEST] ✗ 连接失败");
            return (false, "连接失败");
        }
        catch (Renci.SshNet.Common.SshAuthenticationException ex)
        {
            Log($"[TEST] ✗ 认证失败: {ex.Message}");
            return (false, $"认证失败，请检查用户名/密码: {ex.Message}");
        }
        catch (Renci.SshNet.Common.SshConnectionException ex)
        {
            Log($"[TEST] ✗ 连接失败: {ex.Message}");
            return (false, $"无法连接主机: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log($"[TEST] ✗ 异常: {ex.Message}");
            return (false, $"连接错误: {ex.Message}");
        }
    }

    private static NvidiaSmiOutput ParseNvidiaSmi(string raw)
    {
        var output = new NvidiaSmiOutput { RawOutput = raw };

        // 打印原始输出前500字符用于调试
        Console.WriteLine($"[PARSE] 原始输出预览:\n{raw[..Math.Min(500, raw.Length)]}");

        var driverMatch = DriverVersionRegex.Match(raw);
        if (driverMatch.Success)
            output.DriverVersion = driverMatch.Groups[1].Value;

        var cudaMatch = CudaVersionRegex.Match(raw);
        if (cudaMatch.Success)
            output.CudaVersion = cudaMatch.Groups[1].Value;

        // 截断到 Processes 部分之前，避免把进程行误解析为 GPU
        var gpuSection = raw;
        var processesIdx = raw.IndexOf("Processes:", StringComparison.OrdinalIgnoreCase);
        if (processesIdx > 0)
        {
            gpuSection = raw[..processesIdx];
            Console.WriteLine($"[PARSE] 在位置 {processesIdx} 截断 Processes 部分, 剩余 {gpuSection.Length} 字符");
        }

        var lines = gpuSection.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        for (int i = 0; i < lines.Length; i++)
        {
            var gpuHeaderMatch = GpuHeaderRegex.Match(lines[i]);
            if (gpuHeaderMatch.Success)
            {
                var rawName = gpuHeaderMatch.Groups[2].Value.Trim();

                // 过滤掉非 GPU 行（如进程行中误匹配的 N/A）
                if (rawName.Equals("N/A", StringComparison.OrdinalIgnoreCase)
                    || rawName.StartsWith("N/A ", StringComparison.OrdinalIgnoreCase)
                    || rawName.All(c => c == ' ' || c == '-' || c == '='))
                {
                    Console.WriteLine($"[PARSE] 跳过非GPU行: '{rawName}'");
                    continue;
                }

                var gpu = new GpuInfo
                {
                    Index = int.Parse(gpuHeaderMatch.Groups[1].Value),
                    Name = rawName
                };

                if (i + 1 < lines.Length)
                    ParseGpuDetailLine(lines[i + 1], gpu);

                output.Gpus.Add(gpu);
                Console.WriteLine($"[PARSE] GPU[{gpu.Index}]: {gpu.Name} | {gpu.Temperature}°C | {gpu.GpuUtilization}% | 显存={gpu.MemoryUsage} | 功耗={gpu.PowerUsage}");
                i++;
            }
        }

        if (output.Gpus.Count == 0)
        {
            Console.WriteLine("[PARSE] 标准解析失败，尝试宽松匹配...");
            TryLooseParse(raw, output);
            Console.WriteLine($"[PARSE] 宽松匹配: {output.Gpus.Count} 张GPU");
        }

        return output;
    }

    private static void ParseGpuDetailLine(string line, GpuInfo gpu)
    {
        var match = GpuDetailRegex.Match(line);

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
            var fanMatch = FanRegex.Match(line);
            var tempMatch = TempRegex.Match(line);
            var perfMatch = PerfRegex.Match(line);
            var powerMatch = PowerRegex.Match(line);
            var memMatch = MemRegex.Match(line);
            var utilMatch = FanRegex.Match(line); // 重新搜索第一个%数字作为利用率

            if (fanMatch.Success) gpu.FanSpeed = int.Parse(fanMatch.Groups[1].Value);
            if (tempMatch.Success) gpu.Temperature = int.Parse(tempMatch.Groups[1].Value);
            if (perfMatch.Success) gpu.PerformanceState = perfMatch.Groups[1].Value;
            if (powerMatch.Success) gpu.PowerUsage = $"{powerMatch.Groups[1].Value} / {powerMatch.Groups[2].Value}";
            if (memMatch.Success) gpu.MemoryUsage = $"{memMatch.Groups[1].Value} / {memMatch.Groups[2].Value}";
            if (utilMatch.Success) gpu.GpuUtilization = int.Parse(utilMatch.Groups[1].Value);
        }
    }

    private static void TryLooseParse(string raw, NvidiaSmiOutput output)
    {
        var gpuMatches = LooseGpuRegex.Matches(raw);
        foreach (Match m in gpuMatches)
        {
            output.Gpus.Add(new GpuInfo
            {
                Index = int.Parse(m.Groups[1].Value),
                Name = m.Groups[2].Value.Trim()
            });
        }
    }

    private static void Log(string msg)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
    }
}

internal static class StringExtensions
{
    public static string Truncate(this string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";
}
