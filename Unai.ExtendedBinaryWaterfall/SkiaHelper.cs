using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Unai.ExtendedBinaryWaterfall;

// SkiaSharp 辅助类：封装常用绘图操作，确保正确的内存管理和对象复用
public sealed class SkiaHelper : IDisposable
{
    // 可复用的 SKPaint 对象池（避免每帧创建）
    private readonly SKPaint _fillPaint;
    private readonly SKPaint _strokePaint;
    private readonly SKPaint _textPaint;
    private readonly SKPaint _imagePaint;
    
    // 字体缓存（SKTypeface 是线程安全且可复用的）
    private readonly Dictionary<string, SKTypeface> _typefaceCache = new();
    
    // 文本测量缓存（避免重复测量相同文本）
    private readonly Dictionary<(string text, float size), float> _textWidthCache = new();
    private const int MaxTextCacheSize = 512;
    
    // 文本渲染缓存（预渲染的文本位图）
    private readonly Dictionary<int, SKBitmap> _textBitmapCache = new();
    private const int MaxTextBitmapCacheSize = 256;
    
    private bool _disposed;
    
    public SkiaHelper()
    {
        // 初始化可复用的 Paint 对象
        _fillPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        
        _strokePaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };
        
        _textPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            SubpixelText = true
        };
        
        _imagePaint = new SKPaint
        {
            IsAntialias = false,
            FilterQuality = SKFilterQuality.None  // 最近邻插值（像素风格）
        };
    }
    
    // 获取或创建字体
    public SKTypeface GetTypeface(string fontName)
    {
        if (_typefaceCache.TryGetValue(fontName, out var cached))
            return cached;
        
        var typeface = SKTypeface.FromFamilyName(fontName) ?? SKTypeface.Default;
        _typefaceCache[fontName] = typeface;
        return typeface;
    }
    
    // 测量文本宽度（带缓存）
    public float MeasureTextWidth(string text, SKTypeface typeface, float fontSize)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        
        var key = (text, fontSize);
        if (_textWidthCache.TryGetValue(key, out float width))
            return width;
        
        // 清理缓存
        if (_textWidthCache.Count >= MaxTextCacheSize)
            _textWidthCache.Clear();
        
        _textPaint.Typeface = typeface;
        _textPaint.TextSize = fontSize;
        width = _textPaint.MeasureText(text);
        _textWidthCache[key] = width;
        return width;
    }
    
    // 测量文本边界
    public SKRect MeasureTextBounds(string text, SKTypeface typeface, float fontSize)
    {
        if (string.IsNullOrEmpty(text)) return SKRect.Empty;
        
        _textPaint.Typeface = typeface;
        _textPaint.TextSize = fontSize;
        var bounds = new SKRect();
        _textPaint.MeasureText(text, ref bounds);
        return bounds;
    }
    
    // 填充矩形
    public void FillRect(SKCanvas canvas, SKRect rect, SKColor color)
    {
        _fillPaint.Color = color;
        canvas.DrawRect(rect, _fillPaint);
    }
    
    // 填充矩形（带透明度）
    public void FillRect(SKCanvas canvas, SKRect rect, SKColor color, byte alpha)
    {
        _fillPaint.Color = color.WithAlpha(alpha);
        canvas.DrawRect(rect, _fillPaint);
    }
    
    // 描边矩形
    public void StrokeRect(SKCanvas canvas, SKRect rect, SKColor color, float strokeWidth)
    {
        _strokePaint.Color = color;
        _strokePaint.StrokeWidth = strokeWidth;
        canvas.DrawRect(rect, _strokePaint);
    }
    
    // 绘制线条
    public void DrawLine(SKCanvas canvas, float x1, float y1, float x2, float y2, SKColor color, float strokeWidth)
    {
        _strokePaint.Color = color;
        _strokePaint.StrokeWidth = strokeWidth;
        canvas.DrawLine(x1, y1, x2, y2, _strokePaint);
    }
    
    // 绘制文本（左对齐）
    public void DrawText(SKCanvas canvas, string text, float x, float y, SKTypeface typeface, float fontSize, SKColor color)
    {
        if (string.IsNullOrEmpty(text)) return;
        
        _textPaint.Typeface = typeface;
        _textPaint.TextSize = fontSize;
        _textPaint.Color = color;
        canvas.DrawText(text, x, y, _textPaint);
    }
    
    // 绘制文本（水平居中）
    public void DrawTextCentered(SKCanvas canvas, string text, float x, float y, SKTypeface typeface, float fontSize, SKColor color)
    {
        if (string.IsNullOrEmpty(text)) return;
        
        _textPaint.Typeface = typeface;
        _textPaint.TextSize = fontSize;
        _textPaint.Color = color;
        
        float width = _textPaint.MeasureText(text);
        canvas.DrawText(text, x - width / 2, y, _textPaint);
    }
    
    // 绘制文本（右对齐）
    public void DrawTextRight(SKCanvas canvas, string text, float x, float y, SKTypeface typeface, float fontSize, SKColor color)
    {
        if (string.IsNullOrEmpty(text)) return;
        
        _textPaint.Typeface = typeface;
        _textPaint.TextSize = fontSize;
        _textPaint.Color = color;
        
        float width = _textPaint.MeasureText(text);
        canvas.DrawText(text, x - width, y, _textPaint);
    }
    
    // 绘制文本（带垂直居中）
    public void DrawTextVerticalCenter(SKCanvas canvas, string text, float x, float y, SKTypeface typeface, float fontSize, SKColor color, HorizontalAlign align = HorizontalAlign.Left)
    {
        if (string.IsNullOrEmpty(text)) return;
        
        _textPaint.Typeface = typeface;
        _textPaint.TextSize = fontSize;
        _textPaint.Color = color;
        
        // 获取字体度量以计算垂直居中
        var metrics = _textPaint.FontMetrics;
        float textHeight = metrics.Descent - metrics.Ascent;
        float drawY = y - metrics.Ascent - textHeight / 2;
        
        float drawX = x;
        if (align == HorizontalAlign.Center)
        {
            drawX = x - _textPaint.MeasureText(text) / 2;
        }
        else if (align == HorizontalAlign.Right)
        {
            drawX = x - _textPaint.MeasureText(text);
        }
        
        canvas.DrawText(text, drawX, drawY, _textPaint);
    }
    
    // 绘制预缓存的文本（适用于频繁绘制的相同文本）
    public void DrawCachedText(SKCanvas canvas, string text, float x, float y, SKTypeface typeface, float fontSize, SKColor color, HorizontalAlign hAlign = HorizontalAlign.Left, VerticalAlign vAlign = VerticalAlign.Top)
    {
        if (string.IsNullOrEmpty(text)) return;
        
        // 计算缓存 key
        int hash = HashCode.Combine(text, typeface?.FamilyName, fontSize, (uint)color);
        
        if (!_textBitmapCache.TryGetValue(hash, out var cachedBitmap))
        {
            // 清理缓存
            if (_textBitmapCache.Count >= MaxTextBitmapCacheSize)
            {
                foreach (var bmp in _textBitmapCache.Values)
                    bmp.Dispose();
                _textBitmapCache.Clear();
            }
            
            // 测量文本边界
            _textPaint.Typeface = typeface;
            _textPaint.TextSize = fontSize;
            _textPaint.Color = color;
            
            var bounds = new SKRect();
            _textPaint.MeasureText(text, ref bounds);
            
            int width = (int)Math.Ceiling(bounds.Width) + 4;
            int height = (int)Math.Ceiling(-bounds.Top + bounds.Bottom) + 4;
            if (width < 1) width = 1;
            if (height < 1) height = 1;
            
            // 创建位图并渲染文本
            cachedBitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var tempCanvas = new SKCanvas(cachedBitmap);
            tempCanvas.Clear(SKColors.Transparent);
            tempCanvas.DrawText(text, -bounds.Left + 2, -bounds.Top + 2, _textPaint);
            
            _textBitmapCache[hash] = cachedBitmap;
        }
        
        // 计算绘制位置
        float drawX = x;
        float drawY = y;
        
        if (hAlign == HorizontalAlign.Center)
            drawX -= cachedBitmap.Width / 2f;
        else if (hAlign == HorizontalAlign.Right)
            drawX -= cachedBitmap.Width;
        
        if (vAlign == VerticalAlign.Center)
            drawY -= cachedBitmap.Height / 2f;
        else if (vAlign == VerticalAlign.Bottom)
            drawY -= cachedBitmap.Height;
        
        canvas.DrawBitmap(cachedBitmap, drawX, drawY);
    }
    
    // 绘制位图（最近邻插值，适合像素风格）
    public void DrawBitmapNearestNeighbor(SKCanvas canvas, SKBitmap bitmap, SKRect destRect)
    {
        canvas.DrawBitmap(bitmap, destRect, _imagePaint);
    }
    
    // 绘制位图（带透明度）
    public void DrawBitmapWithAlpha(SKCanvas canvas, SKBitmap bitmap, float x, float y, byte alpha)
    {
        _imagePaint.Color = new SKColor(255, 255, 255, alpha);
        canvas.DrawBitmap(bitmap, x, y, _imagePaint);
        _imagePaint.Color = SKColors.White;  // 重置
    }
    
    // 创建线性渐变着色器
    public SKShader CreateLinearGradient(SKPoint start, SKPoint end, SKColor[] colors, float[] positions)
    {
        return SKShader.CreateLinearGradient(start, end, colors, positions, SKShaderTileMode.Clamp);
    }
    
    // 使用渐变填充矩形
    public void FillRectWithGradient(SKCanvas canvas, SKRect rect, SKPoint gradientStart, SKPoint gradientEnd, SKColor[] colors, float[] positions)
    {
        using var shader = CreateLinearGradient(gradientStart, gradientEnd, colors, positions);
        _fillPaint.Shader = shader;
        canvas.DrawRect(rect, _fillPaint);
        _fillPaint.Shader = null;  // 重置
    }
    
    // 清理文本缓存
    public void ClearTextCache()
    {
        _textWidthCache.Clear();
        foreach (var bmp in _textBitmapCache.Values)
            bmp.Dispose();
        _textBitmapCache.Clear();
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _fillPaint?.Dispose();
        _strokePaint?.Dispose();
        _textPaint?.Dispose();
        _imagePaint?.Dispose();
        
        foreach (var typeface in _typefaceCache.Values)
            typeface?.Dispose();
        _typefaceCache.Clear();
        
        foreach (var bmp in _textBitmapCache.Values)
            bmp?.Dispose();
        _textBitmapCache.Clear();
        
        _textWidthCache.Clear();
    }
}

// 水平对齐枚举
public enum HorizontalAlign
{
    Left,
    Center,
    Right
}

// 垂直对齐枚举
public enum VerticalAlign
{
    Top,
    Center,
    Bottom
}
