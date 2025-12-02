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
		StrokeCap = SKStrokeCap.Butt
	};
	
	// 文本渲染缓存（使用 SKTextBlob 替代位图）
	private static readonly Dictionary<int, (SKTextBlob blob, float width, float height, float baselineY, long lastUsed)> _textBlobCache = new();
	private const int MaxTextCacheSize = 2048;  // 增大缓存容量
	private const int CacheCleanupBatch = 512;  // 每次清理的数量
	private static long _cacheAccessCounter = 0;
	// LRU 清理复用数组（避免每次清理时分配）
	private static int[] _lruRemoveKeys = null;
	private static long[] _lruRemoveValues = null;
	
	// SKFont 缓存（避免每次调用 ToFont()）
	private static readonly Dictionary<int, SKFont> _fontCache = new();
	private const int MaxFontCacheSize = 32;
	
	// 动画文本位图序列缓存（用于大字号动画文本的多帧预渲染）
	// key = (text, fontSize, alpha), value = (bitmap, lastUsed)
	private static readonly Dictionary<int, (SKBitmap bitmap, long lastUsed)> _animTextBitmapCache = new();
	private const int MaxAnimTextCacheSize = 256;  // 动画位图缓存容量（每个文本约 8 个透明度级别）
	private const int AnimTextCleanupBatch = 64;   // 每次清理数量
	
	// 旧版位图缓存（保留用于兼容）
	private static readonly Dictionary<int, (SKBitmap bitmap, long lastUsed)> _textRenderCache = new();
	
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
	
	// 绘制单行文本（使用 SKTextBlob 缓存）
	private static void DrawTextAndCacheSingleLine(SKCanvas canvas, SKTypeface typeface, float fontSize, 
		string text, float x, float y, SKColor color, 
		HorizontalAlign hAlign, VerticalAlign vAlign)
	{
		if (string.IsNullOrEmpty(text)) return;
		
		// 缓存 key 不包含颜色（SKTextBlob 不绑定颜色）
		int hash = HashCode.Combine(text, typeface?.FamilyName ?? "", fontSize);
		
		SKTextBlob blob;
		float width, height, baselineY;
		long accessTime = Interlocked.Increment(ref _cacheAccessCounter);
		
		if (_textBlobCache.TryGetValue(hash, out var cached))
		{
			// 缓存命中，更新访问时间
			blob = cached.blob;
			width = cached.width;
			height = cached.height;
			baselineY = cached.baselineY;
			_textBlobCache[hash] = (blob, width, height, baselineY, accessTime);
		}
		else
		{
			// LRU 清理
			if (_textBlobCache.Count >= MaxTextCacheSize)
			{
				if (_lruRemoveKeys == null || _lruRemoveKeys.Length < CacheCleanupBatch)
				{
					_lruRemoveKeys = new int[CacheCleanupBatch];
					_lruRemoveValues = new long[CacheCleanupBatch];
				}
				
				for (int i = 0; i < CacheCleanupBatch; i++)
					_lruRemoveValues[i] = long.MaxValue;
				
				foreach (var kv in _textBlobCache)
				{
					long lastUsed = kv.Value.lastUsed;
					if (lastUsed < _lruRemoveValues[CacheCleanupBatch - 1])
					{
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
				
				for (int i = 0; i < CacheCleanupBatch && _lruRemoveValues[i] != long.MaxValue; i++)
				{
					int key = _lruRemoveKeys[i];
					if (_textBlobCache.TryGetValue(key, out var entry))
					{
						entry.blob.Dispose();
						_textBlobCache.Remove(key);
					}
				}
			}
			
			// 获取或创建缓存的 SKFont
			int fontHash = HashCode.Combine(typeface?.FamilyName ?? "", fontSize);
			if (!_fontCache.TryGetValue(fontHash, out var font))
			{
				// 配置画笔并创建字体
				_textPaint.Typeface = typeface;
				_textPaint.TextSize = fontSize;
				font = _textPaint.ToFont();
				
				// 限制字体缓存大小（简单清理策略）
				if (_fontCache.Count >= MaxFontCacheSize)
				{
					var oldestKey = _fontCache.Keys.First();
					_fontCache[oldestKey].Dispose();
					_fontCache.Remove(oldestKey);
				}
				_fontCache[fontHash] = font;
			}
			
			// 配置画笔并测量边界
			_textPaint.Typeface = typeface;
			_textPaint.TextSize = fontSize;
			var bounds = new SKRect();
			_textPaint.MeasureText(text, ref bounds);
			
			width = bounds.Width;
			height = -bounds.Top + bounds.Bottom;
			baselineY = -bounds.Top;
			
			// 获取字形 ID（使用预分配数组减少分配）
			ushort[] glyphs = new ushort[text.Length];
			font.GetGlyphs(text.AsSpan(), glyphs);
			
			// 获取字形位置
			SKPoint[] positions = new SKPoint[text.Length];
			font.GetGlyphPositions(glyphs, positions);
			
			// 创建带位置的 SKTextBlob（比 AllocateRun 更快）
			using var builder = new SKTextBlobBuilder();
			var run = builder.AllocatePositionedRun(font, glyphs.Length);
			glyphs.AsSpan().CopyTo(run.GetGlyphSpan());
			positions.AsSpan().CopyTo(run.GetPositionSpan());
			blob = builder.Build();
			
			_textBlobCache[hash] = (blob, width, height, baselineY, accessTime);
		}
		
		// 计算绘制位置
		float drawX = x;
		float drawY = y + baselineY;
		
		if (hAlign == HorizontalAlign.Center)
			drawX -= width / 2f;
		else if (hAlign == HorizontalAlign.Right)
			drawX -= width;
		
		if (vAlign == VerticalAlign.Center)
			drawY -= height / 2f;
		else if (vAlign == VerticalAlign.Bottom)
			drawY -= height;
		
		// 设置颜色并绘制
		_textPaint.Color = color;
		canvas.DrawText(blob, drawX, drawY, _textPaint);
	}
	
	// 绘制带透明度的动画文本（使用位图缓存优化大字号文本）
	// 对于频繁变化透明度的大字号文本，预渲染为位图
	public static void DrawAnimatedText(this SKCanvas canvas, SKTypeface typeface, float fontSize,
		string text, float x, float y, SKColor color, byte alpha,
		HorizontalAlign hAlign = HorizontalAlign.Left,
		VerticalAlign vAlign = VerticalAlign.Top)
	{
		if (string.IsNullOrEmpty(text)) return;
		
		// 对于小字号或完全不透明的文本，直接使用 SKTextBlob
		if (fontSize <= 24 || alpha == 255)
		{
			canvas.DrawTextAndCache(typeface, fontSize, text, x, y, color.WithAlpha(alpha), hAlign, vAlign);
			return;
		}
		
		// 使用位图缓存（透明度量化为 32 级减少缓存条目）
		byte quantizedAlpha = (byte)((alpha / 8) * 8);  // 量化透明度
		int hash = HashCode.Combine(text, typeface?.FamilyName ?? "", fontSize, quantizedAlpha);
		long accessTime = Interlocked.Increment(ref _cacheAccessCounter);
		
		SKBitmap bitmap;
		if (_animTextBitmapCache.TryGetValue(hash, out var cached))
		{
			bitmap = cached.bitmap;
			_animTextBitmapCache[hash] = (bitmap, accessTime);
		}
		else
		{
			// LRU 清理
			if (_animTextBitmapCache.Count >= MaxAnimTextCacheSize)
			{
				CleanupAnimTextCache();
			}
			
			// 预渲染文本到位图
			bitmap = RenderTextToBitmap(typeface, fontSize, text, color.WithAlpha(quantizedAlpha));
			_animTextBitmapCache[hash] = (bitmap, accessTime);
		}
		
		// 计算绘制位置
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
		
		// 绘制缓存的位图
		canvas.DrawBitmap(bitmap, drawX, drawY);
	}
	
	// 将文本渲染到位图
	private static SKBitmap RenderTextToBitmap(SKTypeface typeface, float fontSize, string text, SKColor color)
	{
		_textPaint.Typeface = typeface;
		_textPaint.TextSize = fontSize;
		_textPaint.Color = color;
		
		// 测量文本边界
		var bounds = new SKRect();
		_textPaint.MeasureText(text, ref bounds);
		
		int width = Math.Max(1, (int)Math.Ceiling(bounds.Width) + 4);
		int height = Math.Max(1, (int)Math.Ceiling(-bounds.Top + bounds.Bottom) + 4);
		
		var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
		using var bitmapCanvas = new SKCanvas(bitmap);
		bitmapCanvas.Clear(SKColors.Transparent);
		
		// 绘制文本到位图（偏移以保留边界）
		bitmapCanvas.DrawText(text, -bounds.Left + 2, -bounds.Top + 2, _textPaint);
		
		return bitmap;
	}
	
	// 清理动画文本位图缓存（LRU 策略）
	private static void CleanupAnimTextCache()
	{
		var toRemove = _animTextBitmapCache
			.OrderBy(kv => kv.Value.lastUsed)
			.Take(AnimTextCleanupBatch)
			.Select(kv => kv.Key)
			.ToList();
		
		foreach (var key in toRemove)
		{
			if (_animTextBitmapCache.TryGetValue(key, out var entry))
			{
				entry.bitmap?.Dispose();
				_animTextBitmapCache.Remove(key);
			}
		}
	}
	
	// 清理文本缓存（在需要时调用，如字体变化）
	public static void ClearTextRenderCache()
	{
		// 清理 SKTextBlob 缓存
		foreach (var entry in _textBlobCache.Values)
			entry.blob?.Dispose();
		_textBlobCache.Clear();
		
		// 清理动画文本位图缓存
		foreach (var entry in _animTextBitmapCache.Values)
			entry.bitmap?.Dispose();
		_animTextBitmapCache.Clear();
		
		// 清理旧版位图缓存（兼容）
		foreach (var entry in _textRenderCache.Values)
			entry.bitmap?.Dispose();
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
