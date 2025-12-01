using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SkiaSharp;

namespace Unai.ExtendedBinaryWaterfall;

// SKCanvas 扩展方法
public static class Extensions
{
	// 可复用的 SKPaint 对象（避免每次调用创建新对象）
	// 进度条画笔（使用方形端点）
	private static readonly SKPaint _progressBarPaint = new()
	{
		IsAntialias = true,
		Style = SKPaintStyle.Stroke,
		StrokeCap = SKStrokeCap.Butt  // 方形端点
	};
	
	// 文本渲染缓存（预渲染的文本位图）- 使用 LRU 策略
	private static readonly Dictionary<int, (SKBitmap bitmap, long lastUsed)> _textRenderCache = new();
	private const int MaxTextCacheSize = 1024;  // 增大缓存容量
	private const int CacheCleanupBatch = 256;  // 每次清理的数量
	private static long _cacheAccessCounter = 0;
	// LRU 清理复用数组（避免每次清理时分配）
	private static int[] _lruRemoveKeys = null;
	private static long[] _lruRemoveValues = null;
	
	// 可复用的文本绘制 Paint
	private static readonly SKPaint _textPaint = new()
	{
		IsAntialias = true,
		SubpixelText = true,
		Style = SKPaintStyle.Fill
	};

	// 绘制进度条，支持可选的不透明度参数（用于淡入淡出效果）
	public static void DrawProgressBar(this SKCanvas canvas, float percent, int x1, int x2, float y, float opacity = 1f)
	{
		percent = Math.Clamp(percent, 0, 1);
		opacity = Math.Clamp(opacity, 0f, 1f);

		// 根据 opacity 调整颜色的 alpha 通道
		byte bgAlpha = (byte)(255 * opacity * 0.5f);  // 背景色半透明
		byte fgAlpha = (byte)(255 * opacity);          // 前景色

		_progressBarPaint.StrokeWidth = 8;
		
		// 绘制背景
		_progressBarPaint.Color = new SKColor(32, 32, 32, bgAlpha);
		canvas.DrawLine(x1, y, x2, y, _progressBarPaint);
		
		// 绘制前景（进度）
		_progressBarPaint.Color = new SKColor(192, 192, 192, fgAlpha);
		canvas.DrawLine(x1, y, x1 + (x2 - x1) * percent, y, _progressBarPaint);
	}

	// 绘制预缓存的文本（支持多行，增加\n分行）
	public static void DrawTextAndCache(this SKCanvas canvas, SKTypeface typeface, float fontSize, 
		string text, float x, float y, SKColor color, 
		HorizontalAlign hAlign = HorizontalAlign.Left, 
		VerticalAlign vAlign = VerticalAlign.Top)
	{
		if (string.IsNullOrEmpty(text)) return;
		
		// 处理多行文本：按 \n 分割
		string[] lines = text.Split('\n');
		if (lines.Length > 1)
		{
			// 多行文本：逐行绘制
			float lineHeight = fontSize * 1.2f;
			float totalHeight = lineHeight * lines.Length;
			
			// 根据垂直对齐计算起始 Y
			float startY = y;
			if (vAlign == VerticalAlign.Center)
				startY -= totalHeight / 2f;
			else if (vAlign == VerticalAlign.Bottom)
				startY -= totalHeight;
			
			for (int i = 0; i < lines.Length; i++)
			{
				if (!string.IsNullOrEmpty(lines[i]))
				{
					DrawTextAndCacheSingleLine(canvas, typeface, fontSize, lines[i], x, startY + i * lineHeight, color, hAlign, VerticalAlign.Top);
				}
			}
			return;
		}
		
		// 单行文本
		DrawTextAndCacheSingleLine(canvas, typeface, fontSize, text, x, y, color, hAlign, vAlign);
	}
	
	// 绘制单行文本（内部方法）
	private static void DrawTextAndCacheSingleLine(SKCanvas canvas, SKTypeface typeface, float fontSize, 
		string text, float x, float y, SKColor color, 
		HorizontalAlign hAlign, VerticalAlign vAlign)
	{
		if (string.IsNullOrEmpty(text)) return;
		
		// 计算缓存 key（基于文本、字体大小、颜色）
		int hash = HashCode.Combine(text, typeface?.FamilyName ?? "", fontSize, (uint)color);
		
		SKBitmap bitmap;
		long accessTime = Interlocked.Increment(ref _cacheAccessCounter);
		
		if (_textRenderCache.TryGetValue(hash, out var cached))
		{
			// 缓存命中，更新访问时间
			bitmap = cached.bitmap;
			_textRenderCache[hash] = (bitmap, accessTime);
		}
		else
		{
			// 缓存未命中，预渲染文本到位图
			
			// LRU 清理：使用无分配方式找出最旧条目
			if (_textRenderCache.Count >= MaxTextCacheSize)
			{
				// 复用静态数组存储待删除的 key（避免 LINQ 分配）
				if (_lruRemoveKeys == null || _lruRemoveKeys.Length < CacheCleanupBatch)
				{
					_lruRemoveKeys = new int[CacheCleanupBatch];
					_lruRemoveValues = new long[CacheCleanupBatch];
				}
				
				// 初始化为最大值
				for (int i = 0; i < CacheCleanupBatch; i++)
				{
					_lruRemoveValues[i] = long.MaxValue;
				}
				
				// 遍历一次，找出最旧的 CacheCleanupBatch 个条目
				foreach (var kv in _textRenderCache)
				{
					long lastUsed = kv.Value.lastUsed;
					// 检查是否比当前最大的还小
					if (lastUsed < _lruRemoveValues[CacheCleanupBatch - 1])
					{
						// 插入排序：找到合适位置
						int insertPos = CacheCleanupBatch - 1;
						while (insertPos > 0 && lastUsed < _lruRemoveValues[insertPos - 1])
						{
							_lruRemoveKeys[insertPos] = _lruRemoveKeys[insertPos - 1];
							_lruRemoveValues[insertPos] = _lruRemoveValues[insertPos - 1];
							insertPos--;
						}
						_lruRemoveKeys[insertPos] = kv.Key;
						_lruRemoveValues[insertPos] = lastUsed;
					}
				}
				
				// 移除找到的条目
				for (int i = 0; i < CacheCleanupBatch && _lruRemoveValues[i] != long.MaxValue; i++)
				{
					int key = _lruRemoveKeys[i];
					if (_textRenderCache.TryGetValue(key, out var entry))
					{
						entry.bitmap.Dispose();
						_textRenderCache.Remove(key);
					}
				}
			}
			
			// 配置画笔
			_textPaint.Typeface = typeface;
			_textPaint.TextSize = fontSize;
			_textPaint.Color = color;
			
			// 测量文本边界
			var bounds = new SKRect();
			_textPaint.MeasureText(text, ref bounds);
			
			int width = (int)Math.Ceiling(bounds.Width) + 4;
			int height = (int)Math.Ceiling(-bounds.Top + bounds.Bottom) + 4;
			if (width < 1) width = 1;
			if (height < 1) height = 1;
			
			// 创建位图并渲染文本
			bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
			using var tempCanvas = new SKCanvas(bitmap);
			tempCanvas.Clear(SKColors.Transparent);
			tempCanvas.DrawText(text, -bounds.Left + 2, -bounds.Top + 2, _textPaint);
			
			_textRenderCache[hash] = (bitmap, accessTime);
		}
		
		// 计算绘制位置（根据对齐方式）
		float drawX = x;
		float drawY = y;
		
		if (hAlign == HorizontalAlign.Center)
			drawX -= bitmap.Width / 2f;
		else if (hAlign == HorizontalAlign.Right)
			drawX -= bitmap.Width;
		
		if (vAlign == VerticalAlign.Center)
			drawY -= bitmap.Height / 2f;
		else if (vAlign == VerticalAlign.Bottom)
			drawY -= bitmap.Height;
		
		canvas.DrawBitmap(bitmap, drawX, drawY);
	}
	
	// 清理文本缓存（在需要时调用，如字体变化）
	public static void ClearTextRenderCache()
	{
		foreach (var entry in _textRenderCache.Values)
			entry.bitmap.Dispose();
		_textRenderCache.Clear();
	}

	// BinaryReader 扩展方法（保持不变）
	public static string ReadString(this BinaryReader br, int length, Encoding textEncoding = null)
	{
		textEncoding ??= Encoding.ASCII;
		return textEncoding.GetString(br.ReadBytes(length));
	}

	public static string ReadCString(this BinaryReader br)
	{
		StringBuilder sb = new();

		byte b;
		while ((b = br.ReadByte()) != 0)
		{
			sb.Append((char)b);
		}

		return sb.ToString();
	}

	public static BinaryReader SkipCString(this BinaryReader br, int count = 1)
	{
		int skipCount = 0;
		while (skipCount < count)
		{
			Console.Error.WriteLine($"{skipCount}/{count} {Utils.GetBufferHexString(br, 16)}");
			if (br.ReadByte() == 0) skipCount++;
		}
		return br;
	}
}
