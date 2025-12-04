using System;
using System.Runtime.InteropServices;

namespace Unai.ExtendedBinaryWaterfall;

// libvgm P/Invoke 绑定
// 需要 libvgm.dll（从 https://github.com/ValleyBell/libvgm 编译）
public static unsafe class LibVgm
{
    private const string DllName = "libvgm";

    // 播放器状态
    public const byte PLAYSTATE_PLAY = 0x01;
    public const byte PLAYSTATE_PAUSE = 0x02;
    public const byte PLAYSTATE_END = 0x04;

    // 采样单位
    public const byte UNIT_SAMPLE = 0x00;
    public const byte UNIT_TICK = 0x01;

    // 32位立体声采样结构
    [StructLayout(LayoutKind.Sequential)]
    public struct Wave32Bs
    {
        public int Left;
        public int Right;
    }

    // 播放器歌曲信息
    [StructLayout(LayoutKind.Sequential)]
    public struct PlrSongInfo
    {
        public uint Format;
        public uint SampleRate;
        public uint TickRate;
        public uint TotalTicks;
        public uint LoopTicks;
        public uint VolGain;
        public uint DeviceCount;
    }

    // 设备信息
    [StructLayout(LayoutKind.Sequential)]
    public struct PlrDevInfo
    {
        public uint Id;
        public uint Type;
        public uint Instance;
        public uint Clock;
        public uint Core;
        public uint Volume;
        public uint MuteMask;
        public IntPtr Name;
    }

    // ========== 核心 API ==========

    // 创建 VGM 播放器实例
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr VgmPlayer_Create();

    // 销毁播放器实例
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void VgmPlayer_Destroy(IntPtr player);

    // 从内存加载 VGM 数据
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte VgmPlayer_LoadData(IntPtr player, byte* data, uint dataSize);

    // 卸载文件
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte VgmPlayer_Unload(IntPtr player);

    // 设置采样率
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte VgmPlayer_SetSampleRate(IntPtr player, uint sampleRate);

    // 获取采样率
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint VgmPlayer_GetSampleRate(IntPtr player);

    // 开始播放
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte VgmPlayer_Start(IntPtr player);

    // 停止播放
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte VgmPlayer_Stop(IntPtr player);

    // 重置播放器
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte VgmPlayer_Reset(IntPtr player);

    // 渲染音频采样
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint VgmPlayer_Render(IntPtr player, uint sampleCount, Wave32Bs* buffer);

    // 跳转到指定位置
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte VgmPlayer_Seek(IntPtr player, byte unit, uint position);

    // 获取播放状态
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte VgmPlayer_GetState(IntPtr player);

    // 获取当前位置
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint VgmPlayer_GetCurPos(IntPtr player, byte unit);

    // 获取当前循环次数
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint VgmPlayer_GetCurLoop(IntPtr player);

    // 获取总 Tick 数
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint VgmPlayer_GetTotalTicks(IntPtr player);

    // 获取循环 Tick 数
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint VgmPlayer_GetLoopTicks(IntPtr player);

    // 获取歌曲信息
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte VgmPlayer_GetSongInfo(IntPtr player, out PlrSongInfo info);

    // 获取设备数量
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint VgmPlayer_GetDeviceCount(IntPtr player);

    // 获取设备信息
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte VgmPlayer_GetDeviceInfo(IntPtr player, uint index, out PlrDevInfo info);

    // 设置设备静音
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte VgmPlayer_SetDeviceMute(IntPtr player, uint deviceId, uint muteMask);

    // 设置播放速度
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte VgmPlayer_SetPlaybackSpeed(IntPtr player, double speed);

    // 获取播放速度
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern double VgmPlayer_GetPlaybackSpeed(IntPtr player);

    // ========== GD3 标签 API ==========

    // 获取 GD3 标签（返回 null 结尾的字符串数组）
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr VgmPlayer_GetTags(IntPtr player);

    // ========== 辅助方法 ==========

    // 缓存 DLL 路径（用于 NativeLibrary resolver）
    private static string _resolvedDllPath = null;
    private static bool _resolverRegistered = false;
    
    // 检查 libvgm.dll 是否可用
    public static bool IsAvailable()
    {
        // 获取 exe 所在目录
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        string dllPath = System.IO.Path.Combine(exeDir, "libvgm.dll");
        
        Logger.Info($"[LibVgm] 搜索路径: {exeDir}");
        Logger.Info($"[LibVgm] DLL 完整路径: {dllPath}");
        Logger.Info($"[LibVgm] 文件存在: {System.IO.File.Exists(dllPath)}");
        
        try
        {
            IntPtr handle = IntPtr.Zero;
            
            // 优先从 exe 目录加载（最可靠的方式）
            if (System.IO.File.Exists(dllPath))
            {
                try
                {
                    handle = NativeLibrary.Load(dllPath);
                }
                catch (Exception loadEx)
                {
                    Logger.Error($"[LibVgm] 加载 {dllPath} 失败: {loadEx.Message}");
                    // 继续尝试其他方式
                }
                if (handle != IntPtr.Zero)
                {
                    NativeLibrary.Free(handle);
                    _resolvedDllPath = dllPath;
                    RegisterDllResolver();
                    Logger.Info($"[LibVgm] 成功加载 libvgm.dll (从 {dllPath})");
                    return true;
                }
            }
            
            // 尝试直接加载
            if (NativeLibrary.TryLoad(DllName, out handle) ||
                NativeLibrary.TryLoad("libvgm.dll", out handle))
            {
                NativeLibrary.Free(handle);
                Logger.Info("[LibVgm] 成功加载 libvgm.dll (从系统路径)");
                return true;
            }
            
            Logger.Warning($"[LibVgm] libvgm.dll 未找到");
            return false;
        }
        catch (DllNotFoundException ex)
        {
            Logger.Error($"[LibVgm] DLL 未找到: {ex.Message}");
            return false;
        }
        catch (BadImageFormatException ex)
        {
            Logger.Error($"[LibVgm] DLL 格式错误 (32/64位不匹配?): {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error($"[LibVgm] 加载失败: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
            {
                Logger.Error($"[LibVgm] 内部错误: {ex.InnerException.Message}");
            }
            return false;
        }
    }
    
    // 注册 DLL 解析器，确保 P/Invoke 能找到 DLL
    private static void RegisterDllResolver()
    {
        if (_resolverRegistered || string.IsNullOrEmpty(_resolvedDllPath)) return;
        
        NativeLibrary.SetDllImportResolver(typeof(LibVgm).Assembly, (name, assembly, path) =>
        {
            if (name == DllName && !string.IsNullOrEmpty(_resolvedDllPath))
            {
                return NativeLibrary.Load(_resolvedDllPath);
            }
            return IntPtr.Zero;
        });
        _resolverRegistered = true;
    }

    // Tick 转采样数
    public static uint TickToSample(uint ticks, uint sampleRate)
    {
        return (uint)((ulong)ticks * sampleRate / 44100);
    }

    // 采样数转 Tick
    public static uint SampleToTick(uint samples, uint sampleRate)
    {
        return (uint)((ulong)samples * 44100 / sampleRate);
    }
}
