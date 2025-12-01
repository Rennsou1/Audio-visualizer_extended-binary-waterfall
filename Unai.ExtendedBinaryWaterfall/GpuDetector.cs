using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Unai.ExtendedBinaryWaterfall.Exporters;

namespace Unai.ExtendedBinaryWaterfall;

// GPU 类型枚举
public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel
}

// GPU 信息结构
public class GpuInfo
{
    public string Name { get; set; } = "";
    public GpuVendor Vendor { get; set; } = GpuVendor.Unknown;
    public bool SupportsNvenc { get; set; } = false;
    public bool SupportsAmf { get; set; } = false;
    public bool SupportsQsv { get; set; } = false;
}

// GPU 检测工具类：检测系统中的显卡并确定支持的硬件编码器
public static class GpuDetector
{
    private static List<GpuInfo> _cachedGpus = null;
    private static bool _hasNvidia = false;
    private static bool _hasAmd = false;
    private static bool _hasIntel = false;

    // 检测系统中的所有 GPU
    public static List<GpuInfo> DetectGpus()
    {
        if (_cachedGpus != null) return _cachedGpus;

        _cachedGpus = new List<GpuInfo>();

        try
        {
            // 仅在 Windows 上使用 WMI 检测
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                DetectGpusWindows();
            }
            else
            {
                // Linux/macOS 使用 lspci 或其他方式
                DetectGpusLinux();
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"GPU 检测失败: {ex.Message}");
        }

        // 设置标志
        _hasNvidia = _cachedGpus.Any(g => g.Vendor == GpuVendor.Nvidia);
        _hasAmd = _cachedGpus.Any(g => g.Vendor == GpuVendor.Amd);
        _hasIntel = _cachedGpus.Any(g => g.Vendor == GpuVendor.Intel);

        return _cachedGpus;
    }

    // Windows 下使用 WMI 检测 GPU
    [SupportedOSPlatform("windows")]
    private static void DetectGpusWindows()
    {
        using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
        foreach (ManagementObject obj in searcher.Get())
        {
            string name = obj["Name"]?.ToString() ?? "";
            string adapterCompatibility = obj["AdapterCompatibility"]?.ToString() ?? "";

            var gpu = new GpuInfo { Name = name };

            // 判断厂商
            string nameUpper = name.ToUpperInvariant();
            string compatUpper = adapterCompatibility.ToUpperInvariant();

            if (nameUpper.Contains("NVIDIA") || nameUpper.Contains("GEFORCE") || 
                nameUpper.Contains("QUADRO") || nameUpper.Contains("RTX") ||
                compatUpper.Contains("NVIDIA"))
            {
                gpu.Vendor = GpuVendor.Nvidia;
                // GTX 600 系列及以上支持 NVENC
                gpu.SupportsNvenc = IsNvencSupported(name);
            }
            else if (nameUpper.Contains("AMD") || nameUpper.Contains("RADEON") ||
                     nameUpper.Contains("ATI") || compatUpper.Contains("AMD") ||
                     compatUpper.Contains("ATI"))
            {
                gpu.Vendor = GpuVendor.Amd;
                // RX 400 系列及以上支持 AMF
                gpu.SupportsAmf = IsAmfSupported(name);
            }
            else if (nameUpper.Contains("INTEL") || compatUpper.Contains("INTEL"))
            {
                gpu.Vendor = GpuVendor.Intel;
                // Intel 6及以上 CPU 支持 QSV
                gpu.SupportsQsv = IsQsvSupported(name);
            }

            if (!string.IsNullOrEmpty(name))
            {
                _cachedGpus.Add(gpu);
                Logger.Info($"检测到 GPU: {name} ({gpu.Vendor})");
            }
        }
    }

    // Linux 下使用 lspci 检测 GPU
    private static void DetectGpusLinux()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "lspci",
                Arguments = "-v",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            // 简单解析 lspci 输出
            if (output.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            {
                _cachedGpus.Add(new GpuInfo
                {
                    Name = "NVIDIA GPU",
                    Vendor = GpuVendor.Nvidia,
                    SupportsNvenc = true
                });
            }
            if (output.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("ATI", StringComparison.OrdinalIgnoreCase))
            {
                _cachedGpus.Add(new GpuInfo
                {
                    Name = "AMD GPU",
                    Vendor = GpuVendor.Amd,
                    SupportsAmf = true
                });
            }
            if (output.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            {
                _cachedGpus.Add(new GpuInfo
                {
                    Name = "Intel GPU",
                    Vendor = GpuVendor.Intel,
                    SupportsQsv = true
                });
            }
        }
        catch
        {
            // 忽略错误
        }
    }

    // 检查 NVIDIA 显卡是否支持 NVENC
    private static bool IsNvencSupported(string name)
    {
        string upper = name.ToUpperInvariant();
        
        // RTX 系列始终支持
        if (upper.Contains("RTX")) return true;
        
        // GTX 系列：600 及以上
        if (upper.Contains("GTX"))
        {
            // 提取数字
            var match = System.Text.RegularExpressions.Regex.Match(upper, @"GTX\s*(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int model))
            {
                return model >= 600;
            }
        }
        
        // Quadro K 系列及以上
        if (upper.Contains("QUADRO")) return true;
        
        // 默认假设支持
        return upper.Contains("NVIDIA") || upper.Contains("GEFORCE");
    }

    // 检查 AMD 显卡是否支持 AMF
    private static bool IsAmfSupported(string name)
    {
        string upper = name.ToUpperInvariant();
        
        // RX 系列
        if (upper.Contains("RX"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(upper, @"RX\s*(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int model))
            {
                return model >= 400;
            }
        }
        
        // Vega 和 Navi 系列
        if (upper.Contains("VEGA") || upper.Contains("NAVI")) return true;
        
        return false;
    }

    // 检查 Intel GPU 是否支持 QSV
    private static bool IsQsvSupported(string name)
    {
        // Intel HD/UHD/Iris Graphics 通常支持 QSV
        string upper = name.ToUpperInvariant();
        return upper.Contains("HD GRAPHICS") || 
               upper.Contains("UHD GRAPHICS") || 
               upper.Contains("IRIS");
    }

    // 快速检查是否有 NVIDIA 显卡
    public static bool HasNvidiaGpu()
    {
        DetectGpus();
        return _hasNvidia;
    }

    // 快速检查是否有 AMD 显卡
    public static bool HasAmdGpu()
    {
        DetectGpus();
        return _hasAmd;
    }

    // 快速检查是否有 Intel GPU
    public static bool HasIntelGpu()
    {
        DetectGpus();
        return _hasIntel;
    }

    // 快速检查是否支持 NVENC
    public static bool SupportsNvenc()
    {
        DetectGpus();
        return _cachedGpus.Any(g => g.SupportsNvenc);
    }

    // 快速检查是否支持 AMF
    public static bool SupportsAmf()
    {
        DetectGpus();
        return _cachedGpus.Any(g => g.SupportsAmf);
    }

    // 快速检查是否支持 QSV
    public static bool SupportsQsv()
    {
        DetectGpus();
        return _cachedGpus.Any(g => g.SupportsQsv);
    }

    // 获取推荐的硬件加速类型
    public static HardwareAccelType GetRecommendedHardwareAccel()
    {
        DetectGpus();
        
        // 优先级：NVENC > QSV > AMF > None
        if (SupportsNvenc()) return HardwareAccelType.NVENC;
        if (SupportsQsv()) return HardwareAccelType.QSV;
        if (SupportsAmf()) return HardwareAccelType.AMF;
        
        return HardwareAccelType.None;
    }
}
