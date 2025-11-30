using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using CSCore;
using CSCore.Codecs;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;
using Unai.ExtendedBinaryWaterfall.Exporters;
using Unai.ExtendedBinaryWaterfall.Parsers;
using TagLib;

namespace Unai.ExtendedBinaryWaterfall;

public class Generator
{
    #region Main Fields

    public FileStream InputFileStream { get; set; }
    public Stream InputAuxiliaryFileStream { get; set; }
    public IParser Parser { get; set; }
    public IExporter Exporter { get; set; }
    private readonly Stopwatch _timer = new();
    private List<SubFile> _subfiles = [];

    public Dictionary<string, string> AdditionalCliArguments { get; } = [];

    #endregion

    #region Events

    public event Action OnFinish;
    public event Action<float> OnProgress;

    #endregion

    #region Generator Registers

    private Image<Rgba32> _frameContent = null;
    private Image<Rgba32> _viewportFramebuf = null;
    private AudioBuffer _inputAudioBuffer = null;
    private AudioBuffer _outputAudioBuffer = null;
    // CSCore 音频解码相关字段：使用 CodecFactory 自动根据扩展名选择解码器
    private IWaveSource _audioWaveSource = null;
    private ISampleSource _audioSampleSource = null;
    // 用于暂存每帧交织的 float PCM 数据
    private float[] _audioSampleBuffer = null;
    // 频谱上一帧的柱状值，用于做简单的指数平滑，降低频谱动态速度
    private float[] _lastSpectrumBins = null;
    // 频谱峰值保持数组：记录每个柱的历史峰值，缓慢衰减
    private float[] _spectrumPeakHold = null;
    private int _videoFrameX1, _videoFrameX2, _videoFrameY1, _videoFrameY2;
    internal bool _exitRequested = false;
    
    // 歌曲切换过渡动画相关字段
    private int _lastSubfileIndex = -1;
    private long _transitionStartOffset = -1;
    private long _transitionEndOffset = -1;
    
    // 性能优化：预计算的布局参数缓存
    private struct LayoutCache
    {
        public float Scale;
        public int RightPanelX1, RightPanelX2;
        public int SubfileX1, SubfileX2;
        public int AudioVisX1, AudioVisX2;
        public float SubfileH, SubfileRowH;
        public float ShadowY1, ShadowY2;
        public float RightPanelTop, RightPanelBottom;
        public float ListTop, ListBottom;
        public float BottomPanelHeight, BottomY;
        public float CoverSize, CoverX, CoverY;
        public float InfoX, TimeX;
        public bool IsValid;
    }
    private LayoutCache _layoutCache;
    
    // 性能优化：预渲染的静态 UI 元素
    private Image<Rgba32> _staticLabelsCache = null;
    
    // Ease-out 缓动函数（快到慢）
    private static float EaseOutCubic(float t) => 1f - MathF.Pow(1f - t, 3f);
    
    // 测量文本宽度
    private float MeasureTextWidth(string text, Font font)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var bounds = TextMeasurer.MeasureBounds(text, new TextOptions(font));
        return bounds.Width;
    }

    // 公共方法：请求停止生成
    public void RequestStop() => _exitRequested = true;

    public byte[] GetCurrentFrameAsBgra()
    {
        if (_frameContent == null) return null;
        
        try
        {
            var width = _frameContent.Width;
            var height = _frameContent.Height;
            var data = new byte[width * height * 4];
            
            _frameContent.ProcessPixelRows(pa =>
            {
                for (int y = 0; y < pa.Height; y++)
                {
                    var row = pa.GetRowSpan(y);
                    for (int x = 0; x < pa.Width; x++)
                    {
                        var pixel = row[x];
                        int idx = (y * width + x) * 4;
                        // BGRA 格式（WPF 使用）
                        data[idx + 0] = pixel.B;
                        data[idx + 1] = pixel.G;
                        data[idx + 2] = pixel.R;
                        data[idx + 3] = pixel.A;
                    }
                }
            });
            
            return data;
        }
        catch
        {
            return null;
        }
    }

    public int CurrentFrameWidth => _frameContent?.Width ?? 0;
    public int CurrentFrameHeight => _frameContent?.Height ?? 0;

    // 预览模式初始化（不需要导出器，？？？）
    public void InitializeForPreview()
    {
        if (InputFileStream == null && !string.IsNullOrEmpty(InputFilePath))
        {
            InputFileStream = System.IO.File.OpenRead(InputFilePath);
        }
        
        InitializeParser();
        ParseSubfiles();
        PrecomputeSubfileWaveforms();
        InitializeFonts();
        UpdateValues();
        
        // 初始化音频缓冲
        _inputAudioBuffer = new(AudioInputSamplesPerFramePerChannel, AudioInputChannelCount);
        _outputAudioBuffer = new(AudioOutputSamplesPerFramePerChannel, AudioOutputChannelCount);
    }

    // 渲染指定位置的预览帧（progress: 0-1）
    public void RenderPreviewFrame(float progress)
    {
        if (InputFileStream == null) return;
        
        progress = Math.Clamp(progress, 0f, 1f);
        long currentOffset = (long)(InputFileStream.Length * progress);
        
        // 对齐到帧边界
        currentOffset = currentOffset - (currentOffset % (WaterfallWidth * 4));
        if (currentOffset < 0) currentOffset = 0;
        if (currentOffset >= InputFileStream.Length) currentOffset = InputFileStream.Length - WaterfallFrameLength;
        
        // 计算帧起始偏移
        long frameStartByteOffset = currentOffset - (WaterfallFrameLength / 2);
        int playHeadRelPos = 0;
        
        if (frameStartByteOffset < 0)
        {
            playHeadRelPos = (int)-(frameStartByteOffset / (WaterfallWidth * 4));
            frameStartByteOffset = 0;
        }
        else if (frameStartByteOffset + WaterfallFrameLength >= InputFileStream.Length)
        {
            playHeadRelPos = (int)((InputFileStream.Length - (frameStartByteOffset + WaterfallFrameLength)) / (WaterfallWidth * 4));
            frameStartByteOffset = InputFileStream.Length - WaterfallFrameLength;
        }
        
        // 读取视频字节
        InputFileStream.Position = frameStartByteOffset;
        byte[] currentVideoBuffer = new byte[WaterfallFrameLength];
        InputFileStream.Read(currentVideoBuffer, 0, WaterfallFrameLength);
        
        // 创建瀑布视图
        _viewportFramebuf?.Dispose();
        _viewportFramebuf = Image.LoadPixelData<Rgba32>(currentVideoBuffer, WaterfallWidth, WaterfallHeight);
        _viewportFramebuf.ProcessPixelRows(pa =>
        {
            for (int y = 0; y < pa.Height; y++)
            {
                var row = pa.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    row[x].A = 255;
                }
            }
        });
        _viewportFramebuf.Mutate(vctx => vctx
            .Flip(FlipMode.Vertical)
            .Resize(WaterfallScaledWidth, WaterfallScaledHeight, new NearestNeighborResampler()));
        
        // 查找当前偏移对应的子文件
        KeyValuePair<int, SubFile>? currentSubfile = null;
        float subfileWindowIndex = 0f;
        for (int sfi = 0; sfi < _subfiles.Count; sfi++)
        {
            var sf = _subfiles[sfi];
            if (currentOffset >= sf.StartOffset && currentOffset < sf.StartOffset + sf.Length)
            {
                currentSubfile = new(sfi, sf);
                subfileWindowIndex = sfi;
                break;
            }
        }

        // 绘制预览帧（完整布局）
        _frameContent.Mutate(ctx =>
        {
            ctx.Clear(new Rgba32(16, 16, 16, 255));
            
            float s = ResolutionScale;
            
            // 布局参数
            int rightPanelX1 = _videoFrameX2 + (int)(64 * s);
            int rightPanelX2 = OutputVideoWidth - (int)(32 * s);
            int subfileX1 = rightPanelX1;
            int subfileX2 = rightPanelX2;
            int audioVisX1 = rightPanelX1;
            int audioVisX2 = rightPanelX2;
            float subfileH = 48f * s;
            float shadowY1 = (OutputVideoHeight / 2f) - subfileH * 8.5f;
            float shadowY2 = (OutputVideoHeight / 2f) + subfileH * 6.5f;
            
            // 右侧面板垂直范围
            float rightPanelTop = shadowY1 + subfileH * 2f + 16f * s;
            float rightPanelBottom = shadowY2 - 16f * s;
            float rightPanelHeight = rightPanelBottom - rightPanelTop;
            float listHeight = rightPanelHeight * 0.20f;
            float listTop = rightPanelTop;
            float listBottom = listTop + listHeight;
            
            // 绘制瀑布视图
            ctx.DrawImage(_viewportFramebuf, new Point(_videoFrameX1, _videoFrameY1), 1f);
            
            // 播放指示器
            ctx.DrawText(new RichTextOptions(_font32)
            {
                Origin = new Vector2(32, (OutputVideoHeight / 2f) + (playHeadRelPos * (WaterfallScaledHeight / (float)WaterfallHeight))),
                VerticalAlignment = VerticalAlignment.Center,
            }, "▶", Color.White);
            
            // 绘制右上歌曲/子文件列表
            int firstSubfileIndex = Math.Max(0, (int)(subfileWindowIndex - 2));
            int lastSubfileIndex = Math.Min(_subfiles.Count - 1, (int)(subfileWindowIndex + 7));
            float subfileRowH = 36f * s; // 行高
            float subfileY = listTop + subfileRowH / 2f - (subfileWindowIndex - firstSubfileIndex) * subfileRowH;
            
            for (int sfi = firstSubfileIndex; sfi <= lastSubfileIndex; sfi++)
            {
                if (sfi < 0 || sfi >= _subfiles.Count)
                {
                    subfileY += subfileRowH;
                    continue;
                }

                var subfile = _subfiles[sfi];
                bool isMainSubfile = sfi == (currentSubfile?.Key ?? -1);

                // （_font24 代替 _font32）
                ctx.DrawText(new RichTextOptions(_font24)
                {
                    Origin = new Vector2(subfileX1, subfileY),
                    VerticalAlignment = VerticalAlignment.Center,
                }, isMainSubfile ? "▶" : " ", Color.White)
                .DrawText(new RichTextOptions(_font24)
                {
                    Origin = new Vector2(subfileX1 + 24 * s, subfileY),
                    VerticalAlignment = VerticalAlignment.Center,
                }, $"{Utils.GetFileTypeEmoji(subfile)} {Utils.TruncateString(BuildSubfileDisplayLine(subfile), 40)}", Color.White)
                .DrawText(new RichTextOptions(_font24)
                {
                    Origin = new Vector2(subfileX2, subfileY),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                }, Utils.ToByteSizeString(subfile.Length), Color.DimGray);

                if (isMainSubfile)
                {
                    float percentOfSubfile = (currentOffset - subfile.StartOffset) / (float)subfile.Length;
                    float progressY = subfileY + 22 * s; // 间距
                    ctx.DrawText(new RichTextOptions(_font16)
                    {
                        Origin = new PointF(subfileX1 + 40 * s, progressY),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    }, $"{(int)Math.Clamp(percentOfSubfile * 100, 0, 100)} %", Color.White)
                    .DrawProgressBar(percentOfSubfile, (int)(subfileX1 + 60 * s), subfileX2, progressY);
                }

                subfileY += subfileRowH;
            }
            
            // 音频可视化占位符区域
            float audioVisTop = listBottom + 16f;
            float audioVisBottom = rightPanelBottom;
            if (audioVisBottom > audioVisTop + 16f)
            {
                var audioVisRegion = new RectangleF(audioVisX1, audioVisTop, audioVisX2 - audioVisX1, audioVisBottom - audioVisTop);
                
                // 绘制占位符边框和文字
                ctx.Draw(Color.FromRgba(80, 80, 80, 128), 1f, audioVisRegion);
                ctx.DrawText(new RichTextOptions(_font24)
                {
                    Origin = new Vector2(audioVisRegion.X + audioVisRegion.Width / 2, audioVisRegion.Y + audioVisRegion.Height / 2),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }, "🎵 音频可视化\n（导出时显示波形和频谱）", Color.FromRgba(128, 128, 128, 200));
            }
            
            // 顶部/底部渐变遮罩
            ctx.Fill(
                new LinearGradientBrush(
                    new PointF(0, shadowY1),
                    new PointF(0, shadowY1 + subfileH * 2f),
                    GradientRepetitionMode.None,
                    new(0.5f, Color.FromRgba(16, 16, 16, 255)),
                    new(1, Color.FromRgba(16, 16, 16, 0))
                ),
                new RectangleF(0, shadowY1, OutputVideoWidth, subfileH * 2f)
            )
            .Fill(
                new LinearGradientBrush(
                    new PointF(0, shadowY2),
                    new PointF(0, shadowY2 + subfileH * 2f),
                    GradientRepetitionMode.None,
                    new(0, Color.FromRgba(16, 16, 16, 0)),
                    new(0.5f, Color.FromRgba(16, 16, 16, 255))
                ),
                new RectangleF(0, shadowY2, OutputVideoWidth, subfileH * 2f)
            );
            
            // 专辑/目录抬头文字
            string albumHeaderText = "";
            if (currentSubfile?.Value != null)
            {
                var sfValue = currentSubfile.Value.Value;
                if (!string.IsNullOrWhiteSpace(sfValue.AlbumTitle))
                {
                    albumHeaderText = sfValue.AlbumTitle;
                    if (!string.IsNullOrWhiteSpace(sfValue.AlbumArtistName))
                    {
                        albumHeaderText += " // " + sfValue.AlbumArtistName;
                    }
                }
                else
                {
                    albumHeaderText = sfValue.FileDirectory ?? "";
                }
            }
            
            ctx.DrawText(new RichTextOptions(_font24)
            {
                Origin = new Vector2(subfileX1 + 40, rightPanelTop - 24f),
                VerticalAlignment = VerticalAlignment.Center,
            }, Utils.TruncateString(albumHeaderText, 72), Color.DimGray);
            
            // A/V 设置信息
            string avSettingsString = $"{OutputVideoWidth}×{OutputVideoHeight} @ {OutputFps} fps\n" +
                $"{AudioOutputSampleRate / 1000}kHz {AudioOutputChannelCount}ch\n" +
                $"RGBA (32bpp), {WaterfallWidth} px/line";
            string readSpeedString = $"{InputBytesPerSecond / 1024} KiB/s";
            
            ctx.DrawText(new RichTextOptions(_font24)
            {
                Origin = new Vector2(32, 32),
            }, "A/V SETTINGS", Color.DimGray)
            .DrawText(new RichTextOptions(_font32)
            {
                Origin = new Vector2(32, 32 + 24),
            }, avSettingsString, Color.White)
            .DrawText(new RichTextOptions(_font24)
            {
                Origin = new Vector2(OutputVideoWidth - 32, 32),
                HorizontalAlignment = HorizontalAlignment.Right,
            }, "ABS. OFFSET", Color.DimGray)
            .DrawText(new RichTextOptions(_font32)
            {
                Origin = new Vector2(OutputVideoWidth - 32, 32 + 24),
                HorizontalAlignment = HorizontalAlignment.Right,
                TextAlignment = TextAlignment.End,
            }, $"{currentOffset / 1048576f:N2} MiB\n0x{currentOffset:X8}", Color.White)
            .DrawText(new RichTextOptions(_font24)
            {
                Origin = new Vector2(OutputVideoWidth - 256, 32),
                HorizontalAlignment = HorizontalAlignment.Right,
            }, "BITRATE", Color.DimGray)
            .DrawText(new RichTextOptions(_font32)
            {
                Origin = new Vector2(OutputVideoWidth - 256, 32 + 24),
                HorizontalAlignment = HorizontalAlignment.Right,
            }, readSpeedString, Color.White);
            
            // 作者（顶部中央）
            if (!string.IsNullOrEmpty(Author))
            {
                ctx.DrawText(new RichTextOptions(_font32)
                {
                    Origin = new Vector2(OutputVideoWidth / 2f, 32 + 24),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                }, Author, Color.White);
            }
            
            // 底部播放器区域
            float bottomPanelHeight = 100f * s;
            float bottomY = OutputVideoHeight - bottomPanelHeight - 16f * s;
            float coverSize = 72f * s;
            float coverX = 32f * s;
            float coverY = bottomY + (bottomPanelHeight - coverSize) / 2f;
            float infoX = coverX + coverSize + 16f * s;
            float timeX = OutputVideoWidth - 32f * s;
            
            // 获取当前歌曲信息
            string trackName = "";
            string artistName = "";
            string genreText = "";
            TimeSpan currentTime = TimeSpan.Zero;
            TimeSpan totalTime = TimeSpan.Zero;
            float trackProgress = 0f;
            Image coverImage = null;
            
            if (currentSubfile?.Value != null)
            {
                var sf = currentSubfile.Value.Value;
                trackName = !string.IsNullOrWhiteSpace(sf.TrackTitle) ? sf.TrackTitle : sf.FileName;
                artistName = sf.ArtistName ?? sf.AlbumArtistName ?? "";
                genreText = sf.Genre ?? "";
                coverImage = sf.Icon;
                
                if (InputBytesPerSecond > 0)
                {
                    totalTime = TimeSpan.FromSeconds(sf.Length / (double)InputBytesPerSecond);
                    long offsetInTrack = currentOffset - sf.StartOffset;
                    currentTime = TimeSpan.FromSeconds(Math.Max(0, offsetInTrack) / (double)InputBytesPerSecond);
                    trackProgress = Math.Clamp((float)offsetInTrack / sf.Length, 0f, 1f);
                }
            }
            
            // 检测歌曲切换并计算过渡动画
            int currSubIdx = currentSubfile?.Key ?? -1;
            float transitionT = 1f;
            bool isInTransition = false;
            long transitionBytes = InputBytesPerSecond;
            
            if (currSubIdx != _lastSubfileIndex && currSubIdx >= 0)
            {
                var currSf = currentSubfile.Value.Value;
                _transitionStartOffset = currSf.StartOffset - transitionBytes;
                _transitionEndOffset = currSf.StartOffset + transitionBytes;
                _lastSubfileIndex = currSubIdx;
            }
            
            if (_transitionStartOffset >= 0 && currentOffset >= _transitionStartOffset && currentOffset <= _transitionEndOffset)
            {
                isInTransition = true;
                float rawT = (currentOffset - _transitionStartOffset) / (float)(_transitionEndOffset - _transitionStartOffset);
                transitionT = EaseOutCubic(Math.Clamp(rawT, 0f, 1f));
            }
            
            byte contentAlpha = (byte)(255 * transitionT);
            Color labelColor = Color.FromRgba(105, 105, 105, contentAlpha);
            Color textColor = Color.FromRgba(255, 255, 255, contentAlpha);
            
            // 绘制封面
            var coverRect = new RectangleF(coverX, coverY, coverSize, coverSize);
            ctx.Fill(Color.FromRgba(32, 32, 32, 255), coverRect);
            ctx.Draw(Color.FromRgba(200, 200, 200, 255), 2f, coverRect);
            
            if (coverImage != null)
            {
                using var scaledCover = coverImage.Clone(imgCtx => imgCtx.Resize((int)coverSize - 4, (int)coverSize - 4));
                ctx.DrawImage(scaledCover, new Point((int)(coverX + 2), (int)(coverY + 2)), transitionT);
            }
            else
            {
                ctx.DrawText(new RichTextOptions(_font48)
                {
                    Origin = new Vector2(coverX + coverSize / 2, coverY + coverSize / 2),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }, "♪", Color.FromRgba(100, 100, 100, contentAlpha));
            }
            
            // 标签行位置
            float labelY = bottomY + 4f * s;
            float valueY = labelY + 18f * s;
            float waveformY = valueY + 32f * s;
            
            // 测量文本宽度以动态定位标签
            float trackNameWidth = MeasureTextWidth(trackName, _font32);
            float separatorWidth = MeasureTextWidth(" // ", _font32);
            float artistNameWidth = MeasureTextWidth(artistName, _font32);
            
            bool hasArtist = !string.IsNullOrWhiteSpace(artistName);
            bool hasGenre = !string.IsNullOrWhiteSpace(genreText);
            
            // 计算标签位置
            float titleLabelX = infoX;
            float composerLabelX = infoX + trackNameWidth + separatorWidth;
            float genreLabelX = infoX + trackNameWidth + (hasArtist ? separatorWidth + artistNameWidth : 0);
            
            // 过渡动画：标签偏移
            float labelOffsetX = isInTransition ? (1f - transitionT) * 50f * s : 0f;
            
            // 绘制 Title: 标签
            ctx.DrawText(new RichTextOptions(_font16)
            {
                Origin = new Vector2(titleLabelX + labelOffsetX, labelY),
            }, "Title:", labelColor);
            
            // 绘制 Composer: 标签（仅当有艺术家时）
            if (hasArtist)
            {
                ctx.DrawText(new RichTextOptions(_font16)
                {
                    Origin = new Vector2(composerLabelX + labelOffsetX, labelY),
                }, "Composer:", labelColor);
            }
            
            // 绘制 Genre: 标签（仅当有风格时）
            if (hasGenre)
            {
                ctx.DrawText(new RichTextOptions(_font16)
                {
                    Origin = new Vector2(genreLabelX + labelOffsetX, labelY),
                }, "Genre:", labelColor);
            }
            
            // 绘制 Time: 标签
            ctx.DrawText(new RichTextOptions(_font16)
            {
                Origin = new Vector2(timeX, labelY),
                HorizontalAlignment = HorizontalAlignment.Right,
            }, "Time:", labelColor);
            
            // 构建歌曲信息
            string displayInfo = trackName;
            if (hasArtist) displayInfo += $" // {artistName}";
            if (hasGenre) displayInfo += $" [{genreText}]";
            
            ctx.DrawText(new RichTextOptions(_font32)
            {
                Origin = new Vector2(infoX, valueY),
                WrappingLength = timeX - infoX - 120f * s,
            }, Utils.TruncateString(displayInfo, 55), textColor);
            
            // 时间显示
            string timeString = $"{(int)currentTime.TotalMinutes}:{currentTime.Seconds:D2} / {(int)totalTime.TotalMinutes}:{totalTime.Seconds:D2}";
            ctx.DrawText(new RichTextOptions(_font32)
            {
                Origin = new Vector2(timeX, valueY),
                HorizontalAlignment = HorizontalAlignment.Right,
            }, timeString, textColor);
            
            // 波形进度条
            float waveformX1 = infoX;
            float waveformX2 = timeX;
            float waveformHeight = 20f * s;
            var waveformRect = new RectangleF(waveformX1, waveformY, waveformX2 - waveformX1, waveformHeight);
            ctx.Fill(Color.FromRgba(40, 40, 40, contentAlpha), waveformRect);
            
            float barWidth = 2f;
            float barSpacing = 1f;
            int totalBars = (int)(waveformRect.Width / (barWidth + barSpacing));
            float progressX = waveformRect.X + waveformRect.Width * trackProgress;
            float gapWidth = 4f * s;
            
            for (int i = 0; i < totalBars; i++)
            {
                float barX = waveformRect.X + i * (barWidth + barSpacing);
                float heightRatio = 0.3f + 0.6f * (float)Math.Abs(Math.Sin(i * 0.4 + currSubIdx * 0.1));
                float barHeight = waveformHeight * heightRatio * 0.85f;
                float barY = waveformRect.Y + (waveformHeight - barHeight) / 2f;
                
                if (barX > progressX - gapWidth && barX < progressX + gapWidth)
                    continue;
                
                Color barColor = barX < progressX 
                    ? Color.FromRgba(100, 100, 100, contentAlpha)
                    : Color.FromRgba(220, 220, 220, contentAlpha);
                
                ctx.Fill(barColor, new RectangleF(barX, barY, barWidth, barHeight));
            }
            
            // 绘制播放头
            float headlineWidth = 2f;
            float borderWidth = 2f;
            
            ctx.Fill(Color.FromRgba(30, 30, 30, contentAlpha), new RectangleF(
                progressX - headlineWidth / 2 - borderWidth, waveformRect.Y - 2f, borderWidth, waveformHeight + 4f));
            ctx.Fill(Color.FromRgba(255, 255, 255, contentAlpha), new RectangleF(
                progressX - headlineWidth / 2, waveformRect.Y - 2f, headlineWidth, waveformHeight + 4f));
            ctx.Fill(Color.FromRgba(30, 30, 30, contentAlpha), new RectangleF(
                progressX + headlineWidth / 2, waveformRect.Y - 2f, borderWidth, waveformHeight + 4f));
        });
    }

    #endregion

    #region ImageSharp-specific

    private FontCollection _fontCollection;
    private FontFamily _fontFamily, _emojiFontFamily;
    private Font _font16, _font24, _font32, _font48;
    private readonly DrawingOptions _drawOpts = new()
    {
        GraphicsOptions = new()
        {
            Antialias = true,
            AntialiasSubpixelDepth = 0, // coalesced value
        }
    };

    #endregion

    #region General Parameters

    public string InputFilePath { get; set; } = null;
    public string InputAuxiliaryFilePath { get; set; } = null;
    public string OutputFilePath { get; set; } = null;
    public string Title { get; set; } = null;
    public string Author { get; set; } = null;
    public string InputFileFormatId { get; set; } = null;
    public string ExporterId { get; set; } = null;
    // 硬件加速类型（仅适用于 FFmpeg 导出器）
    public HardwareAccelType HardwareAccel { get; set; } = HardwareAccelType.Auto;
    // NVENC 编码配置（默认为最快速度）
    public string NvencPreset { get; set; } = "p1";
    public string NvencTune { get; set; } = "hq";
    public string NvencRateControl { get; set; } = "vbr";
    public bool NvencTemporalAQ { get; set; } = false;
    public bool NvencSpatialAQ { get; set; } = false;
    public int NvencLookahead { get; set; } = 0;
    public int InputBytesPerSecond { get; set; } = 48000 * 2;
    public string FontName { get; set; } = null;
    [CliParameter("Font Antialiasing", "font-antialiasing")]
    public bool FontAntialiasing { get => _drawOpts.GraphicsOptions.Antialias; set => _drawOpts.GraphicsOptions.Antialias = value; }

    #endregion

    public int InputBytesPerFrame => InputBytesPerSecond / OutputFps;

    #region Video Parameters

    [CliParameter("Output Video Width", "output-width")]
    public int OutputVideoWidth { get; set; } = 1920;
    [CliParameter("Output Video Height", "output-height")]
    public int OutputVideoHeight { get; set; } = 1080;
    // 分辨率缩放因子（基于 1080p 标准）
    public float ResolutionScale => OutputVideoHeight / 1080f;
    [CliParameter("Output Framerate", "output-fps")]
    public int OutputFps { get; set; } = 60;
    public int WaterfallScaledWidth { get; set; } = 768;
    public int WaterfallScaledHeight { get; set; } = 768;
    [CliParameter("Input Video Width", "input-width")]
    public int WaterfallWidth { get; set; } = 256;
    [CliParameter("Input Video Height", "input-height")]
    public int WaterfallHeight { get; set; } = 256;
    public int WaterfallFrameLength => WaterfallWidth * WaterfallHeight * 4;

    #endregion

    #region Audio Parameters

    [CliParameter("Input Sample Format", "sample-format")]
    public AudioSampleFormat AudioInputSampleFormat { get; set; } = AudioSampleFormat.Unsigned8;
    [CliParameter("Input Audio Channel Count", "channel-count")]
    public int AudioInputChannelCount { get; set; } = 2;
    public int AudioInputSamplesPerFrame => InputBytesPerFrame / AudioInputSampleFormat.GetByteSize();
    public int AudioInputSamplesPerFramePerChannel => AudioInputSamplesPerFrame / AudioInputChannelCount;
    public int AudioInputSampleRate => (InputBytesPerSecond / AudioInputSampleFormat.GetByteSize()) / AudioInputChannelCount;
    public int AudioInputBytesPerFrame => InputBytesPerFrame;

    [CliParameter("Output Sample Format", "output-sample-format")]
    public AudioSampleFormat AudioOutputSampleFormat { get; set; } = AudioSampleFormat.Float32;
    [CliParameter("Output Audio Channel Count", "output-channel-count")]
    public int AudioOutputChannelCount { get; set; } = 2;
    [CliParameter("Output Sample Rate", "output-sample-rate")]
    public int AudioOutputSampleRate { get; set; } = 48000;
    public int AudioOutputSamplesPerFramePerChannel => AudioOutputSampleRate / OutputFps;
    public int AudioOutputSamplesPerFrame => AudioOutputSamplesPerFramePerChannel * AudioOutputChannelCount;
    public int AudioOutputBytesPerFrame => AudioOutputSampleFormat.GetByteSize() * AudioOutputSamplesPerFrame;

    #endregion

    #region Visualizer Parameters

    [CliParameter("Waveform line width (pixels)", "waveform-line-width")]
    public float WaveformLineWidth { get; set; } = 1f; // 波形线条粗细（像素），默认 1

    [CliParameter("Waveform window length in milliseconds", "waveform-length-ms")]
    public float WaveformLengthMs { get; set; } = 50f; // 波形显示窗口长度（毫秒），控制时间轴缩放

    [CliParameter("Waveform display mode (average, diffavg, left, right, stereo)", "waveform-mode")]
    public string WaveformMode { get; set; } = "average"; // 波形显示模式：average/diffavg/left/right/stereo

    [CliParameter("Spectrum bar count", "spectrum-bars")]
    public int SpectrumBarCount { get; set; } = 64; // 频谱柱数量，范围 8~1024

    [CliParameter("Spectrum smoothing factor", "spectrum-smoothing")]
    public float SpectrumSmoothing { get; set; } = 0.6f; // 频谱平滑系数，范围 0.1~1

    [CliParameter("FFT size for spectrum analysis (power of 2)", "fft-size")]
    public int FftSize { get; set; } = 4096; // FFT 大小，必须是 2 的幂次方，范围 512~8192

    [CliParameter("Intro fade duration in seconds", "intro-fade-duration")]
    public float IntroFadeDuration { get; set; } = 1.0f; // 开头免责声明的淡入淡出时长（秒）

    [CliParameter("Outro fade duration in seconds", "outro-fade-duration")]
    public float OutroFadeDuration { get; set; } = 2.0f; // 内容结束后的淡出时长（秒）

    #endregion

    #region Debug Flags

    public bool LogAllSubfiles { get; set; } = false;

    #endregion

    #region Initialization Methods

    public void Initialize()
    {
        if (InputFileStream == null)
        {
            Logger.Info("Opening files…");
            Logger.Debug($"Opening file '{InputFilePath}'…");
            InputFileStream = System.IO.File.OpenRead(InputFilePath);
        }

        if (InputAuxiliaryFileStream == null)
        {
            if (InputAuxiliaryFilePath != null)
            {
                Logger.Debug($"Opening file '{InputAuxiliaryFilePath}'…");
                InputAuxiliaryFileStream = System.IO.File.OpenRead(InputAuxiliaryFilePath);
                Logger.Debug($"  Done ({InputAuxiliaryFileStream.Length / 1024} KiB).");
            }
        }

        InitializeParser();

        ParseSubfiles();
        PrecomputeSubfileWaveforms();

        InitializeExporter();

        InitializeFonts();

        Logger.Info("Preparing audio/video generation…");

        UpdateValues();

        // 使用 CSCore 初始化真实音频解码器，并让输出采样率/声道数跟随解码器参数
        InitializeAudioDecoder();

        if (_drawOpts.GraphicsOptions.Antialias)
        {
            if (_drawOpts.GraphicsOptions.AntialiasSubpixelDepth < 0)
            {
                _drawOpts.GraphicsOptions.AntialiasSubpixelDepth = 1;
            }
        }

        LogGeneratorStatus();
    }

    // 使用 CSCore 初始化音频解码器：根据输入文件自动选择合适的解码器，
    // 并让 AudioOutputSampleRate / AudioOutputChannelCount 直接跟随解码器参数。
    // 如果初始化失败，则回退到原先基于字节流的伪音频配置。
    private void InitializeAudioDecoder()
    {
        try
        {
            Logger.Info("Initializing audio decoder (CSCore)…");

            // 使用 CodecFactory 根据文件扩展名自动选择解码器
            _audioWaveSource = CodecFactory.Instance.GetCodec(InputFilePath);
            var wf = _audioWaveSource.WaveFormat;

            // 直接跟随解码器的采样率和声道数
            AudioOutputSampleRate = wf.SampleRate;
            AudioOutputChannelCount = wf.Channels;

            // 如果可以估算音频总时长，则重算 InputBytesPerSecond，使“字节时间轴”和音频时间轴大致对齐
            long decodedBytes = _audioWaveSource.Length;
            int bytesPerSecond = wf.BytesPerSecond;
            if (decodedBytes > 0 && bytesPerSecond > 0 && InputFileStream != null && InputFileStream.Length > 0)
            {
                double durationSeconds = decodedBytes / (double)bytesPerSecond;
                if (durationSeconds > 0.1)
                {
                    InputBytesPerSecond = (int)(InputFileStream.Length / durationSeconds);
                }
            }

            // 转为浮点样本源，便于后续直接读取 float PCM
            _audioSampleSource = _audioWaveSource.ToSampleSource();

            // 准备输出音频缓冲区（每帧 SamplesPerFramePerChannel × 通道数）
            _outputAudioBuffer = new(AudioOutputSamplesPerFramePerChannel, AudioOutputChannelCount);
            _audioSampleBuffer = new float[AudioOutputSamplesPerFrame];

            // 输入缓冲区在新的流程中不再使用真实含义，这里仅保持大小一致以避免空引用
            _inputAudioBuffer = new(AudioOutputSamplesPerFramePerChannel, AudioOutputChannelCount);
        }
        catch (Exception ex)
        {
            // 出错时记录日志并回退到旧的“伪音频”逻辑，以保证程序仍可运行
            Logger.Error($"Failed to initialize audio decoder with CSCore. Falling back to fake audio. {ex.Message}");
            _audioSampleSource = null;
            _audioWaveSource?.Dispose();
            _audioWaveSource = null;

            _inputAudioBuffer = new(AudioInputSamplesPerFramePerChannel, AudioInputChannelCount);
            _outputAudioBuffer = new(AudioOutputSamplesPerFramePerChannel, AudioOutputChannelCount);
        }
    }

    [Conditional("DEBUG")]
    private void LogGeneratorStatus()
    {
        Logger.Debug($"Selected parser: {Parser?.GetType().GetCustomAttribute<ParserAttribute>()?.Name ?? "<null>"}");
        Logger.Debug($"Selected exporter: {Exporter?.GetType().GetCustomAttribute<ExporterAttribute>()?.Name ?? "<null>"}");
        Logger.Debug($"Selected font: {_fontFamily.Name ?? "<null>"}");
        Logger.Debug($"Read speed: {InputBytesPerFrame} bytes/frame ({InputBytesPerSecond} bytes/second)");
        Logger.Debug($"Waterfall duration will be around {TimeSpan.FromSeconds(InputFileStream.Length / InputBytesPerSecond)}.");
        Logger.Debug($"Video input:  {WaterfallWidth}×{WaterfallHeight}");
        Logger.Debug($"Audio input:  {AudioInputBytesPerFrame}bpf {AudioInputSamplesPerFrame}spf → {AudioInputSampleRate}Hz {AudioInputChannelCount}ch {8 * AudioInputSampleFormat.GetByteSize()}-bit");
        Logger.Debug($"Audio output: {AudioOutputBytesPerFrame}bpf {AudioOutputSamplesPerFrame}spf → {AudioOutputSampleRate}Hz {AudioOutputChannelCount}ch {8 * AudioOutputSampleFormat.GetByteSize()}-bit");
    }

    private void InitializeFonts()
    {
        Logger.Info("Loading fonts…");
        Logger.Debug($"Requested font: '{FontName}'.");

        if (_fontCollection == null)
        {
            _fontCollection = new();
            _fontCollection.AddSystemFonts();
        }

        if (FontName != null)
        {
            // Try getting the font by the font name specified by the user
            if (!_fontCollection.TryGet(FontName, out _fontFamily))
            {
                Logger.Error($"Cannot find font '{FontName}'.");
            }
        }

        if (_fontFamily.Name == null)
        {
            // 默认字体 unifont
            if (_fontCollection.TryGet("unifont", out _fontFamily))
            {
                // unifont upper
                _fontCollection.TryGet("unifont upper", out _emojiFontFamily);
            }

            else if (Environment.OSVersion.Platform == PlatformID.Win32NT && _fontCollection.TryGet("Segoe UI Symbol", out _fontFamily))
            {
            // 没有以上字体后使用 Segoe UI Symbol
            }
            else
            {
                // 最后回退 Consolas / Source Code Pro
                _fontFamily = _fontCollection.Get(Environment.OSVersion.Platform == PlatformID.Win32NT ? "Consolas" : "Source Code Pro");
            }
        }

        // 根据分辨率缩放字体大小
        float scale = ResolutionScale;
        _font48 = _fontFamily.CreateFont(48f * scale, FontStyle.Regular);
        _font32 = _fontFamily.CreateFont(32f * scale, FontStyle.Regular);
        _font24 = _fontFamily.CreateFont(24f * scale, FontStyle.Regular);
        _font16 = _fontFamily.CreateFont(16f * scale, FontStyle.Regular);
    }

    private void InitializeExporter()
    {
        Logger.Info("Setting up exporter…");
        Logger.Debug($"Requested exporter: '{ExporterId}'.");

        if (ExporterId != null)
        {
            var availableExporters = Utils.GetTypesWithAttribute<ExporterAttribute>();
            foreach (var exporterKvp in availableExporters)
            {
                var exporterAttr = exporterKvp.Key;
                if (exporterAttr.Id != ExporterId)
                {
                    continue;
                }
                Exporter = (IExporter)Activator.CreateInstance(exporterKvp.Value);
            }
            if (Exporter == null)
            {
                Logger.Fail($"Unknown exporter ID: '{ExporterId}'.");
                return;
            }
        }
        else
        {
            Logger.Debug("No exporter requested. Using SDL…");
            Exporter = new SdlExporter();
        }

        Exporter.Generator = this;

        // 如果是 FFmpeg 导出器，应用硬件加速设置和 NVENC 配置
        if (Exporter is FfmpegExporter ffmpegExporter)
        {
            ffmpegExporter.HardwareAccel = HardwareAccel;
            ffmpegExporter.NvencPreset = NvencPreset;
            ffmpegExporter.NvencTune = NvencTune;
            ffmpegExporter.NvencRateControl = NvencRateControl;
            ffmpegExporter.NvencTemporalAQ = NvencTemporalAQ;
            ffmpegExporter.NvencSpatialAQ = NvencSpatialAQ;
            ffmpegExporter.NvencLookahead = NvencLookahead;
            Logger.Debug($"Hardware acceleration: {HardwareAccel}");
        }

        if (AdditionalCliArguments.Count > 0)
        {
            Logger.Info($"Setting exporter properties from command line arguments…");
            foreach (var argKvp in AdditionalCliArguments)
            {
                var targetProp = Utils.GetPropertyFromCliArgument(argKvp.Key);

                if (targetProp.DeclaringType.GetInterfaces().Contains(typeof(IExporter)))
                {
                    CliParameterAttribute.SetPropertyFromCliArgument(targetProp, Exporter, argKvp.Value);
                }
                else
                {
                    Logger.Error($"Unrecognized CLI argument name: '{argKvp.Key}'.");
                }
            }
        }
    }

    // 解析子文件列表，如果解析不到则构造一个“覆盖整文件”的虚拟子文件
    private void ParseSubfiles()
    {
        IEnumerable<SubFile> subFiles = null;

        if (Parser != null)
        {
            Logger.Info("Parsing subfiles…");

            Parser.InputStream = InputFileStream;
            Parser.AuxiliaryInputStream = InputAuxiliaryFileStream;

            subFiles = Parser.GetSubFiles();

            _subfiles =
            [
                .. subFiles
                .OrderBy(sf => sf.StartOffset)
                .Select(sf => Utils.ParseSubfile(InputFileStream, sf))
            ];
        }

        Logger.Debug($"Total number of subfiles: {_subfiles.Count}");

        // 如果没有解析到任何子文件，则构造一个覆盖整个输入文件的虚拟子文件，
        // 这样右上区域始终可以显示至少一条“歌曲/文件”记录
        if (_subfiles.Count == 0 && InputFileStream != null && !string.IsNullOrEmpty(InputFilePath))
        {
            _subfiles =
            [
                SubFile.FromWholeFile(InputFilePath, InputFileStream.Length)
            ];
            Logger.Debug("No subfiles parsed; created a virtual subfile covering the whole input file.");
        }

        if (LogAllSubfiles)
        {
            foreach (var sf in _subfiles)
            {
                Logger.Debug($"\t{sf.IconString ?? "–"} '{sf.Path}' {sf.StartOffset:X8}–{sf.EndOffset:X8}");
            }
        }

        // 尝试为每个子文件填充音频元数据（如果对应路径是可由 TagLib 识别的音频文件）
        PopulateSubfileMetadata();
    }

    /// 
    /// 使用 TagLib 为每个子文件填充基础音频元数据：专辑、碟号、曲号、曲名、艺术家和风格。
    /// 解析失败时记录 Debug 日志，不影响整体流程。
    
    private void PopulateSubfileMetadata()
    {
        foreach (var sf in _subfiles)
        {
            // 目录或路径为空时跳过
            if (sf == null || sf.IsDirectory || string.IsNullOrWhiteSpace(sf.Path))
            {
                continue;
            }

            try
            {
                using var tagFile = TagLib.File.Create(sf.Path);
                var tag = tagFile.Tag;
                if (tag == null)
                {
                    continue;
                }

                // 专辑名：优先使用 TagLib 的 Album
                if (string.IsNullOrWhiteSpace(sf.AlbumTitle) && !string.IsNullOrWhiteSpace(tag.Album))
                {
                    sf.AlbumTitle = tag.Album;
                }

                // 专辑作者/专辑艺术家：使用 TagLib 的 AlbumArtists
                if (string.IsNullOrWhiteSpace(sf.AlbumArtistName) && tag.AlbumArtists != null && tag.AlbumArtists.Length > 0)
                {
                    sf.AlbumArtistName = string.Join(", ", tag.AlbumArtists);
                }

                // 碟号 / 曲号
                if (!sf.DiscNumber.HasValue && tag.Disc != 0)
                {
                    sf.DiscNumber = (int)tag.Disc;
                }
                if (!sf.TrackNumber.HasValue && tag.Track != 0)
                {
                    sf.TrackNumber = (int)tag.Track;
                }

                // 曲名：如无则回退文件名
                if (string.IsNullOrWhiteSpace(sf.TrackTitle))
                {
                    if (!string.IsNullOrWhiteSpace(tag.Title))
                    {
                        sf.TrackTitle = tag.Title;
                    }
                    else
                    {
                        sf.TrackTitle = sf.FileName;
                    }
                }

                // 艺术家 / 作曲家
                if (string.IsNullOrWhiteSpace(sf.ArtistName))
                {
                    if (tag.Performers != null && tag.Performers.Length > 0)
                    {
                        sf.ArtistName = string.Join(", ", tag.Performers);
                    }
                    else if (tag.Composers != null && tag.Composers.Length > 0)
                    {
                        sf.ArtistName = string.Join(", ", tag.Composers);
                    }
                }

                // 风格
                if (string.IsNullOrWhiteSpace(sf.Genre) && tag.Genres != null && tag.Genres.Length > 0)
                {
                    sf.Genre = string.Join(", ", tag.Genres);
                }
            }
            catch (Exception ex)
            {
                // 某些子文件路径不是实际音频文件时可能会抛异常，这里只做调试输出
                Logger.Debug($"Failed to read metadata for subfile '{sf.Path}': {ex.Message}");
            }
        }
    }

    // 预计算所有子文件的波形 RMS 值（用于底部进度条显示）
    // 使用 CSCore 解码音频文件获取真正的 PCM 采样数据，然后计算 RMS
    private void PrecomputeSubfileWaveforms()
    {
        Logger.Info("Precomputing waveform RMS for subfiles…");
        
        // 每个子文件生成固定数量的 RMS 值
        const int rmsCount = 256;
        
        foreach (var sf in _subfiles)
        {
            if (sf == null || sf.Length <= 0) continue;
            if (string.IsNullOrWhiteSpace(sf.Path) || !System.IO.File.Exists(sf.Path)) continue;
            
            try
            {
                // 使用 CSCore 解码音频文件获取真正的 PCM 采样
                using var waveSource = CodecFactory.Instance.GetCodec(sf.Path);
                using var sampleSource = waveSource.ToSampleSource();
                
                // 获取音频总采样数（单声道）
                long totalSamples = sampleSource.Length / sampleSource.WaveFormat.Channels;
                if (totalSamples <= 0) continue;
                
                // 计算每个 RMS 段的采样数
                long samplesPerSegment = totalSamples / rmsCount;
                if (samplesPerSegment < 64) samplesPerSegment = 64;
                
                float[] rmsValues = new float[rmsCount];
                float globalMaxRms = 0f;
                
                // 读取缓冲区（交织格式）
                int channelCount = sampleSource.WaveFormat.Channels;
                int bufferSize = (int)Math.Min(samplesPerSegment * channelCount, 65536);
                float[] buffer = new float[bufferSize];
                
                for (int i = 0; i < rmsCount; i++)
                {
                    // 计算该段的 RMS（均方根）值
                    double sumSquares = 0.0;
                    int sampleCount = 0;
                    long samplesToRead = samplesPerSegment * channelCount;
                    
                    while (samplesToRead > 0)
                    {
                        int toRead = (int)Math.Min(samplesToRead, bufferSize);
                        int read = sampleSource.Read(buffer, 0, toRead);
                        if (read <= 0) break;
                        
                        // 累加平方和（混合所有声道）
                        for (int j = 0; j < read; j++)
                        {
                            double sample = buffer[j];
                            sumSquares += sample * sample;
                            sampleCount++;
                        }
                        
                        samplesToRead -= read;
                    }
                    
                    // 计算 RMS 值
                    float rms = sampleCount > 0 ? (float)Math.Sqrt(sumSquares / sampleCount) : 0f;
                    rmsValues[i] = rms;
                    if (rms > globalMaxRms) globalMaxRms = rms;
                }
                
                // 峰值归一化：找到全局最大 RMS，然后将所有值按此缩放到 0-1 范围
                if (globalMaxRms > 0.0001f)
                {
                    for (int i = 0; i < rmsCount; i++)
                    {
                        rmsValues[i] = rmsValues[i] / globalMaxRms;
                    }
                }
                
                sf.WaveformPeaks = rmsValues;
                Logger.Debug($"Waveform computed for '{sf.FileName}': max RMS = {globalMaxRms:F4}");
            }
            catch (Exception ex)
            {
                Logger.Debug($"Failed to compute waveform for subfile '{sf.Path}': {ex.Message}");
            }
        }
        
        Logger.Info("Waveform precomputation completed.");
    }

    // 初始化解析器：优先用用户指定的 parser ID，否则根据扩展名自动猜测
    private void InitializeParser()
    {
        Logger.Info("Setting up parser…");
        var availableParsers = Utils.GetTypesWithAttribute<ParserAttribute>();

        if (InputFileFormatId != null)
        {
            Logger.Debug($"Requested parser: '{InputFileFormatId}'.");
            foreach (var parserKvp in availableParsers)
            {
                var parserAttr = parserKvp.Key;
                if (parserAttr.Id != InputFileFormatId)
                {
                    continue;
                }
                Parser = (IParser)Activator.CreateInstance(parserKvp.Value);
            }
            if (Parser == null)
            {
                Logger.Warning($"Unknown parser ID: '{InputFileFormatId}'. Skipping subfile listing.");
            }
        }
        else
        {
            Logger.Info("Guessing input format from file extension…");
            var inputFileExt = Path.GetExtension(InputFilePath).ToLower();

            foreach (var parserKvp in availableParsers)
            {
                var parserAttr = parserKvp.Key;
                if (parserAttr.FileExtensions.Contains(inputFileExt))
                {
                    Logger.Debug($"Parser '{parserAttr.Id}' recognizes '{inputFileExt}' as a valid file extension.");
                    Parser = (IParser)Activator.CreateInstance(parserKvp.Value);
                    break;
                }
            }
            if (Parser == null)
            {
                Logger.Warning("Unknown input format. Skipping subfile listing.");
            }
        }
    }

    // 根据当前输出分辨率更新瀑布图缩放和在画面中的位置
    internal void UpdateValues()
    {
        if (_frameContent == null || _frameContent.Width != OutputVideoWidth || _frameContent.Height != OutputVideoHeight)
        {
            _frameContent = new(OutputVideoWidth, OutputVideoHeight);
            
            // 使用分辨率缩放因子来计算瀑布尺寸（基于 1080p 的 768x768）
            float scale = ResolutionScale;
            WaterfallScaledWidth = (int)(768 * scale);
            WaterfallScaledHeight = (int)(768 * scale);

            _videoFrameX1 = OutputVideoWidth / (_subfiles.Count > 0 ? 4 : 2) - WaterfallScaledWidth / 2;
            if (_videoFrameX2 == 0) _videoFrameX2 = _videoFrameX1 + WaterfallScaledWidth;
            _videoFrameY1 = OutputVideoHeight / 2 - WaterfallScaledHeight / 2;
            if (_videoFrameY2 == 0) _videoFrameY2 = _videoFrameY1 + WaterfallScaledHeight;
            
            // 初始化布局缓存
            InitializeLayoutCache();
        }
    }
    
    // 初始化布局参数缓存，避免每帧重复计算
    private void InitializeLayoutCache()
    {
        float s = ResolutionScale;
        _layoutCache = new LayoutCache
        {
            Scale = s,
            RightPanelX1 = _videoFrameX2 + (int)(64 * s),
            RightPanelX2 = OutputVideoWidth - (int)(32 * s),
            SubfileH = 48f * s,
            SubfileRowH = 36f * s,
            IsValid = true
        };
        _layoutCache.SubfileX1 = _layoutCache.RightPanelX1;
        _layoutCache.SubfileX2 = _layoutCache.RightPanelX2;
        _layoutCache.AudioVisX1 = _layoutCache.RightPanelX1;
        _layoutCache.AudioVisX2 = _layoutCache.RightPanelX2;
        _layoutCache.ShadowY1 = (OutputVideoHeight / 2f) - _layoutCache.SubfileH * 8.5f;
        _layoutCache.ShadowY2 = (OutputVideoHeight / 2f) + _layoutCache.SubfileH * 6.5f;
        _layoutCache.RightPanelTop = _layoutCache.ShadowY1 + _layoutCache.SubfileH * 2f + 16f * s;
        _layoutCache.RightPanelBottom = _layoutCache.ShadowY2 - 16f * s;
        float rightPanelHeight = _layoutCache.RightPanelBottom - _layoutCache.RightPanelTop;
        _layoutCache.ListTop = _layoutCache.RightPanelTop;
        _layoutCache.ListBottom = _layoutCache.ListTop + rightPanelHeight * 0.20f;
        _layoutCache.BottomPanelHeight = 100f * s;
        _layoutCache.BottomY = OutputVideoHeight - _layoutCache.BottomPanelHeight - 16f * s;
        _layoutCache.CoverSize = 72f * s;
        _layoutCache.CoverX = 32f * s;
        _layoutCache.CoverY = _layoutCache.BottomY + (_layoutCache.BottomPanelHeight - _layoutCache.CoverSize) / 2f;
        _layoutCache.InfoX = _layoutCache.CoverX + _layoutCache.CoverSize + 16f * s;
        _layoutCache.TimeX = OutputVideoWidth - 32f * s;
        
        Logger.Info("Layout cache initialized for performance optimization.");
    }

    // 总生成流程
    public void Generate()
    {
        _timer.Start();

        GenerateIntro();
        if (_exitRequested)
        {
            OnFinish?.Invoke();
            return;
        }

        GenerateMainVideo();

        OnFinish?.Invoke();
    }

    // 生成开头的免责声明画面（带淡入淡出效果）
    private void GenerateIntro()
    {
        Logger.Info("Generating introduction…");

        var totalFrames = 5 * OutputFps;
        // 淡入淡出的帧数（由 IntroFadeDuration 参数控制）
        int fadeFrames = (int)(IntroFadeDuration * OutputFps);
        fadeFrames = Math.Clamp(fadeFrames, 1, (int)(totalFrames / 3)); // 最多占总时长的 1/3

        for (long frameNumber = 0; frameNumber < totalFrames; frameNumber++)
        {
            // 计算当前帧的不透明度
            // 淡入：前 fadeFrames 帧从 0 渐变到 1
            // 淡出：后 fadeFrames 帧从 1 渐变到 0
            // 中间：保持 1
            float opacity = 1f;
            if (frameNumber < fadeFrames)
            {
                // 淡入阶段
                opacity = frameNumber / (float)fadeFrames;
            }
            else if (frameNumber >= totalFrames - fadeFrames)
            {
                // 淡出阶段
                opacity = (totalFrames - frameNumber) / (float)fadeFrames;
            }
            opacity = Math.Clamp(opacity, 0f, 1f);

            // 背景色
            _frameContent.Mutate(ctx => ctx.Clear(new Rgba32(16, 16, 16, 255)));

            // 根据 opacity 计算文字颜色（通过 alpha 通道实现淡入淡出）
            byte alpha = (byte)(255 * opacity);
            var textColor = Color.FromRgba(255, 255, 255, alpha);

            _frameContent.Mutate(av => av
                .DrawText(new RichTextOptions(_font48)
                {
                    Origin = new Vector2(OutputVideoWidth / 2f, OutputVideoHeight / 2f),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                }, "声明\n\n本视频由\nAudio-visualizer_extended-binary-waterfall\n项目进行生成", textColor)
                .DrawText(new RichTextOptions(_font24)
                {
                    Origin = new Vector2(OutputVideoWidth / 2f, OutputVideoHeight - 128),
                    HorizontalAlignment = HorizontalAlignment.Center,
                }, $"Starting in {(totalFrames - frameNumber) / (float)OutputFps:N1} seconds…", textColor)
                .DrawProgressBar(frameNumber / (float)totalFrames,
                    (int)(OutputVideoWidth * 0.3f),
                    (int)(OutputVideoWidth * 0.7f),
                    OutputVideoHeight - 64,
                    opacity)); // 传递 opacity 给进度条

            Exporter.PushNewFrame(_frameContent, _outputAudioBuffer, _timer.Elapsed.TotalSeconds);
            _timer.Restart();

            OnProgress?.Invoke(frameNumber / (float)totalFrames);

            if (_exitRequested)
            {
                break;
            }
        }
    }

    // 主体瀑布 + 右侧列表 + 右下音频可视化
    private void GenerateMainVideo()
    {
        Logger.Info("Generating binary waterfall…");

        string avSettingsString =
            $"{AudioOutputSampleRate} Hz, PCM {(AudioOutputSampleFormat.IsSigned() ? "signed" : "unsigned")} {8 * AudioOutputSampleFormat.GetByteSize()}-bit, " +
            $"{(AudioOutputChannelCount == 2 ? "stereo" : $"{AudioOutputChannelCount} ch")}\n" +
            $"RGBA (32bpp), {WaterfallWidth} px/line";
        string readSpeedString = $"{InputBytesPerSecond / 1024} KiB/s";

        float subfileWindowIndex = 0f;
        long currentOffset = 0;
        int playHeadRelPos = 0;

        using var targetFileReader = new BinaryReader(InputFileStream);

        while (currentOffset < InputFileStream.Length)
        {
            playHeadRelPos = 0;
            long frameStartByteOffset = currentOffset.Align(WaterfallWidth * 4) - (WaterfallFrameLength / 2);
            if (frameStartByteOffset < 0)
            {
                playHeadRelPos = (int)-(frameStartByteOffset / (WaterfallWidth * 4));
                frameStartByteOffset = 0;
            }
            else if (frameStartByteOffset + WaterfallFrameLength >= InputFileStream.Length)
            {
                playHeadRelPos = (int)((InputFileStream.Length - (frameStartByteOffset + WaterfallFrameLength)) / (WaterfallWidth * 4));
                frameStartByteOffset = InputFileStream.Length - WaterfallFrameLength;
            }

            InputFileStream.Position = frameStartByteOffset;
            byte[] currentVideoBuffer = targetFileReader.ReadBytes(WaterfallFrameLength);

            // 获取音频缓冲
            if (_audioSampleSource != null)
            {
                int samplesPerFrame = AudioOutputSamplesPerFrame;
                if (_audioSampleBuffer == null || _audioSampleBuffer.Length < samplesPerFrame)
                {
                    _audioSampleBuffer = new float[samplesPerFrame];
                }

                int readSamples = _audioSampleSource.Read(_audioSampleBuffer, 0, samplesPerFrame);
                if (readSamples < samplesPerFrame)
                {
                    Array.Clear(_audioSampleBuffer, readSamples, samplesPerFrame - readSamples);
                }

                // 将float PCM 写入输出缓冲供可视化使用
                _outputAudioBuffer.LoadFromInterleavedFloats(_audioSampleBuffer, AudioOutputChannelCount);
            }
            else
            {
                long audioFrameStartByteOffset = currentOffset.Align(AudioInputSampleFormat.GetByteSize()) - (InputBytesPerFrame / 2);
                if (audioFrameStartByteOffset < 0)
                {
                    audioFrameStartByteOffset = 0;
                }
                else if (audioFrameStartByteOffset + InputBytesPerFrame >= InputFileStream.Length)
                {
                    audioFrameStartByteOffset = InputFileStream.Length - InputBytesPerFrame;
                }
                long audioFrameEndByteOffset = audioFrameStartByteOffset + InputBytesPerFrame;

                InputFileStream.Position = audioFrameStartByteOffset;
                byte[] currentAudioBuffer = targetFileReader.ReadBytes(InputBytesPerFrame);

                _inputAudioBuffer.LoadFromByteArray(currentAudioBuffer, AudioInputSampleFormat);
                _outputAudioBuffer = new AudioBuffer(_inputAudioBuffer)
                    .Resample(AudioOutputSamplesPerFramePerChannel)
                    .RemixChannels(AudioOutputChannelCount);
            }

            // 3. 将视频字节缓冲转换为 Image，并翻转+缩放成用于绘制的瀑布视图
            _viewportFramebuf = Image.LoadPixelData<Rgba32>(currentVideoBuffer, WaterfallWidth, WaterfallHeight);
            _viewportFramebuf.ProcessPixelRows(pa =>
            {
                for (int y = 0; y < pa.Height; y++)
                {
                    var row = pa.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        row[x].A = 255;
                    }
                }
            });
            _viewportFramebuf.Mutate(vctx => vctx
                .Flip(FlipMode.Vertical)
                .Resize(WaterfallScaledWidth, WaterfallScaledHeight, new NearestNeighborResampler()));

            // 4. 计算当前帧覆盖到哪些子文件，并更新右侧列表窗位置
            var subfilesInFrame = _subfiles
                .Select((sf, i) => new { key = i, value = sf })
                .Where(kvp => kvp.value.Intersects(currentOffset - (InputBytesPerFrame / 2),
                    currentOffset + (InputBytesPerFrame / 2)))
                .ToList();
            var currentSubfile = subfilesInFrame.LastOrDefault();

            if (currentSubfile != null)
            {
                subfileWindowIndex = 0.2f * subfileWindowIndex + 0.8f * currentSubfile.key;
            }

            // 5. 实际绘制一帧：左侧瀑布 + 右侧上部列表 + 右下音频可视化 + 顶/底渐变 + 文字信息
            _frameContent.Mutate(ctx =>
            {
                // 5.1 清屏
                ctx.Clear(new Rgba32(16, 16, 16, 255));

                // 分辨率缩放因子（所有布局常量都乘以此因子）
                float s = ResolutionScale;

                // 5.2 右侧整体面板：上部列表 + 下部音频可视化
                int rightPanelX1 = _videoFrameX2 + (int)(64 * s);       // 瀑布右侧留出间距
                int rightPanelX2 = OutputVideoWidth - (int)(32 * s);    // 靠右留边
                int subfileX1 = rightPanelX1;
                int subfileX2 = rightPanelX2;
                int audioVisX1 = rightPanelX1;
                int audioVisX2 = rightPanelX2;

                float subfileH = 48f * s; // 每一行子文件条目的高度

                // 顶部/底部淡出渐变以画面中线为基准
                float shadowY1 = (OutputVideoHeight / 2f) - subfileH * 8.5f;
                float shadowY2 = (OutputVideoHeight / 2f) + subfileH * 6.5f;

                // 右侧面板整体垂直范围：放在两个淡出区域之间，避免列表/波形/频谱被淡出遮挡
                float rightPanelTop = shadowY1 + subfileH * 2f + 16f * s;
                float rightPanelBottom = shadowY2 - 16f * s;
                float rightPanelHeight = rightPanelBottom - rightPanelTop;
                float listHeight = rightPanelHeight * 0.20f; // 顶部 20% 用于列表
                float listTop = rightPanelTop;
                float listBottom = listTop + listHeight;

                // 显示条目数
                int firstSubfileIndex = Math.Max(0, (int)(subfileWindowIndex - 2));
                int lastSubfileIndex = Math.Min(_subfiles.Count - 1, (int)Math.Ceiling(subfileWindowIndex + 7));
                float subfileRowH = 36f * s; // 行高

                float subfileY = listTop + subfileRowH / 2f - (subfileWindowIndex - firstSubfileIndex) * subfileRowH;

                // 绘制右上“歌曲/子文件列表”
                for (int sfi = firstSubfileIndex; sfi <= lastSubfileIndex; sfi++)
                {
                    if (sfi < 0 || sfi >= _subfiles.Count)
                    {
                        subfileY += subfileRowH;
                        continue;
                    }

                    var subfile = _subfiles[sfi];
                    bool isMainSubfile = sfi == (currentSubfile?.key ?? -1);

                    // 使用更小的字体（_font24 代替 _font32）
                    ctx.DrawText(_drawOpts, new RichTextOptions(_font24)
                    {
                        Origin = new Vector2(subfileX1, subfileY),
                        VerticalAlignment = VerticalAlignment.Center,
                    }, isMainSubfile ? "▶" : " ", new SolidBrush(Color.White), null)
                    .DrawTextAndCache(_drawOpts, new RichTextOptions(_font24)
                    {
                        Origin = new Vector2(subfileX1 + 24 * s, subfileY),
                        VerticalAlignment = VerticalAlignment.Center,
                        FallbackFontFamilies = _emojiFontFamily.Name != null ? [_emojiFontFamily] : null,
                    }, $"{Utils.GetFileTypeEmoji(subfile)} {Utils.TruncateString(BuildSubfileDisplayLine(subfile), 50)}", new SolidBrush(Color.White), null)
                    .DrawTextAndCache(_drawOpts, new RichTextOptions(_font24)
                    {
                        Origin = new Vector2(subfileX2, subfileY),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                    }, Utils.ToByteSizeString(subfile.Length), new SolidBrush(Color.DimGray), null);

                    if (isMainSubfile)
                    {
                        float percentOfSubfile = (currentOffset - subfile.StartOffset) / (float)subfile.Length;
                        float progressY = subfileY + 22 * s; // 进度条间距调整（噢噢噢噢）

                        ctx.DrawText(_drawOpts, new RichTextOptions(_font16)
                        {
                            Origin = new PointF(subfileX1 + 40 * s, progressY),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        }, $"{(int)Math.Clamp(percentOfSubfile * 100, 0, 100)} %", new SolidBrush(Color.White), null)
                        .DrawProgressBar(percentOfSubfile, (int)(subfileX1 + 60 * s), subfileX2, progressY);
                    }

                    subfileY += subfileRowH;
                }

                // 5.3 左侧瀑布视图
                ctx.DrawImage(_viewportFramebuf, new Point(_videoFrameX1, _videoFrameY1), 1f)
                .DrawText(new RichTextOptions(_font32)
                {
                    Origin = new Vector2(32, (OutputVideoHeight / 2f) + (playHeadRelPos * (WaterfallScaledHeight / (float)WaterfallHeight))),
                    VerticalAlignment = VerticalAlignment.Center,
                }, "▶", Color.White);

                // 5.4 右下音频可视化（波形 + 频谱）
                float audioVisTop = listBottom + 16f;
                float audioVisBottom = rightPanelBottom;
                if (audioVisBottom > audioVisTop + 16f && _outputAudioBuffer != null)
                {
                    var audioVisRegion = new RectangleF(audioVisX1, audioVisTop, audioVisX2 - audioVisX1, audioVisBottom - audioVisTop);
                    DrawAudioVisualizer(ctx, audioVisRegion, _outputAudioBuffer);
                }

                // 5.5 顶部/底部渐变遮罩 + 专辑/目录抬头文字
                string albumHeaderText;
                {
                    var sfValue = currentSubfile?.value;
                    if (sfValue != null && !string.IsNullOrWhiteSpace(sfValue.AlbumTitle))
                    {
                        albumHeaderText = sfValue.AlbumTitle;
                        if (!string.IsNullOrWhiteSpace(sfValue.AlbumArtistName))
                        {
                            albumHeaderText += " // " + sfValue.AlbumArtistName;
                        }
                    }
                    else
                    {
                        albumHeaderText = sfValue?.FileDirectory ?? string.Empty;
                    }
                }

                ctx.Fill(
                    new LinearGradientBrush(
                        new PointF(0, shadowY1),
                        new PointF(0, shadowY1 + subfileH * 2f),
                        GradientRepetitionMode.None,
                        new(0.5f, Color.FromRgba(16, 16, 16, 255)),
                        new(1, Color.FromRgba(16, 16, 16, 0))
                    ),
                    new RectangleF(0, shadowY1, OutputVideoWidth, subfileH * 2f)
                )
                .Fill(
                    new LinearGradientBrush(
                        new PointF(0, shadowY2),
                        new PointF(0, shadowY2 + subfileH * 2f),
                        GradientRepetitionMode.None,
                        new(0, Color.FromRgba(16, 16, 16, 0)),
                        new(0.5f, Color.FromRgba(16, 16, 16, 255))
                    ),
                    new RectangleF(0, shadowY2, OutputVideoWidth, subfileH * 2f)
                )
                .DrawTextAndCache(new RichTextOptions(_font24)
                {
                    // 将专辑/目录文字放在右侧面板顶部附近，避免被顶部淡出渐变盖住
                    Origin = new Vector2(subfileX1 + 40, rightPanelTop - 24f),
                    VerticalAlignment = VerticalAlignment.Center,
                }, Utils.TruncateString(albumHeaderText ?? string.Empty, 72), Color.DimGray);

                // 5.6 状态信息 / 标题 / 作者等
                ctx.DrawTextAndCache(new RichTextOptions(_font24)
                {
                    Origin = new Vector2(32, 32),
                }, "A/V SETTINGS", Color.DimGray)
                .DrawText(new RichTextOptions(_font32)
                {
                    Origin = new Vector2(32, 32 + 24),
                }, avSettingsString, Color.White)
                .DrawTextAndCache(new RichTextOptions(_font24)
                {
                    Origin = new Vector2(OutputVideoWidth - 32, 32),
                    HorizontalAlignment = HorizontalAlignment.Right,
                }, "ABS. OFFSET", Color.DimGray)
                .DrawText(new RichTextOptions(_font32)
                {
                    Origin = new Vector2(OutputVideoWidth - 32, 32 + 24),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    TextAlignment = TextAlignment.End,
                }, $"{currentOffset / 1048576f:N2} MiB\n0x{currentOffset:X8}", Color.White)
                .DrawTextAndCache(new RichTextOptions(_font24)
                {
                    Origin = new Vector2(OutputVideoWidth - 256, 32),
                    HorizontalAlignment = HorizontalAlignment.Right,
                }, "BITRATE", Color.DimGray)
                .DrawText(new RichTextOptions(_font32)
                {
                    Origin = new Vector2(OutputVideoWidth - 256, 32 + 24),
                    HorizontalAlignment = HorizontalAlignment.Right,
                }, readSpeedString, Color.White);

                if (Author != null)
                {
                    ctx.DrawTextAndCache(new RichTextOptions(_font32)
                    {
                        Origin = new Vector2(OutputVideoWidth / 2f, 32 + 24),
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    }, Author, Color.White);
                }

                // 底部音乐播放器 UI
                DrawBottomPlayerUI(ctx, s, currentOffset, currentSubfile?.key ?? -1, currentSubfile?.value);
            });

            Exporter.PushNewFrame(_frameContent, _outputAudioBuffer, _timer.Elapsed.TotalSeconds);
            _timer.Restart();

            currentOffset += InputBytesPerFrame;

            OnProgress?.Invoke(currentOffset / (float)InputFileStream.Length);

            if (_exitRequested)
            {
                break;
            }
        }

        // 生成结尾淡出效果
        if (!_exitRequested)
        {
            GenerateOutro();
        }

        Exporter.Finish();
    }

    // 绘制底部音乐播放器 UI
    private void DrawBottomPlayerUI(IImageProcessingContext ctx, float s, long currentOffset, int subfileIdx, SubFile subfile)
    {
        float bottomPanelHeight = 100f * s;
        float bottomY = OutputVideoHeight - bottomPanelHeight - 16f * s;
        float coverSize = 72f * s;
        float coverX = 32f * s;
        float coverY = bottomY + (bottomPanelHeight - coverSize) / 2f;
        float infoX = coverX + coverSize + 16f * s;
        float timeX = OutputVideoWidth - 32f * s;
        
        string trackName = "";
        string artistName = "";
        string genreText = "";
        TimeSpan currentTime = TimeSpan.Zero;
        TimeSpan totalTime = TimeSpan.Zero;
        float trackProgress = 0f;
        Image coverImage = null;
        
        if (subfile != null)
        {
            trackName = !string.IsNullOrWhiteSpace(subfile.TrackTitle) ? subfile.TrackTitle : subfile.FileName;
            artistName = subfile.ArtistName ?? subfile.AlbumArtistName ?? "";
            genreText = subfile.Genre ?? "";
            coverImage = subfile.Icon;
            
            if (InputBytesPerSecond > 0)
            {
                totalTime = TimeSpan.FromSeconds(subfile.Length / (double)InputBytesPerSecond);
                long offsetInTrack = currentOffset - subfile.StartOffset;
                currentTime = TimeSpan.FromSeconds(Math.Max(0, offsetInTrack) / (double)InputBytesPerSecond);
                trackProgress = Math.Clamp((float)offsetInTrack / subfile.Length, 0f, 1f);
            }
        }
        
        int currSubIdx = subfileIdx;
        float transitionT = 1f;
        bool isInTransition = false;
        long transitionBytes = InputBytesPerSecond;
        
        if (currSubIdx != _lastSubfileIndex && currSubIdx >= 0 && subfile != null)
        {
            _transitionStartOffset = subfile.StartOffset - transitionBytes;
            _transitionEndOffset = subfile.StartOffset + transitionBytes;
            _lastSubfileIndex = currSubIdx;
        }
        
        if (_transitionStartOffset >= 0 && currentOffset >= _transitionStartOffset && currentOffset <= _transitionEndOffset)
        {
            isInTransition = true;
            float rawT = (currentOffset - _transitionStartOffset) / (float)(_transitionEndOffset - _transitionStartOffset);
            transitionT = EaseOutCubic(Math.Clamp(rawT, 0f, 1f));
        }
        
        byte contentAlpha = (byte)(255 * transitionT);
        Color labelColor = Color.FromRgba(105, 105, 105, contentAlpha);
        Color textColor = Color.FromRgba(255, 255, 255, contentAlpha);
        
        var coverRect = new RectangleF(coverX, coverY, coverSize, coverSize);
        ctx.Fill(Color.FromRgba(32, 32, 32, 255), coverRect);
        ctx.Draw(Color.FromRgba(200, 200, 200, 255), 2f, coverRect);
        
        if (coverImage != null)
        {
            using var scaledCover = coverImage.Clone(imgCtx => imgCtx.Resize((int)coverSize - 4, (int)coverSize - 4));
            ctx.DrawImage(scaledCover, new Point((int)(coverX + 2), (int)(coverY + 2)), transitionT);
        }
        else
        {
            ctx.DrawText(new RichTextOptions(_font48)
            {
                Origin = new Vector2(coverX + coverSize / 2, coverY + coverSize / 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }, "♪", Color.FromRgba(100, 100, 100, contentAlpha));
        }
        
        float labelY = bottomY + 4f * s;
        float valueY = labelY + 18f * s;
        float waveformY = valueY + 32f * s;
        
        float trackNameWidth = MeasureTextWidth(trackName, _font32);
        float separatorWidth = MeasureTextWidth(" // ", _font32);
        float artistNameWidth = MeasureTextWidth(artistName, _font32);
        float bracketWidth = MeasureTextWidth(" [", _font32);
        
        bool hasArtist = !string.IsNullOrWhiteSpace(artistName);
        bool hasGenre = !string.IsNullOrWhiteSpace(genreText);
        
        float titleLabelX = infoX;
        // Composer: 标签对齐到 artistName 首字符（即 " // " 之后）
        float composerLabelX = infoX + trackNameWidth + separatorWidth;
        // Genre: 标签对齐到 "[" 符号（即 " [" 位置）
        float genreValueStartX = infoX + trackNameWidth + (hasArtist ? separatorWidth + artistNameWidth : 0) + bracketWidth;
        float labelOffsetX = isInTransition ? (1f - transitionT) * 50f * s : 0f;
        
        ctx.DrawText(new RichTextOptions(_font16) { Origin = new Vector2(titleLabelX + labelOffsetX, labelY) }, "Title:", labelColor);
        if (hasArtist) ctx.DrawText(new RichTextOptions(_font16) { Origin = new Vector2(composerLabelX + labelOffsetX, labelY) }, "Composer:", labelColor);
        if (hasGenre) ctx.DrawText(new RichTextOptions(_font16) { Origin = new Vector2(genreValueStartX + labelOffsetX, labelY) }, "Genre:", labelColor);
        ctx.DrawText(new RichTextOptions(_font16) { Origin = new Vector2(timeX, labelY), HorizontalAlignment = HorizontalAlignment.Right }, "Time:", labelColor);
        
        string displayInfo = trackName;
        if (hasArtist) displayInfo += $" // {artistName}";
        if (hasGenre) displayInfo += $" [{genreText}]";
        
        ctx.DrawText(new RichTextOptions(_font32) { Origin = new Vector2(infoX, valueY), WrappingLength = timeX - infoX - 120f * s }, Utils.TruncateString(displayInfo, 55), textColor);
        
        string timeString = $"{(int)currentTime.TotalMinutes}:{currentTime.Seconds:D2} / {(int)totalTime.TotalMinutes}:{totalTime.Seconds:D2}";
        ctx.DrawText(new RichTextOptions(_font32) { Origin = new Vector2(timeX, valueY), HorizontalAlignment = HorizontalAlignment.Right }, timeString, textColor);
        
        float waveformX1 = infoX;
        float waveformX2 = timeX;
        float waveformHeight = 20f * s;
        var waveformRect = new RectangleF(waveformX1, waveformY, waveformX2 - waveformX1, waveformHeight);
        ctx.Fill(Color.FromRgba(40, 40, 40, contentAlpha), waveformRect);
        
        float barWidth = 2f;
        float barSpacing = 1f;
        int totalBars = (int)(waveformRect.Width / (barWidth + barSpacing));
        float progressX = waveformRect.X + waveformRect.Width * trackProgress;
        float gapWidth = 4f * s;
        
        // 获取预计算的波形峰值数据
        float[] waveformPeaks = subfile?.WaveformPeaks;
        bool hasRealWaveform = waveformPeaks != null && waveformPeaks.Length > 0;
        
        for (int i = 0; i < totalBars; i++)
        {
            float barX = waveformRect.X + i * (barWidth + barSpacing);
            
            float heightRatio;
            if (hasRealWaveform)
            {
                // 使用真实波形数据，映射到当前柱状图位置
                int peakIndex = (int)((float)i / totalBars * waveformPeaks.Length);
                peakIndex = Math.Clamp(peakIndex, 0, waveformPeaks.Length - 1);
                // 波形峰值已归一化到 0-1 范围，添加最小高度保证可见性
                heightRatio = 0.15f + waveformPeaks[peakIndex] * 0.85f;
            }
            else
            {
                // 无真实波形数据时使用伪随机波形
                heightRatio = 0.3f + 0.6f * (float)Math.Abs(Math.Sin(i * 0.4 + currSubIdx * 0.1));
            }
            
            float barHeight = waveformHeight * heightRatio * 0.85f;
            float barY = waveformRect.Y + (waveformHeight - barHeight) / 2f;
            if (barX > progressX - gapWidth && barX < progressX + gapWidth) continue;
            Color barColor = barX < progressX ? Color.FromRgba(100, 100, 100, contentAlpha) : Color.FromRgba(220, 220, 220, contentAlpha);
            ctx.Fill(barColor, new RectangleF(barX, barY, barWidth, barHeight));
        }
        
        float headlineWidth = 2f;
        float borderWidth = 1f;
        ctx.Fill(Color.FromRgba(30, 30, 30, contentAlpha), new RectangleF(progressX - headlineWidth / 2 - borderWidth, waveformRect.Y - 2f, borderWidth, waveformHeight + 4f));
        ctx.Fill(Color.FromRgba(255, 255, 255, contentAlpha), new RectangleF(progressX - headlineWidth / 2, waveformRect.Y - 2f, headlineWidth, waveformHeight + 4f));
        ctx.Fill(Color.FromRgba(30, 30, 30, contentAlpha), new RectangleF(progressX + headlineWidth / 2, waveformRect.Y - 2f, borderWidth, waveformHeight + 4f));
    }

    // 生成结尾淡出画面
    private void GenerateOutro()
    {
        Logger.Info("Generating outro fade…");
        int fadeFrames = (int)(OutroFadeDuration * OutputFps);
        if (fadeFrames < 1) fadeFrames = 1;

        for (int frameNumber = 0; frameNumber < fadeFrames; frameNumber++)
        {
            float opacity = 1f - (frameNumber / (float)fadeFrames);
            byte overlayAlpha = (byte)(255 * (1f - Math.Clamp(opacity, 0f, 1f)));

            _frameContent.Mutate(ctx =>
            {
                ctx.Fill(Color.FromRgba(16, 16, 16, overlayAlpha),
                    new RectangleF(0, 0, OutputVideoWidth, OutputVideoHeight));
            });

            Exporter.PushNewFrame(_frameContent, _outputAudioBuffer, _timer.Elapsed.TotalSeconds);
            _timer.Restart();

            OnProgress?.Invoke(1f);

            if (_exitRequested)
            {
                break;
            }
        }
    }

    // 频谱平滑缓冲
    private float[] _smoothedSpectrum = null;
    // 音频历史缓冲（用于更大的 FFT 窗口以提高低频分辨率）
    private float[] _audioHistoryBuffer = null;
    private int _audioHistoryWritePos = 0;
    // FFT 旋转因子缓存
    private double[] _fftCosTable = null;
    private double[] _fftSinTable = null;
    private int _fftCachedSize = 0;
    // FFT 输入输出缓冲区复用
    private double[] _fftReal = null;
    private double[] _fftImag = null;
    private float[] _fftMagnitudes = null;
    // 位逆序查找表
    private int[] _bitReverseTable = null;
    // Hann 窗口缓存
    private float[] _hannWindow = null;

    // 验证并获取有效的 FFT 大小（必须是 2 的幂次方）
    private int GetValidFftSize()
    {
        int size = Math.Clamp(FftSize, 512, 8192);
        // 确保是 2 的幂次方
        int power = (int)Math.Log2(size);
        return 1 << power;
    }

    // 预计算位逆序查找表
    private void PrecomputeBitReverseTable(int n)
    {
        if (_bitReverseTable != null && _bitReverseTable.Length == n) return;
        
        _bitReverseTable = new int[n];
        int bits = (int)Math.Log2(n);
        for (int i = 0; i < n; i++)
        {
            int result = 0;
            int x = i;
            for (int b = 0; b < bits; b++)
            {
                result = (result << 1) | (x & 1);
                x >>= 1;
            }
            _bitReverseTable[i] = result;
        }
    }

    // 预计算 Hann 窗口
    private void PrecomputeHannWindow(int n)
    {
        if (_hannWindow != null && _hannWindow.Length == n) return;
        
        _hannWindow = new float[n];
        double factor = 2.0 * Math.PI / (n - 1);
        for (int i = 0; i < n; i++)
        {
            _hannWindow[i] = 0.5f * (1f - (float)Math.Cos(factor * i));
        }
    }

    // SIMD 加速的 Cooley-Tukey FFT 实现
    private void ComputeFFT(double[] real, double[] imag, int n)
    {
        // 使用预计算的位逆序表进行重排
        PrecomputeBitReverseTable(n);
        for (int i = 0; i < n; i++)
        {
            int j = _bitReverseTable[i];
            if (j > i)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        // 缓存旋转因子
        if (_fftCachedSize != n)
        {
            _fftCosTable = new double[n / 2];
            _fftSinTable = new double[n / 2];
            double angleStep = -2.0 * Math.PI / n;
            for (int i = 0; i < n / 2; i++)
            {
                double angle = angleStep * i;
                _fftCosTable[i] = Math.Cos(angle);
                _fftSinTable[i] = Math.Sin(angle);
            }
            _fftCachedSize = n;
        }

        // Cooley-Tukey 蝶形运算（SIMD 优化的内层循环）
        int vectorSize = Vector<double>.Count;
        
        for (int size = 2; size <= n; size *= 2)
        {
            int halfSize = size / 2;
            int tableStep = n / size;
            
            for (int i = 0; i < n; i += size)
            {
                int j = 0;
                
                // SIMD 向量化处理（如果支持且数据量足够）
                if (Vector.IsHardwareAccelerated && halfSize >= vectorSize)
                {
                    for (; j <= halfSize - vectorSize; j += vectorSize)
                    {
                        // 加载旋转因子
                        var cosVec = new Vector<double>(_fftCosTable, j * tableStep);
                        var sinVec = new Vector<double>(_fftSinTable, j * tableStep);
                        
                        // 加载实部和虚部
                        var realHi = new Vector<double>(real, i + j + halfSize);
                        var imagHi = new Vector<double>(imag, i + j + halfSize);
                        var realLo = new Vector<double>(real, i + j);
                        var imagLo = new Vector<double>(imag, i + j);
                        
                        // 复数乘法: (a+bi)(c+di) = (ac-bd) + (ad+bc)i
                        var tRe = realHi * cosVec - imagHi * sinVec;
                        var tIm = realHi * sinVec + imagHi * cosVec;
                        
                        // 蝶形运算
                        (realLo + tRe).CopyTo(real, i + j);
                        (imagLo + tIm).CopyTo(imag, i + j);
                        (realLo - tRe).CopyTo(real, i + j + halfSize);
                        (imagLo - tIm).CopyTo(imag, i + j + halfSize);
                    }
                }
                
                // 处理剩余元素（标量运算）
                for (; j < halfSize; j++)
                {
                    int idx = j * tableStep;
                    double tRe = real[i + j + halfSize] * _fftCosTable[idx] - imag[i + j + halfSize] * _fftSinTable[idx];
                    double tIm = real[i + j + halfSize] * _fftSinTable[idx] + imag[i + j + halfSize] * _fftCosTable[idx];
                    
                    real[i + j + halfSize] = real[i + j] - tRe;
                    imag[i + j + halfSize] = imag[i + j] - tIm;
                    real[i + j] += tRe;
                    imag[i + j] += tIm;
                }
            }
        }
    }

    // SIMD 加速的幅度计算
    private void ComputeMagnitudesSimd(double[] real, double[] imag, float[] magnitudes, int halfN, int n)
    {
        double scale = 2.0 / n;
        int vectorSize = Vector<double>.Count;
        int i = 1;
        
        // SIMD 向量化处理
        if (Vector.IsHardwareAccelerated && halfN >= vectorSize + 1)
        {
            var scaleVec = new Vector<double>(scale);
            for (; i <= halfN - vectorSize; i += vectorSize)
            {
                var re = new Vector<double>(real, i);
                var im = new Vector<double>(imag, i);
                var magSquared = re * re + im * im;
                
                // 逐元素计算平方根并转换为 float
                for (int k = 0; k < vectorSize && i + k < halfN; k++)
                {
                    magnitudes[i + k] = (float)(Math.Sqrt(magSquared[k]) * scale);
                }
            }
        }
        
        // 处理剩余元素
        for (; i < halfN; i++)
        {
            magnitudes[i] = (float)(Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]) * scale);
        }
    }

    // 音频可视化绘制：简洁专业的设计
    private void DrawAudioVisualizer(IImageProcessingContext ctx, RectangleF region, AudioBuffer audioBuffer)
    {
        if (audioBuffer == null || audioBuffer.TotalSampleCount == 0) return;

        int channelCount = audioBuffer.ChannelCount;
        int sampleCount = audioBuffer.SampleCount;
        if (channelCount <= 0 || sampleCount <= 0) return;

        // 提取单声道音频数据
        float[] interleaved = audioBuffer.ToArray();
        float[] mono = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            float sum = 0f;
            for (int ch = 0; ch < channelCount; ch++)
            {
                int idx = i * channelCount + ch;
                if (idx < interleaved.Length) sum += interleaved[idx];
            }
            mono[i] = sum / channelCount;
        }

        // 布局：上 55% 波形，下 45% 频谱
        float waveH = region.Height * 0.55f;
        float specH = region.Height * 0.45f;
        float waveTop = region.Top;
        float specTop = waveTop + waveH;

        // 绘制波形
        DrawWaveform(ctx, new RectangleF(region.Left, waveTop, region.Width, waveH), mono);

        // 绘制频谱
        DrawSpectrum(ctx, new RectangleF(region.Left, specTop, region.Width, specH), mono);
    }

    // 波形（折线）
    private void DrawWaveform(IImageProcessingContext ctx, RectangleF region, float[] samples)
    {
        if (samples.Length < 2) return;

        // 显示窗口
        int windowSize = (int)(AudioOutputSampleRate * WaveformLengthMs / 1000f);
        windowSize = Math.Clamp(windowSize, 64, samples.Length);
        int startIdx = samples.Length - windowSize;

        // 归一化
        float maxAbs = 0.001f;
        for (int i = 0; i < windowSize; i++)
        {
            float abs = Math.Abs(samples[startIdx + i]);
            if (abs > maxAbs) maxAbs = abs;
        }

        float centerY = region.Top + region.Height / 2f;
        float amplitude = region.Height * 0.45f;
        float lineWidth = Math.Max(1.5f, WaveformLineWidth * ResolutionScale);

        // 下采样点数（折线顶点数）
        int pointCount = Math.Min(512, windowSize);
        var points = new PointF[pointCount];

        for (int i = 0; i < pointCount; i++)
        {
            // 每个点对应的样本索引
            int sampleIdx = startIdx + (i * windowSize / pointCount);
            float v = samples[sampleIdx] / maxAbs;

            float x = region.Left + (i / (float)(pointCount - 1)) * region.Width;
            float y = centerY - v * amplitude;
            points[i] = new PointF(x, y);
        }
        ctx.DrawLine(Color.White, lineWidth, points);
    }

    // 频谱绘制：使用历史缓冲实现大 FFT 窗口 + 对数频率映射 + 时间平滑
    private void DrawSpectrum(IImageProcessingContext ctx, RectangleF region, float[] samples)
    {
        // 使用可配置的 FFT 大小（确保是 2 的幂次方）
        int fftSize = GetValidFftSize();
        
        // 初始化或重置历史缓冲
        if (_audioHistoryBuffer == null || _audioHistoryBuffer.Length != fftSize)
        {
            _audioHistoryBuffer = new float[fftSize];
            _audioHistoryWritePos = 0;
        }
        
        // 将当前帧样本追加到历史缓冲（循环写入）
        for (int i = 0; i < samples.Length; i++)
        {
            _audioHistoryBuffer[_audioHistoryWritePos] = samples[i];
            _audioHistoryWritePos = (_audioHistoryWritePos + 1) % fftSize;
        }

        int n = fftSize;
        int halfN = n / 2;

        // 复用 FFT 缓冲区
        if (_fftReal == null || _fftReal.Length != n)
        {
            _fftReal = new double[n];
            _fftImag = new double[n];
            _fftMagnitudes = new float[halfN];
        }

        // 预计算 Hann 窗口
        PrecomputeHannWindow(n);

        // 准备 FFT 输入：从历史缓冲提取 + 预计算的 Hann 窗
        for (int i = 0; i < n; i++)
        {
            int idx = (_audioHistoryWritePos + i) % n;
            _fftReal[i] = _audioHistoryBuffer[idx] * _hannWindow[i];
            _fftImag[i] = 0;
        }

        // 执行 SIMD 加速的快速 FFT
        ComputeFFT(_fftReal, _fftImag, n);

        // 使用 SIMD 加速计算幅度谱
        ComputeMagnitudesSimd(_fftReal, _fftImag, _fftMagnitudes, halfN, n);

        // 频率范围：30Hz ~ 18kHz
        float freqPerBin = (float)AudioOutputSampleRate / n;
        float minFreq = 30f;
        float maxFreq = Math.Min(18000f, AudioOutputSampleRate / 2f * 0.9f);

        // 频谱柱
        int barCount = Math.Clamp(SpectrumBarCount, 16, 128);
        float[] bars = new float[barCount];

        // 对数频率映射：每个柱对应不同的频率范围
        float logMin = (float)Math.Log10(minFreq);
        float logMax = (float)Math.Log10(maxFreq);
        float logRange = logMax - logMin;

        for (int i = 0; i < barCount; i++)
        {
            float t0 = i / (float)barCount;
            float t1 = (i + 1) / (float)barCount;
            float freqLo = (float)Math.Pow(10, logMin + t0 * logRange);
            float freqHi = (float)Math.Pow(10, logMin + t1 * logRange);

            // 转换为 FFT bin 索引（使用浮点数以支持插值）
            float binLo = freqLo / freqPerBin;
            float binHi = freqHi / freqPerBin;
            int bin0 = Math.Max(1, (int)binLo);
            int bin1 = Math.Min(halfN - 1, (int)Math.Ceiling(binHi));
            if (bin1 < bin0) bin1 = bin0;

            // 取该范围内的最大幅度
            float maxMag = 0f;
            for (int k = bin0; k <= bin1; k++)
            {
                if (_fftMagnitudes[k] > maxMag) maxMag = _fftMagnitudes[k];
            }

            // 对数功率刻度（dB）：-60dB ~ 0dB 映射到 0 ~ 1
            float db = 20f * (float)Math.Log10(maxMag + 1e-10f);
            bars[i] = Math.Clamp((db + 60f) / 60f, 0f, 1f);
        }

        // 时间平滑（快攻慢放）
        if (_smoothedSpectrum == null || _smoothedSpectrum.Length != barCount)
        {
            _smoothedSpectrum = new float[barCount];
            Array.Copy(bars, _smoothedSpectrum, barCount);
        }

        float release = Math.Clamp(SpectrumSmoothing, 0.3f, 0.95f);
        for (int i = 0; i < barCount; i++)
        {
            if (bars[i] > _smoothedSpectrum[i])
            {
                _smoothedSpectrum[i] = _smoothedSpectrum[i] * 0.2f + bars[i] * 0.8f;
            }
            else
            {
                _smoothedSpectrum[i] = _smoothedSpectrum[i] * release + bars[i] * (1f - release);
            }
        }

        // 绘制
        float barWidth = region.Width / barCount;
        float gap = barWidth * 0.15f;
        float actualWidth = barWidth - gap;

        for (int i = 0; i < barCount; i++)
        {
            float x = region.Left + i * barWidth + gap / 2f;
            float h = Math.Max(2f, _smoothedSpectrum[i] * region.Height);
            float y = region.Top + region.Height - h;
            ctx.Fill(Color.White, new RectangleF(x, y, actualWidth, h));
        }
    }

    private string BuildSubfileDisplayLine(SubFile subfile)
    {
        // 构造单行显示文本：Disc.Track- Title // Artist [Genre]
        // 按需省略符号，保持在一行内，最终再由外层做截断
        if (subfile == null)
        {
            return string.Empty;
        }

        string line = string.Empty;

        // 碟号 / 曲号前缀
        string prefix = string.Empty;
        if (subfile.DiscNumber.HasValue)
        {
            prefix = subfile.DiscNumber.Value.ToString();
        }
        if (subfile.TrackNumber.HasValue)
        {
            if (!string.IsNullOrEmpty(prefix))
            {
                prefix += ".";
            }
            prefix += subfile.TrackNumber.Value.ToString();
        }
        if (!string.IsNullOrEmpty(prefix))
        {
            line += prefix + "- ";
        }

        // 曲名：优先 TrackTitle，没有则退回文件名
        string title = !string.IsNullOrWhiteSpace(subfile.TrackTitle)
            ? subfile.TrackTitle
            : subfile.FileName;
        line += title;

        // 艺术家：存在时添加 " // Artist"
        if (!string.IsNullOrWhiteSpace(subfile.ArtistName))
        {
            line += " // " + subfile.ArtistName;
        }

        // 风格：存在时添加 " [Genre]"
        if (!string.IsNullOrWhiteSpace(subfile.Genre))
        {
            line += " [" + subfile.Genre + "]";
        }

        return line;
    }
    #endregion
}