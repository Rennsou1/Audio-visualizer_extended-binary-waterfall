using System;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.Common;
using OpenTK.Mathematics;
using SkiaSharp;

namespace Unai.ExtendedBinaryWaterfall;

// GPU 加速渲染器：使用 OpenGL + SkiaSharp GRContext 实现硬件加速
public class GpuRenderer : IDisposable
{
    private GameWindow _window;          // 隐藏的 OpenGL 窗口
    private GRContext _grContext;        // SkiaSharp GPU 上下文
    private SKSurface _gpuSurface;       // GPU 渲染表面
    private SKImageInfo _surfaceInfo;    // 表面信息
    private bool _initialized = false;
    private bool _disposed = false;
    
    // 是否成功初始化 GPU 渲染
    public bool IsGpuEnabled => _initialized && _grContext != null;
    
    // GPU 渲染表面
    public SKSurface Surface => _gpuSurface;
    
    // 画布
    public SKCanvas Canvas => _gpuSurface?.Canvas;
    
    // 初始化 GPU 渲染（创建隐藏窗口和 OpenGL 上下文）
    public bool Initialize(int width, int height)
    {
        if (_initialized) return IsGpuEnabled;
        
        try
        {
            Logger.Info("正在初始化 GPU 渲染...");
            
            // 创建隐藏的 OpenGL 窗口（用于获取 OpenGL 上下文）
            var windowSettings = new NativeWindowSettings
            {
                ClientSize = new Vector2i(1, 1),  // 最小尺寸
                StartVisible = false,              // 隐藏窗口
                WindowBorder = WindowBorder.Hidden,
                Title = "SkiaSharp GPU Context",
                Flags = ContextFlags.Default,     // 默认上下文
                API = ContextAPI.OpenGL,
                APIVersion = new Version(3, 3),   // OpenGL 3.3
                Profile = ContextProfile.Core
            };
            
            var gameSettings = GameWindowSettings.Default;
            
            _window = new GameWindow(gameSettings, windowSettings);
            _window.MakeCurrent();
            
            // 创建 SkiaSharp GRContext（连接到 OpenGL）
            var glInterface = GRGlInterface.Create();
            if (glInterface == null)
            {
                Logger.Warning("无法创建 OpenGL 接口，回退到 CPU 渲染");
                Cleanup();
                return false;
            }
            
            _grContext = GRContext.CreateGl(glInterface);
            if (_grContext == null)
            {
                Logger.Warning("无法创建 GRContext，回退到 CPU 渲染");
                Cleanup();
                return false;
            }
            
            // 创建 GPU 渲染表面
            _surfaceInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            _gpuSurface = SKSurface.Create(_grContext, false, _surfaceInfo);
            
            if (_gpuSurface == null)
            {
                Logger.Warning("无法创建 GPU Surface，回退到 CPU 渲染");
                Cleanup();
                return false;
            }
            
            _initialized = true;
            Logger.Info($"✓ GPU 渲染初始化成功 ({width}x{height})");
            
            // 获取 OpenGL 信息
            string renderer = GL.GetString(StringName.Renderer) ?? "Unknown";
            string version = GL.GetString(StringName.Version) ?? "Unknown";
            Logger.Info($"  OpenGL: {renderer}");
            Logger.Info($"  版本: {version}");
            
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning($"GPU 渲染初始化失败: {ex.Message}");
            Logger.Warning("回退到 CPU 软件渲染");
            Cleanup();
            return false;
        }
    }
    
    // 调整表面大小
    public void Resize(int width, int height)
    {
        if (!IsGpuEnabled) return;
        if (_surfaceInfo.Width == width && _surfaceInfo.Height == height) return;
        
        _gpuSurface?.Dispose();
        _surfaceInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _gpuSurface = SKSurface.Create(_grContext, false, _surfaceInfo);
    }
    
    // 刷新 GPU 上下文（提交绘制命令）
    public void Flush()
    {
        if (!IsGpuEnabled) return;
        _grContext?.Flush();
    }
    
    // 获取渲染结果为 SKBitmap（用于编码）
    public SKBitmap GetBitmap()
    {
        if (!IsGpuEnabled || _gpuSurface == null) return null;
        
        // 从 GPU Surface 读取像素到 CPU Bitmap
        var bitmap = new SKBitmap(_surfaceInfo);
        IntPtr pixels = bitmap.GetPixels();
        _gpuSurface.ReadPixels(_surfaceInfo, pixels, _surfaceInfo.RowBytes, 0, 0);
        return bitmap;
    }
    
    // 将渲染结果复制到现有 Bitmap
    public void CopyToBitmap(SKBitmap target)
    {
        if (!IsGpuEnabled || _gpuSurface == null || target == null) return;
        
        IntPtr pixels = target.GetPixels();
        _gpuSurface.ReadPixels(_surfaceInfo, pixels, _surfaceInfo.RowBytes, 0, 0);
    }
    
    private void Cleanup()
    {
        _gpuSurface?.Dispose();
        _gpuSurface = null;
        _grContext?.Dispose();
        _grContext = null;
        _window?.Dispose();
        _window = null;
        _initialized = false;
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
    }
}
