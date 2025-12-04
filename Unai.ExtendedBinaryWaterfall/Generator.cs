using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using CSCore;
using CSCore.Codecs;
using CSCore.MediaFoundation;
using SkiaSharp;
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

    // 渲染相关字段
    private SKBitmap _frameContent = null;           // 主帧内容位图
    private SKCanvas _frameCanvas = null;            // 主帧画布（复用，避免每帧创建）
    private SKBitmap _viewportFramebuf = null;       // 瀑布视图位图
    private AudioBuffer _inputAudioBuffer = null;
    private AudioBuffer _outputAudioBuffer = null;
    // CSCore 音频解码相关字段：使用 CodecFactory 自动根据扩展名选择解码器
    private IWaveSource _audioWaveSource = null;
    private ISampleSource _audioSampleSource = null;
    // 用于暂存每次渲染的 float PCM 数据
    private float[] _audioSampleBuffer = null;
    private int _videoFrameX1, _videoFrameX2, _videoFrameY1, _videoFrameY2;
    internal bool _exitRequested = false;
    
    // 歌曲切换动画状态
    private int _lastSubfileIndex = -1;
    
    // 音频可视化复用数组（避免每帧分配）
    private float[] _audioMonoBuffer = null;
    private float[] _audioInterleavedBuffer = null;
    
    // 视频帧缓冲区复用（避免每帧分配）
    private byte[] _reusableVideoBuffer = null;
    
    // 瀑布原始图像复用
    private SKBitmap _reusableWaterfallImage = null;
    // 缩放后的瀑布图像复用（避免每帧 Clone+Resize 导致的内存分配）
    private SKBitmap _reusableScaledWaterfall = null;
    // 复用的瀑布缩放 Canvas（避免每帧创建）
    private SKCanvas _reusableScaledWaterfallCanvas = null;
    
    // 预渲染的渐变着色器
    private SKShader _cachedTopGradientShader = null;
    private SKShader _cachedBottomGradientShader = null;
    private float _cachedGradientY1 = -1;
    private float _cachedGradientY2 = -1;
    private float _cachedGradientHeight = -1;
    
    // 文本测量缓存
    private Dictionary<(string text, float fontSize), float> _textWidthCache = new();
    private const int MaxTextCacheSize = 2048;  // 增大缓存容量
    
    // 预渲染的静态 UI 元素
    // 封面图片缓存
    private SKBitmap _cachedScaledCover = null;
    private int _cachedCoverSubfileIndex = -1;
    private int _cachedCoverSize = 0;
    
    // 静态 UI 图层缓存（包含不变的标签文本）
    private SKBitmap _staticUILayer = null;
    private bool _staticUILayerValid = false;
    
    // 子文件列表预渲染缓存
    private SKBitmap _subfileListCache = null;
    private float _cachedSubfileWindowIndex = -999f;
    private int _cachedCurrentSubfileKey = -1;
    private int _cachedFirstSubfileIndex = -1;
    private int _cachedLastSubfileIndex = -1;
    
    // 专辑标题和歌曲信息缓存（歌曲切换时才更新）
    private SKBitmap _albumHeaderCache = null;
    private SKBitmap _trackInfoCache = null;
    private int _cachedTrackInfoSubfileIndex = -1;
    
    // 切换动画状态（基于时间）
    private string _prevDisplayInfo = "";         // 前一首显示信息
    private string _prevTimeString = "";          // 前一首时间字符串
    private string _currentDisplayInfo = "";      // 当前显示信息
    private string _currentTimeString = "";       // 当前时间字符串
    private float[] _prevWaveformPeaks = null;    // 前一首波形数据
    private float[] _currentWaveformPeaks = null; // 当前波形数据
    private SKBitmap _prevScaledCover = null;     // 前一首缩放后的封面
    private byte[] _prevCoverHash = null;         // 前一首封面哈希（用于判断是否相同）
    private double _animationStartTime = -1;      // 动画开始时间（秒）
    private double _currentVideoTime = 0;         // 当前视频时间（秒）
    private const double AnimationDurationSeconds = 0.5; // 动画持续时间（秒）
    
    // 灰色标签位置缓存（用于切换动画）
    private float _prevComposerLabelX = 0;        // 前一首 Composer 标签 X 位置
    private float _prevGenreLabelX = 0;           // 前一首 Genre 标签 X 位置
    private bool _prevHasComposer = false;        // 前一首是否有 Composer
    private bool _prevHasGenre = false;           // 前一首是否有 Genre
    
    // 字符动画缓存
    private readonly List<(char c, float x, float width)> _oldCharPositions = new();
    private readonly List<(char c, float x, float width)> _newCharPositions = new();
    
    // 波形点缓存（用于重用 SKPoint 数组）
    private SKPoint[] _waveformPointCache = null;
    
    // 当前歌曲的音频峰值（用于波形和频谱归一化）
    private float _currentAudioPeak = 1.0f;
    
    // 波形触发状态（用于零交叉触发稳定）
    private int _lastTriggerOffset = 0;              // 上一帧的触发偏移位置
    
    // MIDI 钢琴卷帘可视化相关
    private VisualizerTransition _visualizerTransition = new();  // 可视化模式切换控制器
    private MidiVisualizer _currentMidiVisualizer = null;        // 当前 MIDI 可视化器
    private List<NoteVisualData> _visibleNotesBuffer = new();    // 可见音符缓冲（复用避免 GC）
    
    // 打击乐器显示相关
    private List<(int noteNumber, int velocity, double triggerTimeMs)> _activeDrumNotes = new();
    private Dictionary<int, double> _drumTriggerTimes = new();       // 每个音符的最后触发时间
    private Dictionary<int, int> _drumVelocities = new();            // 每个音符的最后力度
    private const double DrumTriggerWindowMs = 200;                  // 触发检测窗口
    private const double DrumAnimDurationMs = 280;                   // 打击动画时长（放慢）
    private const double DrumVelocityDecayMs = 400;                  // 力度条衰减时长（更缓和）
    private int[] _currentDrumNotes = null;                          // 当前 MIDI 使用的打击乐音符
    
    // MIDI 模式 UI 切换状态
    private bool _isMidiMode = false;                            // 当前是否为 MIDI 模式
    private bool _prevIsMidiMode = false;                        // 上一帧是否为 MIDI 模式
    private double _midiUITransitionStart = -1;                  // MIDI UI 切换动画开始时间
    private const double MidiUITransitionDuration = 0.4;         // UI 切换动画时长（秒）
    private int _currentPolyphony = 0;                           // 当前复音数（实时）
    private int _currentPlayingNotes = 0;                        // 当前已播放音符数
    
    // MIDI 钢琴窗口流速（可通过 GUI 配置）
    public int PianoRollWindowMs { get; set; } = 4000;
    
    // 可复用的 Paint 对象（避免每帧创建）
    private readonly SKPaint _fillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _strokePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private readonly SKPaint _textPaint = new() { IsAntialias = true, SubpixelText = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _imagePaint = new() { IsAntialias = false, FilterQuality = SKFilterQuality.None };
    // 渐变画笔（复用，避免每帧创建）
    private readonly SKPaint _gradientPaint = new() { IsAntialias = false };
    // 封面画笔（复用，避免每帧创建）
    private readonly SKPaint _coverPaint = new() { IsAntialias = false, FilterQuality = SKFilterQuality.Low };
    // 钢琴卷帘画笔（复用，避免每帧创建）
    private readonly SKPaint _pianoRollGridPaint = new() { IsAntialias = false, Style = SKPaintStyle.Stroke };
    private readonly SKPaint _pianoRollNotePaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    
    // SIMD alpha 向量缓存（避免每帧创建）
    private static readonly Vector<byte> _simdAlphaMask;
    private static readonly Vector<byte> _simdPosMask;
    
    // 静态构造函数初始化 SIMD 向量
    static Generator()
    {
        int vectorSize = Vector<byte>.Count;
        byte[] alphaPattern = new byte[vectorSize];
        byte[] posPattern = new byte[vectorSize];
        for (int i = 3; i < vectorSize; i += 4)
        {
            alphaPattern[i] = 255;
            posPattern[i] = 255;
        }
        _simdAlphaMask = new Vector<byte>(alphaPattern);
        _simdPosMask = new Vector<byte>(posPattern);
    }
    
    // Ease-out 缓动函数
    private static float EaseOutCubic(float t) => 1f - MathF.Pow(1f - t, 3f);
    
    // Ease-in-out 缓动函数
    private static float EaseInOutQuad(float t) => t < 0.5f ? 2f * t * t : 1f - MathF.Pow(-2f * t + 2f, 2f) / 2f;
    
    // 计算封面图像的简单哈希（用于比较是否相同）
    private static byte[] ComputeImageHash(SKBitmap image)
    {
        if (image == null) return null;
        // 简单取样哈希：取四角和中心的像素值
        int w = image.Width, h = image.Height;
        var hash = new byte[20];
        var positions = new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1), (w / 2, h / 2) };
        int idx = 0;
        foreach (var (x, y) in positions)
        {
            var pixel = image.GetPixel(x, y);
            hash[idx++] = pixel.Red;
            hash[idx++] = pixel.Green;
            hash[idx++] = pixel.Blue;
            hash[idx++] = pixel.Alpha;
        }
        return hash;
    }
    
    // 比较两个哈希是否相同
    private static bool HashEquals(byte[] a, byte[] b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
    
    // 测量文本宽度
    private float MeasureTextWidth(string text, float fontSize)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        
        // 使用缓存避免重复测量相同文本
        var key = (text, fontSize);
        if (_textWidthCache.TryGetValue(key, out float cachedWidth))
        {
            return cachedWidth;
        }
        
        // 缓存未命中，执行测量
        _textPaint.TextSize = fontSize;
        _textPaint.Typeface = _typeface;
        float width = _textPaint.MeasureText(text);
        
        // 缓存清理
        if (_textWidthCache.Count >= MaxTextCacheSize)
        {
            _textWidthCache.Clear();
        }
        
        _textWidthCache[key] = width;
        return width;
    }
    
    // 裁剪文本以适应指定宽度（使用省略号）
    private string ClampTextToWidth(string text, float fontSize, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0) return text;
        float textWidth = MeasureTextWidth(text, fontSize);
        if (textWidth <= maxWidth) return text;
        
        const string ellipsis = "…";
        float ellipsisWidth = MeasureTextWidth(ellipsis, fontSize);
        float availableWidth = maxWidth - ellipsisWidth;
        
        // 二分查找最大可容纳字符数
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (MeasureTextWidth(text[..mid], fontSize) <= availableWidth)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo > 0 ? text[..lo] + ellipsis : ellipsis;
    }
    
    // 计算并填充每个字符的位置信息
    private void PopulateCharacterPositions(string text, float fontSize, List<(char c, float x, float width)> positions)
    {
        positions.Clear();
        if (string.IsNullOrEmpty(text)) return;
        
        _textPaint.Typeface = _typeface;
        _textPaint.TextSize = fontSize;
        
        float currentX = 0f;
        foreach (char c in text)
        {
            string charStr = c.ToString();
            float charWidth = _textPaint.MeasureText(charStr);
            positions.Add((c, currentX, charWidth));
            currentX += charWidth;
        }
    }
    
    // 确保帧画布已创建
    private void EnsureFrameCanvas()
    {
        if (_frameContent == null) return;
        _frameCanvas ??= new SKCanvas(_frameContent);
    }
    
    // 通用文本绘制辅助方法，支持对齐和字体大小
    private void DrawText(float x, float y, float fontSize, string text, SKColor color,
        VerticalAlign vAlign = VerticalAlign.Top, HorizontalAlign hAlign = HorizontalAlign.Left, SKTypeface typeface = null)
    {
        if (string.IsNullOrEmpty(text) || _frameCanvas == null) return;
        typeface ??= _typeface;
        _frameCanvas.DrawTextAndCache(typeface, fontSize, text, x, y, color, hAlign, vAlign);
    }
    
    // 绘制多行文本（自动换行并居中，用于 Intro 文案）
    private void DrawMultilineCentered(string text, float centerX, float centerY, float fontSize, SKColor color, float lineSpacingMultiplier = 1.25f)
    {
        if (string.IsNullOrEmpty(text) || _frameCanvas == null) return;
        // 统一换行符：Windows 的 \r\n 和 Mac 旧版的 \r 都转换为 \n
        string normalizedText = text.Replace("\r\n", "\n").Replace("\r", "\n");
        string[] lines = normalizedText.Split('\n');
        float lineSpacing = fontSize * lineSpacingMultiplier;
        float totalHeight = lineSpacing * (lines.Length - 1);
        for (int i = 0; i < lines.Length; i++)
        {
            float lineY = centerY - totalHeight / 2f + i * lineSpacing;
            DrawText(centerX, lineY, fontSize, lines[i], color, VerticalAlign.Center, HorizontalAlign.Center);
        }
    }
    
    // 预渲染渐变着色器（SKShader）
    private void EnsureGradientShadersCached(float shadowY1, float shadowY2, float gradientHeight)
    {
        // 检查是否需要重新生成缓存
        if (_cachedTopGradientShader != null && 
            Math.Abs(_cachedGradientY1 - shadowY1) < 0.1f &&
            Math.Abs(_cachedGradientY2 - shadowY2) < 0.1f &&
            Math.Abs(_cachedGradientHeight - gradientHeight) < 0.1f)
        {
            return; // 缓存有效，直接返回
        }
        
        // 释放旧缓存
        _cachedTopGradientShader?.Dispose();
        _cachedBottomGradientShader?.Dispose();
        
        // 背景色（深灰色）
        var bgColor = new SKColor(16, 16, 16);
        var bgColorTransparent = new SKColor(16, 16, 16, 0);
        
        // 创建顶部渐变着色器（从不透明到透明）
        _cachedTopGradientShader = SKShader.CreateLinearGradient(
            new SKPoint(0, shadowY1),
            new SKPoint(0, shadowY1 + gradientHeight),
            new[] { bgColor, bgColorTransparent },
            new[] { 0.5f, 1f },
            SKShaderTileMode.Clamp
        );
        
        // 创建底部渐变着色器（从透明到不透明）
        _cachedBottomGradientShader = SKShader.CreateLinearGradient(
            new SKPoint(0, shadowY2 - gradientHeight),
            new SKPoint(0, shadowY2),
            new[] { bgColorTransparent, bgColor },
            new[] { 0f, 0.5f },
            SKShaderTileMode.Clamp
        );
        
        // 更新缓存标记
        _cachedGradientY1 = shadowY1;
        _cachedGradientY2 = shadowY2;
        _cachedGradientHeight = gradientHeight;
    }

    // 公共方法：请求停止生成
    public void RequestStop() => _exitRequested = true;

    // 预渲染静态 UI 图层（仅包含底部播放器标签，顶部标签动态绘制以支持 MIDI 模式切换）
    private void EnsureStaticUILayerCached(string avSettingsString, string readSpeedString)
    {
        if (_staticUILayerValid && _staticUILayer != null) return;
        
        // 释放旧缓存
        _staticUILayer?.Dispose();
        
        // 创建透明图层（BGRA8888 格式）
        _staticUILayer = new SKBitmap(OutputVideoWidth, OutputVideoHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(_staticUILayer);
        canvas.Clear(SKColors.Transparent);
        
        // 颜色定义
        var dimGray = new SKColor(105, 105, 105);
        
        // 底部播放器固定标签（这些标签不随 MIDI 模式变化）
        float s = ResolutionScale;
        float bottomPanelHeight = 100f * s;
        float bottomY = OutputVideoHeight - bottomPanelHeight - 16f * s;
        float coverSize = 72f * s;
        float coverX = 32f * s;
        float infoX = coverX + coverSize + 16f * s;
        float timeX = OutputVideoWidth - 32f * s;
        float labelY = bottomY + 4f * s;
        
        canvas.DrawTextAndCache(_typeface, _fontSize16, "Title:", infoX, labelY, dimGray, HorizontalAlign.Left, VerticalAlign.Top);
        canvas.DrawTextAndCache(_typeface, _fontSize16, "Time:", timeX, labelY, dimGray, HorizontalAlign.Right, VerticalAlign.Top);
        
        _staticUILayerValid = true;
        Logger.Info("静态 UI 图层已预渲染");
    }
    
    // 绘制顶部 UI 标签（根据 MIDI/Audio 模式显示不同内容）
    private void DrawTopUILabels(SubFile currentSubfile, double currentTimeMs)
    {
        if (_frameCanvas == null) return;
        
        var dimGray = new SKColor(105, 105, 105);
        float labelY = 32;
        
        // 检测是否为 MIDI 模式
        bool isMidi = currentSubfile?.IsMidi == true && currentSubfile.MidiMetadata != null;
        _isMidiMode = isMidi;
        
        // 左上角：A/V SETTINGS 或 MIDI SETTINGS
        string leftLabel = isMidi ? "MIDI SETTINGS" : "A/V SETTINGS";
        DrawText(32, labelY, _fontSize16, leftLabel, dimGray, VerticalAlign.Top, HorizontalAlign.Left);
        
        // 中间偏右：BITRATE 或 POLYPHONY
        float midRightX = OutputVideoWidth - 280;
        string midLabel = isMidi ? "POLYPHONY" : "BITRATE";
        DrawText(midRightX, labelY, _fontSize16, midLabel, dimGray, VerticalAlign.Top, HorizontalAlign.Right);
        
        // 右上角：ABS. OFFSET 或 NOTE
        float rightX = OutputVideoWidth - 32;
        string rightLabel = isMidi ? "NOTE" : "ABS. OFFSET";
        DrawText(rightX, labelY, _fontSize16, rightLabel, dimGray, VerticalAlign.Top, HorizontalAlign.Right);
    }
    
    // 绘制顶部 UI 数值（根据 MIDI/Audio 模式显示不同内容）
    private void DrawTopUIValues(SubFile currentSubfile, long currentOffset, string avSettingsString, 
        string readSpeedString, double currentTimeMs)
    {
        if (_frameCanvas == null) return;
        
        float valueY = 32 + _fontSize16 + 8;
        bool isMidi = currentSubfile?.IsMidi == true && currentSubfile.MidiMetadata != null;
        var meta = currentSubfile?.MidiMetadata;
        
        // 左上角数值
        if (isMidi && meta != null)
        {
            // MIDI 模式：显示 TEMPO, TIME SIG, CHANNELS, TYPE + SoundFont
            double bpm = meta.GetBpmAtTime(currentTimeMs);
            var (num, den) = meta.GetTimeSignatureAtTime(currentTimeMs);
            string midiInfo = $"{bpm:F0} BPM, {num}/{den}, {meta.ChannelCount} CH, {meta.MidiType}\n" +
                              $"SoundFont: {currentSubfile.SoundFontName ?? "Default"}";
            DrawText(32, valueY, _fontSize24, midiInfo, SKColors.White, VerticalAlign.Top, HorizontalAlign.Left);
        }
        else
        {
            // Audio 模式
            string currentAvSettings = avSettingsString;
            if (currentSubfile != null && currentSubfile.AudioSampleRate > 0)
            {
                currentAvSettings = BuildAudioFormatString(
                    currentSubfile.AudioSampleRate,
                    currentSubfile.AudioChannels,
                    currentSubfile.AudioBitDepth);
            }
            DrawText(32, valueY, _fontSize24, currentAvSettings, SKColors.White, VerticalAlign.Top, HorizontalAlign.Left);
        }
        
        // 中间偏右数值
        float midRightX = OutputVideoWidth - 280;
        if (isMidi && meta != null && _currentMidiVisualizer != null)
        {
            // MIDI 模式：显示当前复音数/最大复音数
            _currentPolyphony = _currentMidiVisualizer.GetActiveNoteCount(currentTimeMs, _visibleNotesBuffer);
            string polyString = $"{_currentPolyphony} / {meta.MaxPolyphony}";
            DrawText(midRightX, valueY, _fontSize24, polyString, SKColors.White, VerticalAlign.Top, HorizontalAlign.Right);
        }
        else
        {
            // Audio 模式：显示比特率
            string currentBitrateString = readSpeedString;
            if (currentSubfile != null && currentSubfile.AudioBitrate > 0)
            {
                currentBitrateString = $"{currentSubfile.AudioBitrate / 1000} kbps";
            }
            DrawText(midRightX, valueY, _fontSize24, currentBitrateString, SKColors.White, VerticalAlign.Top, HorizontalAlign.Right);
        }
        
        // 右上角数值
        float rightX = OutputVideoWidth - 32;
        if (isMidi && meta != null && _currentMidiVisualizer != null)
        {
            // MIDI 模式：显示已播放音符数/总音符数
            _currentPlayingNotes = _currentMidiVisualizer.GetPlayedNoteCount(currentTimeMs);
            string noteString = $"{_currentPlayingNotes} / {meta.TotalNoteCount}";
            DrawText(rightX, valueY, _fontSize24, noteString, SKColors.White, VerticalAlign.Top, HorizontalAlign.Right);
        }
        else
        {
            // Audio 模式：显示偏移
            long displayOffset = currentOffset;
            if (currentSubfile != null)
            {
                displayOffset = currentOffset - currentSubfile.StartOffset;
                if (displayOffset < 0) displayOffset = 0;
            }
            DrawText(rightX, valueY, _fontSize24,
                $"{displayOffset / 1048576f:N2} MiB\n0x{displayOffset:X8}", SKColors.White,
                VerticalAlign.Top, HorizontalAlign.Right);
        }
    }
    
    // 获取当前帧的 BGRA 字节数组（用于 WPF 显示）
    public byte[] GetCurrentFrameAsBgra()
    {
        if (_frameContent == null) return null;
        
        try
        {
            int width = _frameContent.Width;
            int height = _frameContent.Height;
            
            // 使用 GetPixelSpan 高效获取像素数据（SkiaSharp 使用 BGRA 格式）
            ReadOnlySpan<byte> srcSpan = _frameContent.GetPixelSpan();
            
            byte[] data = new byte[width * height * 4];
            srcSpan.CopyTo(data);
            
            return data;
        }
        catch
        {
            return null;
        }
    }

    public int CurrentFrameWidth => _frameContent?.Width ?? 0;
    public int CurrentFrameHeight => _frameContent?.Height ?? 0;

    // 预览模式初始化（不需要导出器，以后要改）
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

    #endregion

    #region SkiaSharp-specific

    // 字体相关
    private SKTypeface _typeface;
    private SKTypeface _emojiTypeface;
    
    // 不同大小的字体尺寸（根据分辨率缩放）
    private float _fontSize16, _fontSize24, _fontSize32, _fontSize48;
    
    // 抗锯齿设置
    private bool _fontAntialiasing = true;

    #endregion

    #region General Parameters

    public string InputFilePath { get; set; } = null;
    // 多文件队列
    public List<string> InputFilePaths { get; set; } = null;
    // 当前播放的音频文件索引（用于 UI 显示）
    public int CurrentAudioIndex { get; private set; } = 0;
    public string InputAuxiliaryFilePath { get; set; } = null;
    public string OutputFilePath { get; set; } = null;
    public string Title { get; set; } = null;
    public string Author { get; set; } = null;
    public string InputFileFormatId { get; set; } = null;
    public string ExporterId { get; set; } = null;
    // 硬件加速类型（仅适用于 FFmpeg 导出器）
    public HardwareAccelType HardwareAccel { get; set; } = HardwareAccelType.Auto;
    // 编码质量预设
    public EncodingQualityPreset EncodingPreset { get; set; } = EncodingQualityPreset.Speed;
    // NVENC 编码配置
    public string NvencPreset { get; set; } = "p1";             // P1 最快速度
    public string NvencTune { get; set; } = "ll";               // ll=低延迟模式
    public string NvencRateControl { get; set; } = "vbr";       // vbr=可变比特率
    public int NvencBFrames { get; set; } = 0;                  // B帧=0
    public bool NvencTemporalAQ { get; set; } = false;          // 关闭时域AQ
    public bool NvencSpatialAQ { get; set; } = false;           // 关闭空域AQ
    public int NvencAQStrength { get; set; } = 0;               // AQ强度=0
    public int NvencLookahead { get; set; } = 0;                // Lookahead=0
    public bool NvencZeroLatency { get; set; } = true;          // 零延迟模式
    // 视频编码参数
    public uint VideoBitrate { get; set; } = 20_000_000;        // 默认 20 Mbps
    public int VideoCodecIndex { get; set; } = 0;               // 0=H.264, 1=H.265, 2=AV1, 3=VP9
    public int RateControlMode { get; set; } = 0;               // 0=VBR, 1=CRF, 2=CBR
    public int CrfValue { get; set; } = 23;                     // CRF值 0-51
    public int VideoProfile { get; set; } = 0;                  // 0=auto, 1=baseline, 2=main, 3=high
    public int VideoLevel { get; set; } = 0;                    // 0=auto
    public int KeyframeInterval { get; set; } = 0;              // GOP大小，0=自动
    // 音频编码参数
    public uint AudioBitrate { get; set; } = 256_000;           // 默认 256 kbps
    public int AudioCodecIndex { get; set; } = 0;               // 0=AAC
    // 输出格式
    public string OutputFormat { get; set; } = "matroska";      // matroska, mp4, webm, mov, avi
    
    // 瀑布视窗时间跨度（毫秒），控制瀑布滚动速度
    // 默认值 2730ms 对应原始 InputBytesPerSecond = 96000
    [CliParameter("Waterfall window duration in milliseconds", "waterfall-window-ms")]
    public int WaterfallWindowMs { get; set; } = 2730;
    
    // 根据瀑布视窗时间计算每秒字节数（瀑布滚动速度）
    // 公式：InputBytesPerSecond = WaterfallFrameLength * 1000 / WaterfallWindowMs
    public int InputBytesPerSecond => WaterfallFrameLength * 1000 / Math.Max(1, WaterfallWindowMs);
    
    public string FontName { get; set; } = null;
    [CliParameter("Font Antialiasing", "font-antialiasing")]
    public bool FontAntialiasing { get => _fontAntialiasing; set => _fontAntialiasing = value; }

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

    [CliParameter("Enable waveform trigger (zero-crossing) for stable display", "waveform-trigger")]
    public bool WaveformTriggerEnabled { get; set; } = true; // 启用波形触发

    [CliParameter("Spectrum bar count", "spectrum-bars")]
    public int SpectrumBarCount { get; set; } = 64; // 频谱柱数量，范围 8~1024

    [CliParameter("Spectrum attack time in milliseconds", "spectrum-attack-ms")]
    public float SpectrumAttackMs { get; set; } = 10f; // 频谱上升时间（毫秒）

    [CliParameter("Spectrum slope in dB/octave (0=flat, 3=pink noise flat, 4.5=SPAN default)", "spectrum-slope")]
    public float SpectrumSlope { get; set; } = 4.5f; // 频谱斜率（dB/octave），默认 4.5

    [CliParameter("Spectrum release time in milliseconds", "spectrum-release-ms")]
    public float SpectrumReleaseMs { get; set; } = 10f; // 频谱下降时间（毫秒）

    [CliParameter("FFT size for spectrum analysis (power of 2)", "fft-size")]
    public int FftSize { get; set; } = 4096; // FFT 大小，范围 512~8192

    [CliParameter("Intro fade duration in seconds", "intro-fade-duration")]
    public float IntroFadeDuration { get; set; } = 1.0f; // 开头免责声明的淡入淡出时长（秒）

    [CliParameter("Outro fade duration in seconds", "outro-fade-duration")]
    public float OutroFadeDuration { get; set; } = 2.0f; // 内容结束后的淡出时长（秒）

    [CliParameter("Intro text content", "intro-text")]
    public string IntroText { get; set; } = "声明\n\n本视频使用\nextended-binary-waterfall\n项目进行生成\n\n"; // 入场显示的文字内容

    [CliParameter("Intro duration in seconds", "intro-duration")]
    public float IntroDuration { get; set; } = 5.0f; // 开场持续时间（秒）

    [CliParameter("Enable intro fade effect", "intro-fade-enabled")]
    public bool IntroFadeEnabled { get; set; } = true; // 是否启用开场淡入淡出效果

    #endregion

    #region Debug Flags

    public bool LogAllSubfiles { get; set; } = false;

    #endregion

    #region Initialization Methods

    public void Initialize()
    {
        // 验证输入文件路径
        if (string.IsNullOrEmpty(InputFilePath))
        {
            throw new ArgumentException("InputFilePath 不能为空");
        }
        
        if (InputFileStream == null)
        {
            Logger.Info("Opening files…");
            Logger.Debug($"Opening file '{InputFilePath}'…");
            
            if (!System.IO.File.Exists(InputFilePath))
            {
                throw new System.IO.FileNotFoundException($"找不到输入文件: {InputFilePath}");
            }
            
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

        // 初始化真实音频解码器
        InitializeAudioDecoder();

        // 配置画笔的抗锯齿设置
        _fillPaint.IsAntialias = _fontAntialiasing;
        _strokePaint.IsAntialias = _fontAntialiasing;
        _textPaint.IsAntialias = _fontAntialiasing;

        // JIT 预热
        WarmupRenderingPipeline();

        LogGeneratorStatus();
    }

    // JIT 预热
    private void WarmupRenderingPipeline()
    {
        Logger.Info("Warming up rendering pipeline...");
        var sw = Stopwatch.StartNew();
        
        using (var warmupBitmap = new SKBitmap(OutputVideoWidth, OutputVideoHeight, SKColorType.Bgra8888, SKAlphaType.Premul))
        using (var warmupCanvas = new SKCanvas(warmupBitmap))
        {
            warmupCanvas.Clear(SKColors.Black);
            _fillPaint.Color = SKColors.White;
            warmupCanvas.DrawRect(0, 0, 100, 100, _fillPaint);
            _textPaint.TextSize = _fontSize32;
            _textPaint.Typeface = _typeface;
            warmupCanvas.DrawText("Warmup 预热 0123456789", 0, 50, _textPaint);
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(100, 100),
                new[] { SKColors.Black, SKColors.White },
                SKShaderTileMode.Clamp);
            _gradientPaint.Shader = shader;
            warmupCanvas.DrawRect(0, 0, 100, 100, _gradientPaint);
            _gradientPaint.Shader = null;
        }
        
        if (_reusableVideoBuffer == null || _reusableVideoBuffer.Length < WaterfallFrameLength)
            _reusableVideoBuffer = new byte[WaterfallFrameLength];
        for (int i = 0; i < Math.Min(1000, _reusableVideoBuffer.Length); i++)
            _reusableVideoBuffer[i] = (byte)(i & 0xFF);
        
        if (_smoothedSpectrum == null || _smoothedSpectrum.Length != SpectrumBarCount)
            _smoothedSpectrum = new float[SpectrumBarCount];
        for (int i = 0; i < SpectrumBarCount; i++)
            _smoothedSpectrum[i] = (float)Math.Sin(i * 0.1) * 0.5f + 0.5f;
        
        MeasureTextWidth("Test 测试 0123456789 // Artist [Genre]", _fontSize32);
        MeasureTextWidth("00:00 / 00:00", _fontSize24);
        
        // 预热常用文本的 SKTextBlob 缓存（使用临时 canvas 触发缓存创建）
        using (var textWarmupBitmap = new SKBitmap(800, 200, SKColorType.Bgra8888, SKAlphaType.Premul))
        using (var textWarmupCanvas = new SKCanvas(textWarmupBitmap))
        {
            // 预热固定标签（这些文本每帧都会用到）
            string[] commonLabels = { "Title:", "Time:", "A/V SETTINGS", "ABS. OFFSET", "BITRATE", "Composer:", "Genre:", "▶", "♪" };
            foreach (var label in commonLabels)
            {
                textWarmupCanvas.DrawTextAndCache(_typeface, _fontSize16, label, 0, 50, SKColors.White);
                textWarmupCanvas.DrawTextAndCache(_typeface, _fontSize24, label, 0, 100, SKColors.White);
            }
            // 预热数字和常用字符
            textWarmupCanvas.DrawTextAndCache(_typeface, _fontSize32, "0123456789:/ MiBx", 0, 50, SKColors.White);
            textWarmupCanvas.DrawTextAndCache(_typeface, _fontSize24, "0123456789:/ MiBx%", 0, 100, SKColors.White);
            textWarmupCanvas.DrawTextAndCache(_typeface, _fontSize48, "0123456789", 0, 150, SKColors.White);
        }
        
        sw.Stop();
        Logger.Info($"Warmup completed in {sw.ElapsedMilliseconds}ms");
    }

    // 多文件音频源（当 InputFilePaths 有多个文件时使用）
    private MultiFileAudioSource _multiFileAudioSource;
    
    // 多文件二进制数据源（用于瀑布可视化）
    private MultiFileBinarySource _multiFileBinarySource;
    
    // 音频解码器的原始采样率（用于 FFmpeg 重采样）
    public int AudioDecoderSampleRate { get; private set; } = 48000;
    
    // 音频解码器的原始声道数（用于 FFmpeg 重采样）
    public int AudioDecoderChannelCount { get; private set; } = 2;
    
    // 使用 CSCore 初始化音频解码器，支持多文件队列
    private void InitializeAudioDecoder()
    {
        try
        {
            Logger.Info("Initializing audio decoder (CSCore)…");

            // 保存用户期望的输出设置（不要被解码器参数覆盖）
            int targetOutputSampleRate = AudioOutputSampleRate;
            int targetOutputChannelCount = AudioOutputChannelCount;
            
            // 如果有多文件队列，使用 MultiFileAudioSource
            if (InputFilePaths != null && InputFilePaths.Count > 1)
            {
                Logger.Info($"Using multi-file audio source with {InputFilePaths.Count} files");
                // 直接输出到用户指定的目标采样率和声道数，避免 FfmpegExporter 再次重采样
                _multiFileAudioSource = new MultiFileAudioSource(InputFilePaths, targetOutputSampleRate, targetOutputChannelCount);
                _audioSampleSource = _multiFileAudioSource;
                
                var wf = _multiFileAudioSource.WaveFormat;
                // 解码器输出的就是目标格式（已在 MultiFileAudioSource 中完成重采样）
                AudioDecoderSampleRate = wf.SampleRate;
                AudioDecoderChannelCount = wf.Channels;
                
                // 计算总时长（采样数 / (采样率 × 声道数)）
                double totalDuration = _multiFileAudioSource.Length / (double)(wf.SampleRate * wf.Channels);
                Logger.Info($"Total audio duration: {TimeSpan.FromSeconds(totalDuration)}");
                
                // 计算所有文件的总大小
                long totalFileSize = 0;
                foreach (var path in InputFilePaths)
                {
                    if (System.IO.File.Exists(path))
                        totalFileSize += new System.IO.FileInfo(path).Length;
                }
                
                // 根据总文件大小和总时长计算 WaterfallWindowMs（瀑布视窗时间）
                if (totalDuration > 0.1)
                {
                    int calculatedBytesPerSecond = (int)(totalFileSize / totalDuration);
                    // WaterfallWindowMs = WaterfallFrameLength * 1000 / InputBytesPerSecond
                    WaterfallWindowMs = WaterfallFrameLength * 1000 / Math.Max(1, calculatedBytesPerSecond);
                    Logger.Info($"Multi-file WaterfallWindowMs: {WaterfallWindowMs}ms (InputBytesPerSecond={InputBytesPerSecond})");
                }
                
                // 初始化多文件二进制源用于瀑布可视化
                _multiFileBinarySource = new MultiFileBinarySource(InputFilePaths, InputBytesPerSecond);
                Logger.Info($"Multi-file binary source initialized: total {_multiFileBinarySource.TotalLength} bytes");
            }
            else
            {
                // 单文件模式
                bool isMidi = MidiAudioSourceFactory.IsMidiFile(InputFilePath);
                
                if (isMidi)
                {
                    // MIDI 文件使用 FluidSynth 渲染
                    Logger.Info($"[MIDI] 使用 FluidSynth 渲染: {Path.GetFileName(InputFilePath)}");
                    _audioSampleSource = MidiAudioSourceFactory.Create(InputFilePath, targetOutputSampleRate, targetOutputChannelCount);
                    var wf = _audioSampleSource.WaveFormat;
                    AudioDecoderSampleRate = wf.SampleRate;
                    AudioDecoderChannelCount = wf.Channels;
                    
                    // MIDI 文件：使用渲染后的音频长度计算时长
                    long audioLength = _audioSampleSource.Length;
                    double durationSeconds = audioLength / (double)(wf.SampleRate * wf.Channels);
                    Logger.Info($"[MIDI] 渲染完成: {durationSeconds:F2}s, {audioLength} samples");
                }
                else
                {
                    // 流式音频文件
                    _audioWaveSource = CodecFactory.Instance.GetCodec(InputFilePath);
                    var wf = _audioWaveSource.WaveFormat;

                    // 保存解码器原始参数（用于 FFmpeg 重采样）
                    AudioDecoderSampleRate = wf.SampleRate;
                    AudioDecoderChannelCount = wf.Channels;

                    // 根据音频时长计算 WaterfallWindowMs（瀑布视窗时间）
                    long decodedBytes = _audioWaveSource.Length;
                    int bytesPerSecond = wf.BytesPerSecond;
                    if (decodedBytes > 0 && bytesPerSecond > 0 && InputFileStream != null && InputFileStream.Length > 0)
                    {
                        double durationSeconds = decodedBytes / (double)bytesPerSecond;
                        if (durationSeconds > 0.1)
                        {
                            int calculatedBytesPerSecond = (int)(InputFileStream.Length / durationSeconds);
                            WaterfallWindowMs = WaterfallFrameLength * 1000 / Math.Max(1, calculatedBytesPerSecond);
                            Logger.Info($"Single-file WaterfallWindowMs: {WaterfallWindowMs}ms (InputBytesPerSecond={InputBytesPerSecond})");
                        }
                    }

                    _audioSampleSource = SafeToSampleSource(_audioWaveSource, InputFilePath);
                }
            }
            
            // FFmpeg 导出器会将音频重采样
            AudioOutputSampleRate = targetOutputSampleRate;
            AudioOutputChannelCount = targetOutputChannelCount;
            Logger.Info($"Audio: decoder={AudioDecoderSampleRate}Hz {AudioDecoderChannelCount}ch -> output={AudioOutputSampleRate}Hz {AudioOutputChannelCount}ch");

            // 准备音频缓冲区
            int decoderSamplesPerChannel = AudioDecoderSampleRate / OutputFps;
            _outputAudioBuffer = new(decoderSamplesPerChannel, AudioDecoderChannelCount);
            _audioSampleBuffer = new float[decoderSamplesPerChannel * AudioDecoderChannelCount];
            _inputAudioBuffer = new(decoderSamplesPerChannel, AudioDecoderChannelCount);
            
            Logger.Info($"Audio decoder initialized: {AudioDecoderSampleRate}Hz, {AudioDecoderChannelCount}ch, {decoderSamplesPerChannel} samples/ch/frame, OutputFps={OutputFps}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to initialize audio decoder: {ex.Message}");
            _audioSampleSource = null;
            _audioWaveSource?.Dispose();
            _audioWaveSource = null;
            _multiFileAudioSource?.Dispose();
            _multiFileAudioSource = null;

            _inputAudioBuffer = new(AudioInputSamplesPerFramePerChannel, AudioInputChannelCount);
            _outputAudioBuffer = new(AudioOutputSamplesPerFramePerChannel, AudioOutputChannelCount);
        }
    }

    // 安全地将 IWaveSource 转换为 ISampleSource，支持更多音频格式
    private ISampleSource SafeToSampleSource(IWaveSource waveSource, string filePath)
    {
        try
        {
            // 首先尝试直接转换
            return waveSource.ToSampleSource();
        }
        catch (NotSupportedException ex) when (ex.Message.Contains("WaveformatTag"))
        {
            // 格式不支持时，尝试使用 MediaFoundationDecoder 重新解码
            Logger.Warning($"音频格式不支持直接转换，尝试使用 MediaFoundation 解码: {filePath}");
            try
            {
                waveSource.Dispose();
                var mfDecoder = new MediaFoundationDecoder(filePath);
                return mfDecoder.ToSampleSource();
            }
            catch (Exception mfEx)
            {
                Logger.Error($"MediaFoundation 解码也失败: {mfEx.Message}");
                throw new NotSupportedException($"无法解码音频文件: {filePath}\n原因: {ex.Message}", ex);
            }
        }
    }

    [Conditional("DEBUG")]
    private void LogGeneratorStatus()
    {
        Logger.Debug($"Selected parser: {Parser?.GetType().GetCustomAttribute<ParserAttribute>()?.Name ?? "<null>"}");
        Logger.Debug($"Selected exporter: {Exporter?.GetType().GetCustomAttribute<ExporterAttribute>()?.Name ?? "<null>"}");
        Logger.Debug($"Selected font: {_typeface?.FamilyName ?? "<null>"}");
        Logger.Debug($"Read speed: {InputBytesPerFrame} bytes/frame ({InputBytesPerSecond} bytes/second)");
        Logger.Debug($"Waterfall duration will be around {TimeSpan.FromSeconds(InputFileStream.Length / InputBytesPerSecond)}.");
        Logger.Debug($"Video input:  {WaterfallWidth}×{WaterfallHeight}");
        Logger.Debug($"Audio input:  {AudioInputBytesPerFrame}bpf {AudioInputSamplesPerFrame}spf → {AudioInputSampleRate}Hz {AudioInputChannelCount}ch {8 * AudioInputSampleFormat.GetByteSize()}-bit");
        Logger.Debug($"Audio output: {AudioOutputBytesPerFrame}bpf {AudioOutputSamplesPerFrame}spf → {AudioOutputSampleRate}Hz {AudioOutputChannelCount}ch {8 * AudioOutputSampleFormat.GetByteSize()}-bit");
    }

    private void InitializeFonts()
    {
        Logger.Info("Loading fonts (SkiaSharp)…");
        Logger.Debug($"Requested font: '{FontName}'.");

        // 尝试按指定的字体名加载
        if (!string.IsNullOrEmpty(FontName))
        {
            _typeface = TryLoadFont(FontName);
            if (_typeface == null)
            {
                Logger.Error($"Cannot find font '{FontName}', using fallback.");
            }
        }

        // 如果指定的字体不可用，尝试备选字体
        if (_typeface == null)
        {
            // 按优先级尝试字体（包含大小写变体）
            string[] fallbackFonts = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? new[] { "Unifont", "unifont", "UNIFONT", "Unifont Upper", "Segoe UI Symbol", "Consolas", "Microsoft YaHei" }
                : new[] { "Unifont", "unifont", "Source Code Pro", "DejaVu Sans Mono", "Liberation Mono" };

            foreach (var fontName in fallbackFonts)
            {
                _typeface = TryLoadFont(fontName);
                if (_typeface != null)
                {
                    Logger.Debug($"Using fallback font: '{fontName}' → '{_typeface.FamilyName}'");
                    break;
                }
            }
        }
        
        // 尝试从常见路径加载 unifont TTF 文件
        if (_typeface == null)
        {
            _typeface = TryLoadUnifontFromFile();
        }

        // 最后回退到默认字体
        _typeface ??= SKTypeface.Default;
        Logger.Debug($"Selected font: '{_typeface?.FamilyName ?? "Default"}'");

        // 尝试加载 emoji 字体
        _emojiTypeface = SKTypeface.FromFamilyName("Segoe UI Emoji") 
            ?? SKTypeface.FromFamilyName("Noto Color Emoji")
            ?? _typeface;

        // 根据分辨率缩放字体大小
        float scale = ResolutionScale;
        _fontSize48 = 48f * scale;
        _fontSize32 = 32f * scale;
        _fontSize24 = 24f * scale;
        _fontSize16 = 16f * scale;

        // 配置文本画笔的抗锯齿
        _textPaint.IsAntialias = _fontAntialiasing;
        _textPaint.Typeface = _typeface;
    }
    
    // 尝试加载指定名称的字体
    private SKTypeface TryLoadFont(string fontName)
    {
        if (string.IsNullOrEmpty(fontName)) return null;
        
        // 使用 SKFontManager 进行精确匹配
        var fontManager = SKFontManager.Default;
        var typeface = fontManager.MatchFamily(fontName);
        
        // 检查是否真的找到了请求的字体（而不是回退字体）
        if (typeface != null && 
            typeface.FamilyName.Equals(fontName, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Debug($"Font matched: '{fontName}' → '{typeface.FamilyName}'");
            return typeface;
        }
        
        // 如果 MatchFamily 返回了不同的字体，说明没有找到
        Logger.Trace($"Font not found: '{fontName}' (got '{typeface?.FamilyName ?? "null"}')");
        return null;
    }
    
    // 尝试从常见路径加载 unifont TTF 文件
    private SKTypeface TryLoadUnifontFromFile()
    {
        // Windows 和 Linux 的常见字体路径
        string[] possiblePaths = Environment.OSVersion.Platform == PlatformID.Win32NT
            ? new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "unifont.ttf"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "unifont-15.1.05.ttf"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts", "unifont.ttf"),
                @"C:\Windows\Fonts\unifont.ttf",
                @"C:\Windows\Fonts\unifont-15.1.05.ttf",
            }
            : new[]
            {
                "/usr/share/fonts/truetype/unifont/unifont.ttf",
                "/usr/share/fonts/unifont/unifont.ttf",
                "/usr/share/fonts/TTF/unifont.ttf",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fonts", "unifont.ttf"),
            };
        
        foreach (var path in possiblePaths)
        {
            if (System.IO.File.Exists(path))
            {
                try
                {
                    var typeface = SKTypeface.FromFile(path);
                    if (typeface != null)
                    {
                        Logger.Info($"Loaded unifont from file: '{path}'");
                        return typeface;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Trace($"Failed to load font from '{path}': {ex.Message}");
                }
            }
        }
        
        Logger.Warning("unifont not found in system fonts or common file paths. Please install unifont TTF.");
        return null;
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

        // 如果是 FFmpeg 导出器，应用所有编码设置
        if (Exporter is FfmpegExporter ffmpegExporter)
        {
            // 先应用编码质量预设（如果设置了）
            if (EncodingPreset != EncodingQualityPreset.Speed)
            {
                ffmpegExporter.ApplyQualityPreset(EncodingPreset);
            }
            
            // 硬件加速设置
            ffmpegExporter.HardwareAccel = HardwareAccel;
            
            // NVENC 配置（仅在未应用预设或需要覆盖时设置）
            ffmpegExporter.NvencPreset = NvencPreset;
            ffmpegExporter.NvencTune = NvencTune;
            ffmpegExporter.NvencRateControl = NvencRateControl;
            ffmpegExporter.NvencBFrames = NvencBFrames;
            ffmpegExporter.NvencTemporalAQ = NvencTemporalAQ;
            ffmpegExporter.NvencSpatialAQ = NvencSpatialAQ;
            ffmpegExporter.NvencAQStrength = NvencAQStrength;
            ffmpegExporter.NvencLookahead = NvencLookahead;
            ffmpegExporter.NvencZeroLatency = NvencZeroLatency;
            
            // 视频编码参数
            ffmpegExporter.OutputVideoBitRate = VideoBitrate;
            ffmpegExporter.VideoCodecIndex = VideoCodecIndex;
            ffmpegExporter.RateControlMode = RateControlMode;
            ffmpegExporter.CrfValue = CrfValue;
            ffmpegExporter.VideoProfile = VideoProfile;
            ffmpegExporter.VideoLevel = VideoLevel;
            ffmpegExporter.KeyframeInterval = KeyframeInterval;
            
            // 音频编码参数
            ffmpegExporter.OutputAudioBitRate = AudioBitrate;
            ffmpegExporter.AudioCodecIndex = AudioCodecIndex;
            
            // 输出容器格式
            ffmpegExporter.OutputFormat = OutputFormat;
            
            Logger.Debug($"FFmpeg: Codec={VideoCodecIndex}, HW={HardwareAccel}, Preset={NvencPreset}, RC={RateControlMode}, CRF={CrfValue}, Bitrate={VideoBitrate/1_000_000}Mbps, Lookahead={NvencLookahead}");
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

    // 解析子文件列表，支持多文件队列
    // 优化：同时计算波形 RMS，避免多次打开文件
    private void ParseSubfiles()
    {
        // 音频文件队列使用基于音频时长的虚拟偏移
        // 这对 MIDI 文件尤其重要，因为 MIDI 文件很小但音频时长可能很长
        if (InputFilePaths != null && InputFilePaths.Count >= 1)
        {
            Logger.Info($"Building subfiles from audio queue ({InputFilePaths.Count} files)…");
            
            _subfiles = new List<SubFile>();
            long currentOffset = 0;
            long actualByteOffset = 0;    // 实际文件字节偏移累加
            double currentAudioTime = 0;  // 累积音频时间（秒）
            
            // 波形计算参数
            const int rmsCount = 256;
            const int readBufferSize = 65536;
            
            foreach (var filePath in InputFilePaths)
            {
                if (!System.IO.File.Exists(filePath)) continue;
                
                // 获取音频时长和实际文件大小
                long fileLength = 0;
                long actualFileSize = 0;
                double durationSeconds = 0;
                // 音频格式信息（用于 A/V SETTINGS 显示）
                int audioSampleRate = 0;
                int audioChannels = 0;
                int audioBitDepth = 0;
                // 波形数据
                float[] waveformPeaks = null;
                float audioPeak = 1.0f;
                
                try
                {
                    actualFileSize = new System.IO.FileInfo(filePath).Length;
                    // MIDI 文件用 MidiMetadata 获取准确时长
                    bool isMidiFile = MidiAudioSourceFactory.IsMidiFile(filePath);
                    if (isMidiFile)
                    {
                        var tempMeta = MidiMetadata.FromMidiFile(filePath);
                        durationSeconds = tempMeta.TotalDurationMs / 1000.0;
                        if (durationSeconds < 0.1) durationSeconds = 60.0;
                        fileLength = (long)(durationSeconds * InputBytesPerSecond);
                        audioSampleRate = AudioOutputSampleRate;
                        audioChannels = AudioOutputChannelCount;
                        audioBitDepth = 32;
                        Logger.Debug($"[ParseSubfiles] MIDI {System.IO.Path.GetFileName(filePath)}: duration={durationSeconds:F2}s");
                    }
                    else
                    {
                        //非 MIDI操作
                        // 使用 FfmpegAudioDecoder 获取准确的音频时长和格式信息
                        // 同时计算波形 RMS
                        using var decoder = new FfmpegAudioDecoder(filePath);
                        var wf = decoder.WaveFormat;
                    
                    // 获取格式信息
                    long totalSamplesInFile = decoder.Length;
                    int channelCount = wf.Channels;
                    audioSampleRate = wf.SampleRate;
                    audioChannels = channelCount;
                    audioBitDepth = wf.BitsPerSample;
                    durationSeconds = (totalSamplesInFile / channelCount) / (double)audioSampleRate;
                    fileLength = (long)(durationSeconds * InputBytesPerSecond);
                    
                    // 计算波形 RMS
                    if (totalSamplesInFile > 0)
                    {
                        long samplesPerSegment = totalSamplesInFile / rmsCount;
                        if (samplesPerSegment < 1) samplesPerSegment = 1;
                        
                        double[] sumSquares = new double[rmsCount];
                        long[] sampleCounts = new long[rmsCount];
                        float[] buffer = new float[readBufferSize];
                        long currentPosition = 0;
                        float fileMaxPeak = 0f;
                        int totalRead;
                        
                        while ((totalRead = decoder.Read(buffer, 0, readBufferSize)) > 0)
                        {
                            for (int i = 0; i < totalRead; i++)
                            {
                                int segmentIndex = (int)((currentPosition + i) / samplesPerSegment);
                                if (segmentIndex >= rmsCount) segmentIndex = rmsCount - 1;
                                
                                float sample = buffer[i];
                                float absVal = Math.Abs(sample);
                                sumSquares[segmentIndex] += sample * sample;
                                sampleCounts[segmentIndex]++;
                                if (absVal > fileMaxPeak) fileMaxPeak = absVal;
                            }
                            currentPosition += totalRead;
                        }
                        
                        audioPeak = fileMaxPeak > 0.001f ? fileMaxPeak : 1.0f;
                        
                        // 计算 RMS 并归一化
                        waveformPeaks = new float[rmsCount];
                        float globalMaxRms = 0f;
                        for (int i = 0; i < rmsCount; i++)
                        {
                            if (sampleCounts[i] > 0)
                            {
                                float rms = (float)Math.Sqrt(sumSquares[i] / sampleCounts[i]);
                                waveformPeaks[i] = rms;
                                if (rms > globalMaxRms) globalMaxRms = rms;
                            }
                        }
                        if (globalMaxRms > 0.0001f)
                        {
                            for (int i = 0; i < rmsCount; i++)
                            {
                                waveformPeaks[i] /= globalMaxRms;
                            }
                        }
                    }
                    
                    Logger.Debug($"[ParseSubfiles] {System.IO.Path.GetFileName(filePath)}: duration={durationSeconds:F2}s, {audioSampleRate}Hz {audioChannels}ch, peak={audioPeak:F4}");
                } // 结束非 MIDI 音频处理块
                }
                catch (Exception ex)
                {
                    // 无法获取时长时使用文件大小估算
                    Logger.Warning($"[ParseSubfiles] 无法获取 {System.IO.Path.GetFileName(filePath)} 的音频信息: {ex.Message}");
                    actualFileSize = new System.IO.FileInfo(filePath).Length;
                    fileLength = actualFileSize;
                    durationSeconds = fileLength / (double)InputBytesPerSecond;
                }
                
                var sf = SubFile.FromWholeFile(filePath, fileLength);
                sf.StartOffset = currentOffset;
                sf.EndOffset = currentOffset + fileLength;
                // 设置音频时间信息（用于精确同步）
                sf.AudioStartTime = currentAudioTime;
                sf.AudioDuration = durationSeconds;
                // 设置实际文件字节偏移（用于瀑布可视化）
                sf.ActualByteOffset = actualByteOffset;
                sf.ActualByteLength = actualFileSize;
                // 设置音频格式信息（用于 A/V SETTINGS 显示源文件格式）
                sf.AudioSampleRate = audioSampleRate;
                sf.AudioChannels = audioChannels;
                sf.AudioBitDepth = audioBitDepth;
                // 设置波形数据（已在上面计算）
                sf.WaveformPeaks = waveformPeaks;
                sf.AudioPeak = audioPeak;
                
                // 解析 MIDI 元数据
                if (MidiAudioSourceFactory.IsMidiFile(filePath))
                {
                    sf.IsMidi = true;
                    sf.MidiMetadata = MidiMetadata.FromMidiFile(filePath);
                    // 获取 SoundFont 名称
                    string sfPath = MidiAudioSourceFactory.FindSoundFont();
                    sf.SoundFontName = !string.IsNullOrEmpty(sfPath) 
                        ? System.IO.Path.GetFileNameWithoutExtension(sfPath) 
                        : "Default";
                    Logger.Debug($"[MIDI] {System.IO.Path.GetFileName(filePath)}: " +
                        $"BPM={sf.MidiMetadata.InitialBpm:F0}, " +
                        $"TimeSign={sf.MidiMetadata.TimeSignatureString}, " +
                        $"Channels={sf.MidiMetadata.ChannelCount}, " +
                        $"Type={sf.MidiMetadata.MidiType}");
                }
                
                _subfiles.Add(sf);
                
                currentOffset += fileLength;
                actualByteOffset += actualFileSize;
                currentAudioTime += durationSeconds;
            }
            
            Logger.Debug($"Created {_subfiles.Count} subfiles from audio queue, total audio time: {currentAudioTime:F2}s, total actual bytes: {actualByteOffset}");
        }
        else
        {
            // 原有的单文件解析逻辑
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

            // 如果没有解析到任何子文件，则构造一个覆盖整个输入文件的虚拟子文件
            if (_subfiles.Count == 0 && InputFileStream != null && !string.IsNullOrEmpty(InputFilePath))
            {
                _subfiles =
                [
                    SubFile.FromWholeFile(InputFilePath, InputFileStream.Length)
                ];
                Logger.Debug("No subfiles parsed; created a virtual subfile covering the whole input file.");
            }
        }

        if (LogAllSubfiles)
        {
            foreach (var sf in _subfiles)
            {
                Logger.Debug($"\t{sf.IconString ?? "–"} '{sf.Path}' {sf.StartOffset:X8}–{sf.EndOffset:X8}");
            }
        }

        // 尝试为每个子文件填充音频元数据
        PopulateSubfileMetadata();
    }

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

                // 作曲家（Composer）- 优先用于显示
                if (string.IsNullOrWhiteSpace(sf.ComposerName))
                {
                    if (tag.Composers != null && tag.Composers.Length > 0)
                    {
                        sf.ComposerName = string.Join(", ", tag.Composers);
                    }
                }
                
                // 艺术家 Artist 作为备选
                if (string.IsNullOrWhiteSpace(sf.ArtistName))
                {
                    if (tag.Performers != null && tag.Performers.Length > 0)
                    {
                        sf.ArtistName = string.Join(", ", tag.Performers);
                    }
                }

                // 风格
                if (string.IsNullOrWhiteSpace(sf.Genre) && tag.Genres != null && tag.Genres.Length > 0)
                {
                    sf.Genre = string.Join(", ", tag.Genres);
                }
                
                // 音频比特率（从 Properties 读取，单位 kbps）
                if (sf.AudioBitrate == 0 && tagFile.Properties != null)
                {
                    sf.AudioBitrate = tagFile.Properties.AudioBitrate * 1000; // 转换为 bps
                }
                
                // 专辑封面：从 tag.Pictures 读取嵌入的封面图片
                if (sf.Icon == null && tag.Pictures != null && tag.Pictures.Length > 0)
                {
                    try
                    {
                        // 优先查找 FrontCover 类型的图片
                        TagLib.IPicture coverPic = null;
                        foreach (var pic in tag.Pictures)
                        {
                            if (pic.Type == TagLib.PictureType.FrontCover)
                            {
                                coverPic = pic;
                                break;
                            }
                        }
                        // 如果没有 FrontCover，使用第一张图片
                        coverPic ??= tag.Pictures[0];
                        
                        if (coverPic?.Data?.Data != null && coverPic.Data.Data.Length > 0)
                        {
                            using var stream = new MemoryStream(coverPic.Data.Data);
                            sf.Icon = SKBitmap.Decode(stream);
                            if (sf.Icon != null)
                            {
                                Logger.Debug($"Loaded album art for '{sf.FileName}' ({sf.Icon.Width}x{sf.Icon.Height})");
                            }
                        }
                    }
                    catch (Exception picEx)
                    {
                        Logger.Debug($"Failed to decode album art for '{sf.FileName}': {picEx.Message}");
                    }
                }
                
                // 如果元数据中没有封面，尝试从音频同目录查找封面图片
                if (sf.Icon == null)
                {
                    sf.Icon = TryLoadCoverFromDirectory(sf.FileDirectory, sf.FileName);
                }
            }
            catch (Exception ex)
            {
                // 某些子文件路径不是实际音频文件时可能会抛异常，这里只做调试输出
                Logger.Debug($"Failed to read metadata for subfile '{sf.Path}': {ex.Message}");
            }
            
            // 即使元数据读取失败，仍尝试从目录加载封面
            if (sf.Icon == null && !string.IsNullOrWhiteSpace(sf.FileDirectory))
            {
                sf.Icon = TryLoadCoverFromDirectory(sf.FileDirectory, sf.FileName);
            }
        }
    }
    
    // 常见封面文件名（按优先级排序）
    private static readonly string[] CoverFileNames = 
    {
        "cover", "folder", "front", "album", "artwork", "art", "scan", "booklet"
    };
    
    // 常见图片扩展名
    private static readonly string[] ImageExtensions = 
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"
    };
    
    // 尝试从指定目录加载封面图片
    private SKBitmap TryLoadCoverFromDirectory(string directory, string audioFileName)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;
        
        try
        {
            // 1. 优先查找常见封面文件名
            foreach (var baseName in CoverFileNames)
            {
                foreach (var ext in ImageExtensions)
                {
                    string coverPath = Path.Combine(directory, baseName + ext);
                    if (System.IO.File.Exists(coverPath))
                    {
                        var bitmap = LoadImageSafely(coverPath);
                        if (bitmap != null)
                        {
                            Logger.Debug($"Loaded cover from '{coverPath}' for '{audioFileName}'");
                            return bitmap;
                        }
                    }
                    
                    // 也尝试大写首字母版本
                    string coverPathCap = Path.Combine(directory, char.ToUpper(baseName[0]) + baseName[1..] + ext);
                    if (System.IO.File.Exists(coverPathCap))
                    {
                        var bitmap = LoadImageSafely(coverPathCap);
                        if (bitmap != null)
                        {
                            Logger.Debug($"Loaded cover from '{coverPathCap}' for '{audioFileName}'");
                            return bitmap;
                        }
                    }
                }
            }
            
            // 2. 查找与音频文件同名的图片
            if (!string.IsNullOrWhiteSpace(audioFileName))
            {
                string audioBaseName = Path.GetFileNameWithoutExtension(audioFileName);
                foreach (var ext in ImageExtensions)
                {
                    string sameName = Path.Combine(directory, audioBaseName + ext);
                    if (System.IO.File.Exists(sameName))
                    {
                        var bitmap = LoadImageSafely(sameName);
                        if (bitmap != null)
                        {
                            Logger.Debug($"Loaded cover from '{sameName}' (same name as audio)");
                            return bitmap;
                        }
                    }
                }
            }
            
            // 注：移除了"遍历目录所有图片"的兜底方案以提升性能
            // 如果常见文件名和同名图片都找不到，则不加载封面
        }
        catch (Exception ex)
        {
            Logger.Debug($"Failed to search cover in directory '{directory}': {ex.Message}");
        }
        
        return null;
    }
    
    // 安全加载图片（处理损坏或不支持的格式）
    private SKBitmap LoadImageSafely(string path)
    {
        try
        {
            using var stream = System.IO.File.OpenRead(path);
            var bitmap = SKBitmap.Decode(stream);
            if (bitmap != null && bitmap.Width > 0 && bitmap.Height > 0)
            {
                return bitmap;
            }
            bitmap?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Debug($"Failed to decode image '{path}': {ex.Message}");
        }
        return null;
    }

    // 预计算所有子文件的波形 RMS 值（用于底部进度条显示）
    private void PrecomputeSubfileWaveforms()
    {
        // 检查是否所有子文件都已有波形数据
        bool allHaveWaveforms = _subfiles.All(sf => sf.WaveformPeaks != null);
        if (allHaveWaveforms)
        {
            Logger.Info("Waveform data already computed in ParseSubfiles, skipping.");
            return;
        }
        
        Logger.Info("Precomputing waveform RMS for subfiles…");
        
        // 每个子文件生成固定数量的 RMS 值
        const int rmsCount = 256;
        // 流式读取缓冲区大小（64KB 采样数据）
        const int readBufferSize = 65536;
        
        foreach (var sf in _subfiles)
        {
            // 跳过已有波形数据的文件
            if (sf.WaveformPeaks != null) continue;
            if (sf == null || sf.Length <= 0) continue;
            if (string.IsNullOrWhiteSpace(sf.Path) || !System.IO.File.Exists(sf.Path)) continue;
            
            try
            {
                // 根据文件类型选择解码器：MIDI 使用 FluidSynth，其他使用 FFmpeg
                ISampleSource sampleSource;
                bool isMidi = MidiAudioSourceFactory.IsMidiFile(sf.Path);
                
                if (isMidi)
                {
                    // MIDI 文件使用 FluidSynth 渲染
                    sampleSource = MidiAudioSourceFactory.Create(sf.Path, 48000, 2);
                }
                else
                {
                    // 流式音频使用 FfmpegAudioDecoder
                    sampleSource = new FfmpegAudioDecoder(sf.Path);
                }
                
                using var _ = sampleSource;
                
                // 获取音频总采样数
                long totalSamples = sampleSource.Length;
                if (totalSamples <= 0) continue;
                
                int channelCount = sampleSource.WaveFormat.Channels;
                if (channelCount <= 0) channelCount = 2;
                
                // 计算每个 RMS 段对应的采样数
                long samplesPerSegment = totalSamples / rmsCount;
                if (samplesPerSegment < 1) samplesPerSegment = 1;
                
                // RMS 累加器：每个段的平方和与采样计数
                double[] sumSquares = new double[rmsCount];
                long[] sampleCounts = new long[rmsCount];
                
                // 流式读取整个音频文件
                float[] buffer = new float[readBufferSize];
                long currentPosition = 0;
                int totalRead;
                
                // 同时记录文件峰值振幅
                float fileMaxPeak = 0f;
                
                while ((totalRead = sampleSource.Read(buffer, 0, readBufferSize)) > 0)
                {
                    // 处理每个采样，累加到对应的 RMS 段
                    for (int i = 0; i < totalRead; i++)
                    {
                        // 计算当前采样属于哪个 RMS 段
                        int segmentIndex = (int)((currentPosition + i) / samplesPerSegment);
                        if (segmentIndex >= rmsCount) segmentIndex = rmsCount - 1;
                        
                        // 累加平方值并记录峰值
                        float sample = buffer[i];
                        float absVal = Math.Abs(sample);
                        sumSquares[segmentIndex] += sample * sample;
                        sampleCounts[segmentIndex]++;
                        if (absVal > fileMaxPeak) fileMaxPeak = absVal;
                    }
                    currentPosition += totalRead;
                }
                
                // 保存该歌曲的峰值（用于波形和频谱归一化）
                sf.AudioPeak = fileMaxPeak > 0.001f ? fileMaxPeak : 1.0f;
                
                // 计算每个段的 RMS 值
                float[] rmsValues = new float[rmsCount];
                float globalMaxRms = 0f;
                
                for (int i = 0; i < rmsCount; i++)
                {
                    if (sampleCounts[i] > 0)
                    {
                        float rms = (float)Math.Sqrt(sumSquares[i] / sampleCounts[i]);
                        rmsValues[i] = rms;
                        if (rms > globalMaxRms) globalMaxRms = rms;
                    }
                }
                
                // 峰值归一化：将所有值按全局最大 RMS 缩放到 0-1 范围
                if (globalMaxRms > 0.0001f)
                {
                    for (int i = 0; i < rmsCount; i++)
                    {
                        rmsValues[i] = rmsValues[i] / globalMaxRms;
                    }
                }
                
                sf.WaveformPeaks = rmsValues;
                Logger.Debug($"Waveform computed for '{sf.FileName}': {currentPosition} samples, peak = {sf.AudioPeak:F4}, max RMS = {globalMaxRms:F4}");
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
        }
    }
    
    // 总生成流程
    public void Generate()
    {
        // 配置运行时和线程池
        ConfigureHighPerformanceMode();
        
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
    
    // 配置模式：调整线程池、GC 和运行时设置
    private static void ConfigureHighPerformanceMode()
    {
        // 线程池线程数（完全利用多核 CPU）
        int processorCount = Environment.ProcessorCount;
        int minWorkerThreads = processorCount * 8;
        int minIOThreads = processorCount * 4;
        int maxWorkerThreads = processorCount * 16;
        int maxIOThreads = processorCount * 8;
        
        ThreadPool.SetMinThreads(minWorkerThreads, minIOThreads);
        ThreadPool.SetMaxThreads(maxWorkerThreads, maxIOThreads);
        
        // 配置 GC 为低延迟模式（减少 GC 暂停）
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        
        // 尝试使用服务器 GC（如果可用会更高效）
        // 注意：服务器 GC 需要在 csproj 中配置 <ServerGarbageCollection>true</ServerGarbageCollection>
        
        Logger.Info($"GC已启用: {processorCount} 核心, 线程池 min={minWorkerThreads}/{minIOThreads} max={maxWorkerThreads}/{maxIOThreads}, GC={GCSettings.LatencyMode}");
    }

    // 生成开头的免责声明画面（可配置淡入淡出效果）
    private void GenerateIntro()
    {
        Logger.Info("Generating introduction…");

        int totalFrames = Math.Max(1, (int)(IntroDuration * OutputFps));
        int fadeFrames = IntroFadeEnabled ? Math.Clamp((int)(IntroFadeDuration * OutputFps), 1, Math.Max(1, totalFrames / 3)) : 0;

        int introSamplesPerChannel = Math.Max(1, AudioDecoderSampleRate / OutputFps);
        var silentAudioBuffer = new AudioBuffer(introSamplesPerChannel, AudioDecoderChannelCount);
        silentAudioBuffer.Clear();

        EnsureFrameCanvas();
        if (_frameCanvas == null)
        {
            Logger.Error("Frame canvas is not initialized; cannot render intro.");
            return;
        }

        for (int frameNumber = 0; frameNumber < totalFrames; frameNumber++)
        {
            float opacity = 1f;
            if (IntroFadeEnabled && fadeFrames > 0)
            {
                if (frameNumber < fadeFrames)
                {
                    opacity = frameNumber / (float)fadeFrames;
                }
                else if (frameNumber >= totalFrames - fadeFrames)
                {
                    opacity = (totalFrames - frameNumber) / (float)fadeFrames;
                }
                opacity = Math.Clamp(opacity, 0f, 1f);
            }

            _frameCanvas.Clear(new SKColor(16, 16, 16));

            byte alpha = (byte)(255 * opacity);
            var textColor = new SKColor(255, 255, 255, alpha);
            string countdown = $"Starting in {(totalFrames - frameNumber) / (float)OutputFps:N1} seconds…";

            DrawMultilineCentered(IntroText ?? string.Empty, OutputVideoWidth / 2f, OutputVideoHeight / 2f, _fontSize48, textColor);
            DrawText(OutputVideoWidth / 2f, OutputVideoHeight - 128, _fontSize24, countdown, textColor, VerticalAlign.Center, HorizontalAlign.Center);
            _frameCanvas.DrawProgressBar(frameNumber / (float)totalFrames,
                (int)(OutputVideoWidth * 0.3f), (int)(OutputVideoWidth * 0.7f), OutputVideoHeight - 64, opacity);

            Exporter.PushNewFrame(_frameContent, silentAudioBuffer, _timer.Elapsed.TotalSeconds);
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
        
        // 根据第一个文件类型设置初始可视化模式（避免开始时的模式切换动画）
        string firstFilePath = _subfiles.FirstOrDefault()?.Path ?? InputFilePath;
        var initialMode = VisualizerTransition.GetModeForFile(firstFilePath);
        _visualizerTransition.SetInitialMode(initialMode);
        Logger.Info($"Initial visualizer mode: {initialMode} (based on first file: {Path.GetFileName(firstFilePath)})");

        // A/V SETTINGS 默认值（使用解码器格式，如果有多文件则会在循环内根据当前子文件动态更新）
        string avSettingsString = BuildAudioFormatString(AudioDecoderSampleRate, AudioDecoderChannelCount, 32);
        string readSpeedString = $"{InputBytesPerSecond / 1024} KiB/s";
        
        float subfileWindowIndex = 0f;
        long currentOffset = 0;
        int playHeadRelPos = 0;
        long frameNumber = 0;

        // 计算总帧数：优先基于音频源长度，否则基于文件长度
        long totalFrames;
        
        // 使用浮点数精确计算每帧采样数，避免整数除法的精度丢失
        // 注意：使用解码器声道数，因为读取的是解码器的数据
        double exactSamplesPerFrame = (double)AudioDecoderSampleRate / OutputFps * AudioDecoderChannelCount;
        // 用于整数计算的近似值
        int decoderSamplesPerFrame = (int)Math.Ceiling(exactSamplesPerFrame);
        // 音频采样位置累积器（使用 double 保持精度）
        double audioSamplePosition = 0;
        
        if (_audioSampleSource != null)
        {
            // 音频源长度（采样数）/ 每帧精确采样数 = 总帧数
            long audioLength = _audioSampleSource.Length;
            totalFrames = (long)(audioLength / exactSamplesPerFrame);
            Logger.Info($"Total frames based on audio: {totalFrames} (audio length: {audioLength} samples, exact samples/frame: {exactSamplesPerFrame:F3})");
        }
        else
        {
            // 基于文件长度
            totalFrames = InputFileStream.Length / InputBytesPerFrame;
        }
        
        // 计算总字节长度（用于瀑布数据边界检查）
        long totalByteLength = _multiFileBinarySource != null 
            ? _multiFileBinarySource.TotalLength 
            : InputFileStream.Length;

        using var targetFileReader = new BinaryReader(InputFileStream);
        
        // 渲染性能诊断
        var renderTimer = new System.Diagnostics.Stopwatch();
        double totalPixelProcessTime = 0, totalDrawTime = 0, totalPushTime = 0;
        int renderPerfSampleCount = 0;

        // 循环基于总帧数（音频驱动）
        while (frameNumber < totalFrames)
        {
            renderTimer.Restart();
            // 根据当前音频时间计算正确的字节偏移（解决多文件不同比特率导致的同步问题）
            double currentAudioTime = (double)frameNumber / OutputFps;
            
            // waterfallOffset: 用于瀑布读取的实际文件字节偏移
            // currentOffset: 用于 UI 显示的虚拟字节偏移
            long waterfallOffset = 0;
            
            // 多文件模式：根据音频时间找到对应子文件，计算正确的字节偏移
            if (_subfiles.Count > 0 && _subfiles[0].AudioDuration > 0)
            {
                bool found = false;
                foreach (var sf in _subfiles)
                {
                    // 找到当前时间所在的子文件
                    if (currentAudioTime >= sf.AudioStartTime && 
                        currentAudioTime < sf.AudioStartTime + sf.AudioDuration)
                    {
                        // 在子文件内的相对进度（0-1）
                        double relativeProgress = (currentAudioTime - sf.AudioStartTime) / sf.AudioDuration;
                        // 虚拟字节偏移（用于 UI 显示）
                        currentOffset = sf.StartOffset + (long)(sf.Length * relativeProgress);
                        // 实际字节偏移（用于瀑布读取）
                        waterfallOffset = sf.ActualByteOffset + (long)(sf.ActualByteLength * relativeProgress);
                        found = true;
                        break;
                    }
                }
                // 如果超出所有子文件（播放结束），使用最后位置
                if (!found && _subfiles.Count > 0)
                {
                    var lastSf = _subfiles[_subfiles.Count - 1];
                    currentOffset = lastSf.EndOffset - 1;
                    waterfallOffset = lastSf.ActualByteOffset + lastSf.ActualByteLength - 1;
                }
            }
            else
            {
                // 单文件模式或无音频时间信息：使用传统线性计算
                currentOffset = frameNumber * InputBytesPerFrame;
                waterfallOffset = currentOffset;
            }
            
            playHeadRelPos = 0;
            // 使用实际字节偏移计算瀑布读取位置
            int bytesPerLine = WaterfallWidth * 4;
            long alignedOffset = waterfallOffset.Align(bytesPerLine);
            long frameStartByteOffset = alignedOffset - (WaterfallFrameLength / 2);
            if (frameStartByteOffset < 0)
            {
                // 开头：播放头向下移动（正值）
                playHeadRelPos = (int)-(frameStartByteOffset / bytesPerLine);
                frameStartByteOffset = 0;
            }
            else if (frameStartByteOffset + WaterfallFrameLength >= totalByteLength)
            {
                // 末尾：瀑布停止后，播放头继续向上移动（负值）
                long maxAlignedStart = ((totalByteLength - WaterfallFrameLength) / bytesPerLine) * bytesPerLine;
                if (maxAlignedStart < 0) maxAlignedStart = 0;
                // 计算播放位置超出瀑布中心的行数（负值表示向上）
                long playPositionInFrame = (alignedOffset - maxAlignedStart) / bytesPerLine;
                playHeadRelPos = (int)(playPositionInFrame - WaterfallHeight / 2);
                // 取反使其向上移动
                playHeadRelPos = -playHeadRelPos;
                frameStartByteOffset = maxAlignedStart;
            }

            // 读取瀑布数据：复用视频缓冲区避免每帧分配
            if (_reusableVideoBuffer == null || _reusableVideoBuffer.Length < WaterfallFrameLength)
            {
                _reusableVideoBuffer = new byte[WaterfallFrameLength];
            }
            // 清零缓冲区（确保读取不足时剩余部分为黑色像素）
            Array.Clear(_reusableVideoBuffer, 0, WaterfallFrameLength);
            
            int bytesRead;
            if (_multiFileBinarySource != null)
            {
                // 多文件模式：从多文件二进制源读取实际文件数据
                bytesRead = _multiFileBinarySource.ReadAt(frameStartByteOffset, _reusableVideoBuffer, 0, WaterfallFrameLength);
            }
            else
            {
                // 单文件模式：从 InputFileStream 读取
                InputFileStream.Position = frameStartByteOffset;
                bytesRead = targetFileReader.Read(_reusableVideoBuffer, 0, WaterfallFrameLength);
            }

            // 获取音频缓冲
            if (_audioSampleSource != null)
            {
                // 精确计算本帧需要读取的采样数（使用浮点累积避免精度丢失）
                // 下一帧的采样位置
                double nextSamplePosition = audioSamplePosition + exactSamplesPerFrame;
                // 本帧实际需要读取的采样数（向下取整到整数）
                int samplesThisFrame = (int)nextSamplePosition - (int)audioSamplePosition;
                // 更新累积器
                audioSamplePosition = nextSamplePosition;
                
                if (_audioSampleBuffer == null || _audioSampleBuffer.Length < samplesThisFrame)
                {
                    _audioSampleBuffer = new float[decoderSamplesPerFrame + 16]; // 留一点余量
                }

                int readSamples = _audioSampleSource.Read(_audioSampleBuffer, 0, samplesThisFrame);
                if (readSamples < samplesThisFrame)
                {
                    Array.Clear(_audioSampleBuffer, readSamples, samplesThisFrame - readSamples);
                }

                // 传递实际读取的采样数，避免多余的静音采样导致 clicking
                _outputAudioBuffer.LoadFromInterleavedFloats(_audioSampleBuffer, AudioDecoderChannelCount, readSamples);
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

            // 将视频字节缓冲转换为位图，并完成垂直翻转
            if (_reusableWaterfallImage == null ||
                _reusableWaterfallImage.Width != WaterfallWidth ||
                _reusableWaterfallImage.Height != WaterfallHeight)
            {
                _reusableWaterfallImage?.Dispose();
                _reusableWaterfallImage = new SKBitmap(new SKImageInfo(WaterfallWidth, WaterfallHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
            }

            int srcBytesPerRow = WaterfallWidth * 4;
            var videoBuffer = _reusableVideoBuffer;

            // 使用 GetPixels 获取指针并转换为可写 Span
            IntPtr destPtr = _reusableWaterfallImage.GetPixels();
            int destRowBytes = _reusableWaterfallImage.RowBytes;
            int width = WaterfallWidth;
            int height = WaterfallHeight;

            unsafe
            {
                byte* destBase = (byte*)destPtr;
                int vectorSize = Vector<byte>.Count;
                
                // 使用缓存的 SIMD 向量（避免每帧创建）
                Vector<byte> alphaMask = _simdAlphaMask;
                Vector<byte> posMask = _simdPosMask;
                
                Parallel.For(0, height, y =>
                {
                    int destOffset = y * destRowBytes;
                    int srcOffset = (height - 1 - y) * srcBytesPerRow;
                    byte* destRow = destBase + destOffset;
                    int rowBytes = width * 4;
                    int x = 0;
                    
                    // SIMD 向量化处理（每次处理 vectorSize 字节）
                    fixed (byte* srcPtr = &videoBuffer[srcOffset])
                    {
                        for (; x <= rowBytes - vectorSize; x += vectorSize)
                        {
                            // 加载源数据
                            Vector<byte> src = *(Vector<byte>*)(srcPtr + x);
                            // 清除源数据中的 alpha 位，然后或上 255
                            Vector<byte> result = (src & ~posMask) | alphaMask;
                            // 写入目标
                            *(Vector<byte>*)(destRow + x) = result;
                        }
                        
                        // 处理剩余字节
                        for (; x < rowBytes; x += 4)
                        {
                            destRow[x + 0] = srcPtr[x + 0];
                            destRow[x + 1] = srcPtr[x + 1];
                            destRow[x + 2] = srcPtr[x + 2];
                            destRow[x + 3] = 255;
                        }
                    }
                });
            }

            // 复用缩放后的瀑布位图和 Canvas
            if (_reusableScaledWaterfall == null ||
                _reusableScaledWaterfall.Width != WaterfallScaledWidth ||
                _reusableScaledWaterfall.Height != WaterfallScaledHeight)
            {
                _reusableScaledWaterfallCanvas?.Dispose();
                _reusableScaledWaterfall?.Dispose();
                _reusableScaledWaterfall = new SKBitmap(new SKImageInfo(WaterfallScaledWidth, WaterfallScaledHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
                _reusableScaledWaterfallCanvas = new SKCanvas(_reusableScaledWaterfall);
            }

            // 复用 Canvas 绘制缩放瀑布（DrawBitmap 会完全覆盖目标区域）
            var srcRect = new SKRect(0, 0, WaterfallWidth, WaterfallHeight);
            var dstRect = new SKRect(0, 0, WaterfallScaledWidth, WaterfallScaledHeight);
            _imagePaint.FilterQuality = SKFilterQuality.None; // 保持像素风格
            _reusableScaledWaterfallCanvas.DrawBitmap(_reusableWaterfallImage, srcRect, dstRect, _imagePaint);

            _viewportFramebuf = _reusableScaledWaterfall;
            totalPixelProcessTime += renderTimer.Elapsed.TotalMilliseconds;
            renderTimer.Restart();

            // 计算当前帧覆盖到哪些子文件，并更新右侧列表窗位置
            int currentSubfileKey = -1;
            SubFile currentSubfileValue = null;
            long halfFrame = InputBytesPerFrame / 2;
            for (int i = _subfiles.Count - 1; i >= 0; i--)
            {
                var sf = _subfiles[i];
                if (sf.Intersects(currentOffset - halfFrame, currentOffset + halfFrame))
                {
                    currentSubfileKey = i;
                    currentSubfileValue = sf;
                    break;
                }
            }

            if (currentSubfileKey >= 0)
            {
                // 列表居中逻辑：前3个文件从开头往下移动，第3个之后固定在第3行位置
                // 当播放第3个及之后时，窗口索引=currentSubfileKey-2（当前项固定在第3行）
                float targetWindowIndex = currentSubfileKey < 3 ? 0 : currentSubfileKey - 2;
                subfileWindowIndex = 0.2f * subfileWindowIndex + 0.8f * targetWindowIndex;
                
                // 更新当前歌曲的峰值（用于波形和频谱归一化）
                _currentAudioPeak = currentSubfileValue?.AudioPeak ?? 1.0f;
                
                // MIDI 可视化模式检测
                string currentFilePath = currentSubfileValue?.Path;
                VisualizerMode requiredMode = VisualizerTransition.GetModeForFile(currentFilePath);
                _visualizerTransition.SwitchTo(requiredMode);
                
                // 如果是 MIDI 文件，确保可视化器已加载（切换到新 MIDI 文件时重新加载）
                if (requiredMode == VisualizerMode.PianoRoll && !string.IsNullOrEmpty(currentFilePath))
                {
                    // 检查是否需要加载新的 MIDI 可视化器（文件路径不同时重新加载）
                    if (_currentMidiVisualizer == null || _currentMidiVisualizer.FilePath != currentFilePath)
                    {
                        _currentMidiVisualizer = MidiVisualizerCache.GetOrCreate(currentFilePath);
                        // 从 MidiMetadata 获取打击乐通道列表
                        if (currentSubfileValue?.MidiMetadata?.DrumChannels != null)
                        {
                            _currentMidiVisualizer.SetDrumChannels(currentSubfileValue.MidiMetadata.DrumChannels);
                        }
                        // 清除打击乐缓存（新文件需要重新收集）
                        _currentDrumNotes = null;
                        _drumTriggerTimes.Clear();
                        _drumVelocities.Clear();
                    }
                }
            }
            
            // 更新可视化切换动画
            _visualizerTransition.Update(1f / OutputFps);

            // 5. 实际绘制一帧：左侧瀑布/钢琴卷帘 + 右侧上部列表 + 右下音频可视化 + 顶/底渐变 + 文字信息
            EnsureFrameCanvas();
            _frameCanvas.Clear(new SKColor(16, 16, 16));

            float s = ResolutionScale;
            int rightPanelX1 = _videoFrameX2 + (int)(64 * s);
            int rightPanelX2 = OutputVideoWidth - (int)(32 * s);
            int subfileX1 = rightPanelX1;
            int subfileX2 = rightPanelX2;
            int audioVisX1 = rightPanelX1;
            int audioVisX2 = rightPanelX2;

            float subfileH = 48f * s;
            float shadowY1 = (OutputVideoHeight / 2f) - subfileH * 8.5f;
            float shadowY2 = (OutputVideoHeight / 2f) + subfileH * 8.5f; 
            float rightPanelTop = shadowY1 + subfileH * 2f + 16f * s;
            float rightPanelBottom = shadowY2 - 16f * s;
            float rightPanelHeight = rightPanelBottom - rightPanelTop;
            float listHeight = rightPanelHeight * 0.20f;
            float listTop = rightPanelTop;
            float listBottom = listTop + listHeight;

            int firstSubfileIndex = Math.Max(0, (int)(subfileWindowIndex));
            int lastSubfileIndex = Math.Min(_subfiles.Count - 1, (int)Math.Ceiling(subfileWindowIndex + 9));
            float subfileRowH = 36f * s;
            // 列表起始位置：从 listTop 开始，第一行在中间位置
            float subfileY = listTop + subfileRowH / 2f - (subfileWindowIndex - firstSubfileIndex) * subfileRowH;
            float minSubfileY = rightPanelTop;

            // 直接绘制子文件列表（预渲染缓存反而更慢）
            for (int sfi = firstSubfileIndex; sfi <= lastSubfileIndex; sfi++)
            {
                if (sfi < 0 || sfi >= _subfiles.Count) { subfileY += subfileRowH; continue; }
                if (subfileY < minSubfileY) { subfileY += subfileRowH; continue; }

                var subfile = _subfiles[sfi];
                bool isMainSubfile = sfi == currentSubfileKey;

                DrawText(subfileX1, subfileY, _fontSize24, isMainSubfile ? "▶" : " ", SKColors.White, VerticalAlign.Center);
                DrawText(subfileX1 + 24 * s, subfileY, _fontSize24,
                    $"{Utils.GetFileTypeEmoji(subfile)} {Utils.TruncateString(BuildSubfileDisplayLine(subfile), 50)}",
                    SKColors.White, VerticalAlign.Center);
                DrawText(subfileX2, subfileY, _fontSize24, Utils.ToByteSizeString(subfile.Length),
                    SKColors.DimGray, VerticalAlign.Center, HorizontalAlign.Right);

                if (isMainSubfile)
                {
                    double currentAudioTimeLocal = (double)frameNumber / OutputFps;
                    float percentOfSubfile;

                    if (subfile.AudioDuration > 0)
                    {
                        double timeInTrack = Math.Max(0, currentAudioTimeLocal - subfile.AudioStartTime);
                        percentOfSubfile = (float)Math.Clamp(timeInTrack / subfile.AudioDuration, 0, 1);
                    }
                    else
                    {
                        double totalAudioTimeLocal = (double)totalFrames / OutputFps;
                        double subfileStartTime = (subfile.StartOffset / (double)totalByteLength) * totalAudioTimeLocal;
                        double subfileDuration = (subfile.Length / (double)totalByteLength) * totalAudioTimeLocal;
                        double timeInTrack = Math.Max(0, currentAudioTimeLocal - subfileStartTime);
                        percentOfSubfile = (float)Math.Clamp(timeInTrack / subfileDuration, 0, 1);
                    }

                    float progressY = subfileY + 22 * s;
                    DrawText(subfileX1 + 40 * s, progressY, _fontSize16,
                        $"{(int)(percentOfSubfile * 100)} %", SKColors.White,
                        VerticalAlign.Center, HorizontalAlign.Center);
                    _frameCanvas.DrawProgressBar(percentOfSubfile, (int)(subfileX1 + 60 * s), subfileX2, progressY);
                }

                subfileY += subfileRowH;
            }

            // 根据可视化模式绘制瀑布或钢琴卷帘（带过渡动画）
            var (waterfallSlideOffset, pianoRollSlideOffset) = _visualizerTransition.GetOffsets(WaterfallScaledWidth);
            var visualizerRegion = new SKRect(_videoFrameX1, _videoFrameY1, 
                _videoFrameX1 + WaterfallScaledWidth, _videoFrameY1 + WaterfallScaledHeight);
            
            // 绘制瀑布（如果可见）
            if (waterfallSlideOffset > -WaterfallScaledWidth && _viewportFramebuf != null)
            {
                _frameCanvas.Save();
                _frameCanvas.ClipRect(visualizerRegion);
                _frameCanvas.DrawBitmap(_viewportFramebuf, _videoFrameX1 + waterfallSlideOffset, _videoFrameY1, _imagePaint);
                _frameCanvas.Restore();
            }
            
            // 绘制钢琴卷帘（如果可见）
            if (pianoRollSlideOffset < WaterfallScaledWidth && _currentMidiVisualizer != null)
            {
                _frameCanvas.Save();
                _frameCanvas.ClipRect(visualizerRegion);
                _frameCanvas.Translate(pianoRollSlideOffset, 0);
                
                // 计算当前播放时间（毫秒）
                double currentTimeMs = (double)frameNumber / OutputFps * 1000.0;
                if (currentSubfileValue != null && currentSubfileValue.AudioStartTime > 0)
                {
                    currentTimeMs = ((double)frameNumber / OutputFps - currentSubfileValue.AudioStartTime) * 1000.0;
                }
                
                DrawPianoRoll(visualizerRegion, currentTimeMs, currentSubfileValue);
                _frameCanvas.Restore();
            }
            
            // 绘制模式切换标签
            var (modeLabel, labelAlpha) = _visualizerTransition.GetLabelState();
            if (labelAlpha > 0.01f && !string.IsNullOrEmpty(modeLabel))
            {
                byte alpha = (byte)(labelAlpha * 255);
                DrawText(_videoFrameX1 + WaterfallScaledWidth / 2f, _videoFrameY1 + 60, 
                    _fontSize32, modeLabel, new SKColor(255, 255, 255, alpha), 
                    VerticalAlign.Center, HorizontalAlign.Center);
            }
            
            // 统一播放头指示器 ▶
            // 计算瀑布模式下的播放头 Y 位置
            float waterfallPlayheadY = (OutputVideoHeight / 2f) + (playHeadRelPos * (WaterfallScaledHeight / (float)WaterfallHeight));
            // 钢琴窗模式下的播放头 Y 位置（固定居中）
            float pianoRollPlayheadY = _videoFrameY1 + WaterfallScaledHeight / 2f;
            // 更新播放头位置到动画控制器
            _visualizerTransition.SetWaterfallPlayheadY(waterfallPlayheadY);
            _visualizerTransition.SetPianoRollPlayheadY(pianoRollPlayheadY);
            // 绘制播放头（位置由动画控制器管理）
            DrawText(32, _visualizerTransition.GetPlayheadY(),
                _fontSize32, "▶", SKColors.White, VerticalAlign.Center);

            // 音频可视化区域（在歌曲列表下方，限制在遮罩区域内）
            float audioVisTop = listBottom + 8f * s;
            // 底部限制：不能超过遮罩渐变开始位置（shadowY2 - gradientHeight）
            float audioVisBottom = Math.Min(rightPanelBottom, shadowY2 - subfileH * 2.5f);
            if (audioVisBottom > audioVisTop + 16f && _outputAudioBuffer != null)
            {
                var audioVisRect = SKRect.Create(audioVisX1, audioVisTop, audioVisX2 - audioVisX1, audioVisBottom - audioVisTop);
                DrawAudioVisualizerSkia(audioVisRect, _outputAudioBuffer);
            }

            string albumHeaderText;
            {
                var sfValue = currentSubfileValue;
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

            // 淡出遮罩
            float bottomPanelTop = OutputVideoHeight - 120f * s;
            float correctedShadowY2 = Math.Min(shadowY2, bottomPanelTop);
            
            float gradientHeight = subfileH * 2f;
            EnsureGradientShadersCached(shadowY1, correctedShadowY2, gradientHeight);
            // 使用复用画笔绘制渐变遮罩
            if (_cachedTopGradientShader != null)
            {
                _gradientPaint.Shader = _cachedTopGradientShader;
                _frameCanvas.DrawRect(new SKRect(0, shadowY1, OutputVideoWidth, shadowY1 + gradientHeight), _gradientPaint);
            }
            if (_cachedBottomGradientShader != null)
            {
                _gradientPaint.Shader = _cachedBottomGradientShader;
                _frameCanvas.DrawRect(new SKRect(0, shadowY2 - gradientHeight, OutputVideoWidth, shadowY2), _gradientPaint);
            }

            // 专辑标题直接绘制
            DrawText(subfileX1 + 40, rightPanelTop - 24f, _fontSize24,
                Utils.TruncateString(albumHeaderText ?? string.Empty, 72), new SKColor(105, 105, 105), VerticalAlign.Center);

            // 确保静态UI层已缓存，然后绘制
            EnsureStaticUILayerCached(avSettingsString, readSpeedString);
            if (_staticUILayer != null)
            {
                _frameCanvas.DrawBitmap(_staticUILayer, 0, 0, _imagePaint);
            }

            // 计算当前播放时间（用于 MIDI 模式 UI）
            double topUITimeMs = (double)frameNumber / OutputFps * 1000.0;
            if (currentSubfileValue != null && currentSubfileValue.AudioStartTime > 0)
            {
                topUITimeMs = ((double)frameNumber / OutputFps - currentSubfileValue.AudioStartTime) * 1000.0;
            }
            
            // 绘制顶部 UI（根据 MIDI/Audio 模式自动切换，带滑动动画）
            DrawTopUILabels(currentSubfileValue, topUITimeMs);
            DrawTopUIValues(currentSubfileValue, currentOffset, avSettingsString, readSpeedString, topUITimeMs);

            // Author（居中显示）
            float valueY = 32 + _fontSize16 + 8;
            if (!string.IsNullOrEmpty(Author))
            {
                DrawText(OutputVideoWidth / 2f, valueY + _fontSize24 / 2, _fontSize24,
                    Author, SKColors.White, VerticalAlign.Center, HorizontalAlign.Center);
            }

            DrawBottomPlayerUISkia(s, currentOffset, currentSubfileKey, currentSubfileValue, frameNumber, totalFrames, totalByteLength);
            totalDrawTime += renderTimer.Elapsed.TotalMilliseconds;
            renderTimer.Restart();

            Exporter.PushNewFrame(_frameContent, _outputAudioBuffer, _timer.Elapsed.TotalSeconds);
            totalPushTime += renderTimer.Elapsed.TotalMilliseconds;
            _timer.Restart();

            frameNumber++;
            renderPerfSampleCount++;
            
            // 每 100 帧输出渲染性能统计
            if (renderPerfSampleCount % 100 == 0)
            {
                double avgPixel = totalPixelProcessTime / renderPerfSampleCount;
                double avgDraw = totalDrawTime / renderPerfSampleCount;
                double avgPush = totalPushTime / renderPerfSampleCount;
                Logger.Info($"[渲染性能] 像素处理: {avgPixel:F2}ms, 绘制: {avgDraw:F2}ms, 推送: {avgPush:F2}ms (avg/frame)");
            }

            // 当视频数据播放完毕时，保持在最后位置
            // currentOffset 可以超过 InputFileStream.Length，但读取时会被限制

            OnProgress?.Invoke(frameNumber / (float)totalFrames);

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
    
    // 生成音频格式显示字符串（用于 A/V SETTINGS）
    private string BuildAudioFormatString(int sampleRate, int channels, int bitDepth)
    {
        // 声道描述
        string channelDesc = channels switch
        {
            1 => "mono",
            2 => "stereo",
            6 => "6 ch",
            8 => "8 ch",
            _ => $"{channels} ch"
        };
        
        // 位深描述（如果为0则使用默认值）
        int bits = bitDepth > 0 ? bitDepth : 32;
        
        return $"{sampleRate} Hz, PCM signed {bits}-bit, {channelDesc}\n" +
               $"RGBA (32bpp), {WaterfallWidth} px/line";
    }
    
    // 绘制底部音乐播放器 UI
    // totalFrames 和 totalByteLength 用于计算基于音频时间的进度（与实际播放同步）
    private void DrawBottomPlayerUISkia(float s, long currentOffset, int subfileIdx, SubFile subfile, long frameNumber, long totalFrames, long totalByteLength)
    {
        if (_frameCanvas == null) return;
        
        // 更新当前视频时间（基于帧号和帧率）
        _currentVideoTime = (double)frameNumber / OutputFps;
        
        // 计算总音频时长和当前音频时间（基于帧号，与实际播放同步）
        double totalAudioTime = (double)totalFrames / OutputFps;
        double currentAudioTime = (double)frameNumber / OutputFps;
        
        float bottomPanelHeight = 100f * s;
        float bottomY = OutputVideoHeight - bottomPanelHeight - 16f * s;
        float coverSize = 72f * s;
        float coverX = 32f * s;
        float coverY = bottomY + (bottomPanelHeight - coverSize) / 2f;
        float infoX = coverX + coverSize + 16f * s;
        float timeX = OutputVideoWidth - 32f * s;
        
        // 获取当前歌曲信息
        string trackName = "";
        string composerName = "";  
        string genreText = "";
        TimeSpan currentTime = TimeSpan.Zero;
        TimeSpan totalTime = TimeSpan.Zero;
        float trackProgress = 0f;
        SKBitmap coverImage = null;
        float[] waveformPeaks = null;
        
        if (subfile != null)
        {
            // MIDI 文件优先使用元数据中的标题，否则使用 TrackTitle 或文件名
            if (subfile.IsMidi && subfile.MidiMetadata?.Title != null)
            {
                trackName = subfile.MidiMetadata.Title;
            }
            else
            {
                trackName = !string.IsNullOrWhiteSpace(subfile.TrackTitle) ? subfile.TrackTitle : subfile.FileName;
            }
            composerName = subfile.ComposerName ?? subfile.ArtistName ?? "";
            genreText = subfile.Genre ?? "";
            coverImage = subfile.Icon;
            waveformPeaks = subfile.WaveformPeaks;
            
            // 使用子文件的音频时间信息计算进度（优先）或降级到字节比例计算
            if (subfile.AudioDuration > 0)
            {
                double timeInTrack = Math.Max(0, currentAudioTime - subfile.AudioStartTime);
                
                totalTime = TimeSpan.FromSeconds(subfile.AudioDuration);
                currentTime = TimeSpan.FromSeconds(Math.Min(timeInTrack, subfile.AudioDuration));
                trackProgress = (float)Math.Clamp(timeInTrack / subfile.AudioDuration, 0, 1);
            }
            else if (totalByteLength > 0 && totalAudioTime > 0)
            {
                double subfileStartTime = (subfile.StartOffset / (double)totalByteLength) * totalAudioTime;
                double subfileDuration = (subfile.Length / (double)totalByteLength) * totalAudioTime;
                double timeInTrack = Math.Max(0, currentAudioTime - subfileStartTime);
                
                totalTime = TimeSpan.FromSeconds(subfileDuration);
                currentTime = TimeSpan.FromSeconds(Math.Min(timeInTrack, subfileDuration));
                trackProgress = (float)Math.Clamp(timeInTrack / subfileDuration, 0, 1);
            }
        }
        
        // 构建显示文本（根据 maxWidth 动态截断）
        string displayInfo = trackName;
        bool hasComposer = !string.IsNullOrWhiteSpace(composerName);
        bool hasGenre = !string.IsNullOrWhiteSpace(genreText);
        if (hasComposer) displayInfo += $" // {composerName}";
        if (hasGenre) displayInfo += $" [{genreText}]";
        
        // 计算各部分在显示文本中的位置（用于灰色标签对齐）
        float titleEndX = infoX + MeasureTextWidth(trackName, _fontSize32);
        float composerStartX = hasComposer ? titleEndX + MeasureTextWidth(" // ", _fontSize32) : 0;
        float genreStartX = 0;
        if (hasGenre)
        {
            string beforeGenre = trackName;
            if (hasComposer) beforeGenre += $" // {composerName}";
            genreStartX = infoX + MeasureTextWidth(beforeGenre + " ", _fontSize32);
        }
        
        string timeString = $"{(int)currentTime.TotalMinutes}:{currentTime.Seconds:D2} / {(int)totalTime.TotalMinutes}:{totalTime.Seconds:D2}";
        
        // 检测歌曲切换，触发动画（基于时间）
        bool isInTransition = false;
        float animT = 1f;
        
        // 计算当前封面哈希
        int targetSize = Math.Max(8, (int)coverSize - 4);
        byte[] currentCoverHash = null;
        if (coverImage != null)
        {
            // 有封面：缓存缩放后的封面
            if (_cachedScaledCover == null || _cachedCoverSubfileIndex != subfileIdx || _cachedCoverSize != targetSize)
            {
                _cachedScaledCover?.Dispose();
                _cachedScaledCover = new SKBitmap(targetSize, targetSize, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var coverCanvas = new SKCanvas(_cachedScaledCover))
                {
                    coverCanvas.Clear(SKColors.Transparent);
                    // 重要：绘制缓存封面时使用白色（不受过渡动画 alpha 影响）
                    _coverPaint.Color = SKColors.White;
                    coverCanvas.DrawBitmap(coverImage, new SKRect(0, 0, targetSize, targetSize), _coverPaint);
                }
                _cachedCoverSubfileIndex = subfileIdx;
                _cachedCoverSize = targetSize;
                Logger.Debug($"Cached cover for subfile {subfileIdx}, size={targetSize}x{targetSize}");
            }
            currentCoverHash = ComputeImageHash(_cachedScaledCover);
        }
        else if (_cachedCoverSubfileIndex != subfileIdx)
        {
            // 无封面且歌曲已切换：清理缓存的封面（修复无法切换问题）
            _cachedScaledCover?.Dispose();
            _cachedScaledCover = null;
            _cachedCoverSubfileIndex = subfileIdx;
        }
        
        // 检测歌曲切换（注意：这里 displayInfo/timeString 已经是新歌曲的信息）
        if (subfileIdx != _lastSubfileIndex && subfileIdx >= 0)
        {
            // 第一首歌曲时，跳过切换动画，直接初始化
            bool isFirstSong = _lastSubfileIndex < 0;
            if (isFirstSong)
            {
                // 初始化当前信息，不触发动画
                _currentDisplayInfo = displayInfo;
                _currentTimeString = timeString;
                _currentWaveformPeaks = waveformPeaks?.ToArray();
                _prevDisplayInfo = displayInfo;
                _prevTimeString = timeString;
                _prevWaveformPeaks = waveformPeaks?.ToArray();
                _prevCoverHash = currentCoverHash;
                if (_cachedScaledCover != null)
                {
                    _prevScaledCover?.Dispose();
                    _prevScaledCover = _cachedScaledCover.Copy();
                }
                _prevComposerLabelX = hasComposer ? composerStartX : 0;
                _prevGenreLabelX = hasGenre ? genreStartX : 0;
                _prevHasComposer = hasComposer;
                _prevHasGenre = hasGenre;
                // 不触发动画
                _animationStartTime = -1;
            }
            else
            {
                // 正常歌曲切换，触发动画
                // 使用上一帧保存的"当前信息"作为"前一首信息"（解决快速切换问题）
                _prevDisplayInfo = _currentDisplayInfo;
                _prevTimeString = _currentTimeString;
                _prevWaveformPeaks = _currentWaveformPeaks?.ToArray();
                
                // 判断封面是否相同
                bool coverSame = HashEquals(_prevCoverHash, currentCoverHash);
                if (!coverSame && _cachedScaledCover != null)
                {
                    _prevScaledCover?.Dispose();
                    _prevScaledCover = _cachedScaledCover.Copy();
                }
                _prevCoverHash = currentCoverHash;
                
                // 开始动画（基于时间）
                _animationStartTime = _currentVideoTime;
            }
            _lastSubfileIndex = subfileIdx;
        }
        
        // 每帧更新"当前信息"（用于下次切换时作为"前一首"）
        _currentDisplayInfo = displayInfo;
        _currentTimeString = timeString;
        _currentWaveformPeaks = waveformPeaks;
        
        // 计算动画进度（基于时间，帧率无关）
        // 仅当前一首信息有效时才显示动画（避免空白淡出）
        bool prevInfoValid = !string.IsNullOrEmpty(_prevDisplayInfo);
        if (_animationStartTime >= 0 && _currentVideoTime < _animationStartTime + AnimationDurationSeconds && prevInfoValid)
        {
            isInTransition = true;
            float rawT = (float)((_currentVideoTime - _animationStartTime) / AnimationDurationSeconds);
            animT = EaseOutCubic(Math.Clamp(rawT, 0f, 1f));
        }
        
        float labelY = bottomY + 4f * s;
        float valueY = labelY + 18f * s;
        float waveformY = valueY + 32f * s;
        float waveformX1 = infoX;
        float waveformX2 = timeX;
        float waveformHeight = 20f * s;
        
        // 封面底板与描边
        var coverRect = SKRect.Create(coverX, coverY, coverSize, coverSize);
        _fillPaint.Color = new SKColor(32, 32, 32);
        _frameCanvas.DrawRect(coverRect, _fillPaint);
        _strokePaint.Color = new SKColor(200, 200, 200);
        _strokePaint.StrokeWidth = 2f;
        _frameCanvas.DrawRect(coverRect, _strokePaint);
        
        bool coverSameAsPrev = HashEquals(_prevCoverHash, currentCoverHash);
        float coverSlideOffset = (isInTransition && !coverSameAsPrev) ? (1f - animT) * coverSize * 0.4f : 0f;
        
        // 绘制前一首封面（向左滑出，仅当封面不同）
        if (isInTransition && _prevScaledCover != null && !coverSameAsPrev)
        {
            float prevAlpha = 1f - animT;
            float prevOffsetX = -coverSlideOffset;
            var prevDest = SKRect.Create(coverX + 2 + prevOffsetX, coverY + 2, targetSize, targetSize);
            _coverPaint.Color = SKColors.White.WithAlpha((byte)(255 * prevAlpha));
            _frameCanvas.DrawBitmap(_prevScaledCover, prevDest, _coverPaint);
        }
        
        // 绘制当前封面
        if (_cachedScaledCover != null)
        {
            float currAlpha = (isInTransition && !coverSameAsPrev) ? animT : 1f;
            float currOffsetX = (isInTransition && !coverSameAsPrev) ? coverSlideOffset : 0f;
            var currDest = SKRect.Create(coverX + 2 + currOffsetX, coverY + 2, targetSize, targetSize);
            _coverPaint.Color = SKColors.White.WithAlpha((byte)(255 * currAlpha));
            _frameCanvas.DrawBitmap(_cachedScaledCover, currDest, _coverPaint);
        }
        else
        {
            // 没有封面时显示音符
            byte noteAlpha = (byte)(255 * (isInTransition ? animT : 1f));
            DrawText(coverX + coverSize / 2, coverY + coverSize / 2, _fontSize48, "♪",
                new SKColor(100, 100, 100, noteAlpha), VerticalAlign.Center, HorizontalAlign.Center);
        }
        
        // 文字与标签
        var labelColor = new SKColor(105, 105, 105);
        
        // Title: 和 Time: 标签已预渲染到静态 UI 层
        
        // 绘制动态灰色标签（Composer 和 Genre，带滑动~~~~~~~~~）
        if (isInTransition)
        {
            // 计算插值位置
            float currComposerX = hasComposer ? composerStartX : _prevComposerLabelX;
            float currGenreX = hasGenre ? genreStartX : _prevGenreLabelX;
            
            // Composer 标签动画
            if (_prevHasComposer || hasComposer)
            {
                float animComposerX;
                byte composerAlpha;
                if (_prevHasComposer && hasComposer)
                {
                    // 两首都有 Composer：滑动
                    animComposerX = _prevComposerLabelX + (currComposerX - _prevComposerLabelX) * animT;
                    composerAlpha = 255;
                }
                else if (hasComposer)
                {
                    // 新曲有 Composer：淡入
                    animComposerX = composerStartX;
                    composerAlpha = (byte)(255 * animT);
                }
                else
                {
                    // 旧曲有 Composer：淡出
                    animComposerX = _prevComposerLabelX;
                    composerAlpha = (byte)(255 * (1f - animT));
                }
                DrawText(animComposerX, labelY, _fontSize16, "Composer:", new SKColor(105, 105, 105, composerAlpha));
            }
            
            // Genre 标签动画
            if (_prevHasGenre || hasGenre)
            {
                float animGenreX;
                byte genreAlpha;
                if (_prevHasGenre && hasGenre)
                {
                    // 两首都有 Genre：滑动
                    animGenreX = _prevGenreLabelX + (currGenreX - _prevGenreLabelX) * animT;
                    genreAlpha = 255;
                }
                else if (hasGenre)
                {
                    // 新曲有 Genre：淡入
                    animGenreX = genreStartX;
                    genreAlpha = (byte)(255 * animT);
                }
                else
                {
                    // 旧曲有 Genre：淡出
                    animGenreX = _prevGenreLabelX;
                    genreAlpha = (byte)(255 * (1f - animT));
                }
                DrawText(animGenreX, labelY, _fontSize16, "Genre:", new SKColor(105, 105, 105, genreAlpha));
            }
        }
        else
        {
            // 非过渡状态：直接绘制
            if (hasComposer)
            {
                DrawText(composerStartX, labelY, _fontSize16, "Composer:", labelColor);
            }
            if (hasGenre)
            {
                DrawText(genreStartX, labelY, _fontSize16, "Genre:", labelColor);
            }
            
            // 更新前一首标签位置
            _prevComposerLabelX = hasComposer ? composerStartX : 0;
            _prevGenreLabelX = hasGenre ? genreStartX : 0;
            _prevHasComposer = hasComposer;
            _prevHasGenre = hasGenre;
        }
        
        // 曲目信息字符级动画
        DrawTextWithCharacterAnimationSkia(_fontSize32, displayInfo, _prevDisplayInfo, infoX, valueY, timeX - infoX - 120f * s, animT, isInTransition);
        
        // 时间显示：淡入淡出
        if (isInTransition && !string.IsNullOrEmpty(_prevTimeString))
        {
            // 淡出旧时间
            byte fadeOutAlpha = (byte)(255 * (1f - animT));
            DrawText(timeX, valueY, _fontSize32, _prevTimeString,
                new SKColor(255, 255, 255, fadeOutAlpha), VerticalAlign.Top, HorizontalAlign.Right);
            
            // 淡入新时间
            byte fadeInAlpha = (byte)(255 * animT);
            DrawText(timeX, valueY, _fontSize32, timeString,
                new SKColor(255, 255, 255, fadeInAlpha), VerticalAlign.Top, HorizontalAlign.Right);
        }
        else
        {
            // 非过渡状态，直接绘制
            DrawText(timeX, valueY, _fontSize32, timeString, SKColors.White, VerticalAlign.Top, HorizontalAlign.Right);
        }
        
        // 波形动画：前一首从中间向上滑出+淡出，新波形从下向上滑入+淡入 
        var waveformRect = SKRect.Create(waveformX1, waveformY, Math.Max(0.1f, waveformX2 - waveformX1), waveformHeight);
        _fillPaint.Color = new SKColor(40, 40, 40);
        _frameCanvas.DrawRect(waveformRect, _fillPaint);
        
        float barWidth = 2f;
        float barSpacing = 1f;
        int totalBars = (int)(waveformRect.Width / (barWidth + barSpacing));
        float progressX = waveformRect.Left + waveformRect.Width * trackProgress;
        float gapWidth = 4f * s;
        
        // 波形滑动偏移
        float waveSlideOffset = isInTransition ? (1f - animT) * waveformHeight * 1.2f : 0f;
        byte currWaveAlpha = (byte)(255 * (isInTransition ? animT : 1f));
        byte prevWaveAlpha = (byte)(255 * (1f - animT));
        
        // 使用 SKPath 批量绘制波形条
        using var pathPrevWave = new SKPath();
        using var pathPlayedWave = new SKPath();
        using var pathUnplayedWave = new SKPath();
        
        for (int i = 0; i < totalBars; i++)
        {
            float barX = waveformRect.Left + i * (barWidth + barSpacing);
            if (barX > progressX - gapWidth && barX < progressX + gapWidth) continue;
            
            // 前一首波形（从中间向上滑出 + 淡出）
            if (isInTransition && _prevWaveformPeaks != null && _prevWaveformPeaks.Length > 0)
            {
                int prevPeakIndex = Math.Clamp((int)((float)i / totalBars * _prevWaveformPeaks.Length), 0, _prevWaveformPeaks.Length - 1);
                float prevHeightRatio = 0.15f + _prevWaveformPeaks[prevPeakIndex] * 0.85f;
                float prevBarHeight = waveformHeight * prevHeightRatio * 0.85f;
                float prevBarY = waveformRect.Top + (waveformHeight - prevBarHeight) / 2f - waveSlideOffset;
                
                if (prevBarY >= waveformRect.Top && prevBarY + prevBarHeight <= waveformRect.Top + waveformHeight)
                {
                    pathPrevWave.AddRect(SKRect.Create(barX, prevBarY, barWidth, prevBarHeight));
                }
            }
            
            // 当前波形（从下向上滑入 + 淡入）
            float heightRatio;
            bool hasRealWaveform = waveformPeaks != null && waveformPeaks.Length > 0;
            if (hasRealWaveform)
            {
                int peakIndex = Math.Clamp((int)((float)i / totalBars * waveformPeaks.Length), 0, waveformPeaks.Length - 1);
                heightRatio = 0.15f + waveformPeaks[peakIndex] * 0.85f;
            }
            else
            {
                heightRatio = 0.3f + 0.6f * (float)Math.Abs(Math.Sin(i * 0.4 + subfileIdx * 0.1));
            }
            
            float barHeight = waveformHeight * heightRatio * 0.85f;
            float barY = waveformRect.Top + (waveformHeight - barHeight) / 2f + waveSlideOffset;
            
            if (barY >= waveformRect.Top && barY + barHeight <= waveformRect.Top + waveformHeight)
            {
                if (barX < progressX)
                    pathPlayedWave.AddRect(SKRect.Create(barX, barY, barWidth, barHeight));
                else
                    pathUnplayedWave.AddRect(SKRect.Create(barX, barY, barWidth, barHeight));
            }
        }
        
        // 批量绘制（3次 DrawPath 替代 200+ 次 DrawRect）
        if (isInTransition && !pathPrevWave.IsEmpty)
        {
            _fillPaint.Color = new SKColor(180, 180, 180, prevWaveAlpha);
            _frameCanvas.DrawPath(pathPrevWave, _fillPaint);
        }
        if (!pathPlayedWave.IsEmpty)
        {
            _fillPaint.Color = new SKColor(100, 100, 100, currWaveAlpha);
            _frameCanvas.DrawPath(pathPlayedWave, _fillPaint);
        }
        if (!pathUnplayedWave.IsEmpty)
        {
            _fillPaint.Color = new SKColor(220, 220, 220, currWaveAlpha);
            _frameCanvas.DrawPath(pathUnplayedWave, _fillPaint);
        }
        
        // 播放头
        float headlineWidth = 2f;
        float borderWidth = 1f;
        _fillPaint.Color = new SKColor(30, 30, 30);
        _frameCanvas.DrawRect(SKRect.Create(progressX - headlineWidth / 2 - borderWidth, waveformRect.Top - 2f, borderWidth, waveformHeight + 4f), _fillPaint);
        _fillPaint.Color = SKColors.White;
        _frameCanvas.DrawRect(SKRect.Create(progressX - headlineWidth / 2, waveformRect.Top - 2f, headlineWidth, waveformHeight + 4f), _fillPaint);
        _fillPaint.Color = new SKColor(30, 30, 30);
        _frameCanvas.DrawRect(SKRect.Create(progressX + headlineWidth / 2, waveformRect.Top - 2f, borderWidth, waveformHeight + 4f), _fillPaint);
        
        if (!isInTransition)
        {
            _prevDisplayInfo = displayInfo;
            _prevTimeString = timeString;
        }
    }

    // 绘制带动画的文字
    // 使用 DrawText 确保 Y 坐标计算与非动画状态一致
    private void DrawTextWithCharacterAnimationSkia(float fontSize, string newText, string oldText,
        float x, float y, float maxWidth, float animT, bool isInTransition, bool rightAlign = false)
    {
        if (_frameCanvas == null) return;
        newText ??= string.Empty;
        oldText ??= string.Empty;
        
        // 裁剪文本
        if (maxWidth > 0)
        {
            newText = ClampTextToWidth(newText, fontSize, maxWidth);
            oldText = ClampTextToWidth(oldText, fontSize, maxWidth);
        }
        if (string.IsNullOrEmpty(newText) && string.IsNullOrEmpty(oldText)) return;
        
        // 非动画状态：直接绘制新文本
        if (!isInTransition || string.IsNullOrEmpty(oldText))
        {
            DrawText(x, y, fontSize, newText, SKColors.White,
                VerticalAlign.Top, rightAlign ? HorizontalAlign.Right : HorizontalAlign.Left);
            return;
        }
        
        // 动画状态：使用 DrawText 确保 Y 坐标一致
        float slideDistance = 30f * ResolutionScale;
        var hAlign = rightAlign ? HorizontalAlign.Right : HorizontalAlign.Left;
        
        // 旧文本：向左滑动 + 淡出
        byte fadeOutAlpha = (byte)(255 * (1f - animT));
        float oldOffsetX = -slideDistance * animT;
        DrawText(x + oldOffsetX, y, fontSize, oldText, 
            new SKColor(255, 255, 255, fadeOutAlpha), VerticalAlign.Top, hAlign);
        
        // 新文本：从右滑入 + 淡入
        byte fadeInAlpha = (byte)(255 * animT);
        float newOffsetX = slideDistance * (1f - animT);
        DrawText(x + newOffsetX, y, fontSize, newText, 
            new SKColor(255, 255, 255, fadeInAlpha), VerticalAlign.Top, hAlign);
    }

    // 生成结尾淡出画面
    private void GenerateOutro()
    {
        Logger.Info("Generating outro fade…");
        int fadeFrames = Math.Max(1, (int)(OutroFadeDuration * OutputFps));

        int sampleCount = Math.Max(1, _outputAudioBuffer?.SampleCount ?? AudioOutputSamplesPerFramePerChannel);
        int channelCount = Math.Max(1, _outputAudioBuffer?.ChannelCount ?? AudioOutputChannelCount);
        var silentBuffer = new AudioBuffer(sampleCount, channelCount);
        silentBuffer.Clear();

        EnsureFrameCanvas();
        if (_frameCanvas == null)
        {
            Logger.Error("Frame canvas is not initialized; cannot render outro.");
            return;
        }

        for (int frameNum = 0; frameNum < fadeFrames; frameNum++)
        {
            float opacity = 1f - (frameNum / (float)fadeFrames);
            byte overlayAlpha = (byte)(255 * (1f - Math.Clamp(opacity, 0f, 1f)));

            _fillPaint.Color = new SKColor(16, 16, 16, overlayAlpha);
            _frameCanvas.DrawRect(SKRect.Create(0, 0, OutputVideoWidth, OutputVideoHeight), _fillPaint);

            Exporter.PushNewFrame(_frameContent, silentBuffer, _timer.Elapsed.TotalSeconds);
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
    // 频谱频率映射缓存（避免每帧重复计算 Math.Pow/Log）
    private int[] _spectrumBin0Cache = null;
    private int[] _spectrumBin1Cache = null;
    private float[] _spectrumCenterFreqCache = null;
    private float[] _spectrumBarsCache = null; // 复用 bars 数组
    private int _spectrumCacheBarCount = 0;
    private int _spectrumCacheFftSize = 0;
    // 平滑系数缓存（避免每帧计算 Math.Exp）
    private float _cachedAttackAlpha = 0f;
    private float _cachedReleaseAlpha = 0f;
    private float _cachedAttackMs = 0f;
    private float _cachedReleaseMs = 0f;
    private int _cachedOutputFps = 0;
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

    // 音频可视化绘制
    private void DrawAudioVisualizerSkia(SKRect region, AudioBuffer audioBuffer)
    {
        if (audioBuffer == null || audioBuffer.TotalSampleCount == 0) return;

        int channelCount = audioBuffer.ChannelCount;
        int sampleCount = audioBuffer.SampleCount;
        if (channelCount <= 0 || sampleCount <= 0) return;

        // 复用数组（避免每帧分配）
        int interleavedLen = sampleCount * channelCount;
        if (_audioInterleavedBuffer == null || _audioInterleavedBuffer.Length < interleavedLen)
        {
            _audioInterleavedBuffer = new float[interleavedLen];
        }
        // 直接复制到预分配缓冲区，避免 ToArray() 的内存分配
        audioBuffer.CopyTo(_audioInterleavedBuffer);

        // 复用单声道数组（避免每帧分配）
        if (_audioMonoBuffer == null || _audioMonoBuffer.Length < sampleCount)
        {
            _audioMonoBuffer = new float[sampleCount];
        }
        
        // SIMD 优化的声道混合（解交错 + 混音）
        if (channelCount == 2 && Vector.IsHardwareAccelerated)
        {
            // 立体声：SIMD 向量化解交错 + 混音
            int vectorSize = Vector<float>.Count;
            int i = 0;
            
            for (; i <= sampleCount - vectorSize; i += vectorSize)
            {
                // 解交错：从交错数据中提取左右声道并求平均
                for (int k = 0; k < vectorSize && i + k < sampleCount; k++)
                {
                    int idx = (i + k) * 2;
                    _audioMonoBuffer[i + k] = (_audioInterleavedBuffer[idx] + _audioInterleavedBuffer[idx + 1]) * 0.5f;
                }
            }
            
            // 处理剩余采样
            for (; i < sampleCount; i++)
            {
                int idx = i * 2;
                _audioMonoBuffer[i] = (_audioInterleavedBuffer[idx] + _audioInterleavedBuffer[idx + 1]) * 0.5f;
            }
        }
        else if (channelCount == 2)
        {
            // 立体声回退：无 SIMD
            for (int i = 0; i < sampleCount; i++)
            {
                int idx = i * 2;
                _audioMonoBuffer[i] = (_audioInterleavedBuffer[idx] + _audioInterleavedBuffer[idx + 1]) * 0.5f;
            }
        }
        else if (channelCount == 1)
        {
            // 单声道：直接复制
            Array.Copy(_audioInterleavedBuffer, _audioMonoBuffer, sampleCount);
        }
        else
        {
            // 多声道处理
            // 使用 sqrt(channelCount) 而非 channelCount：
            // - 非相关信号的能量累加是 sqrt(N) 倍
            // - 环绕声通常只有部分声道活跃，直接除以 N 会导致振幅过小
            float sqrtChannels = (float)Math.Sqrt(channelCount);
            float invScale = 1f / sqrtChannels;
            for (int i = 0; i < sampleCount; i++)
            {
                float sum = 0f;
                int baseIdx = i * channelCount;
                for (int ch = 0; ch < channelCount; ch++)
                {
                    sum += _audioInterleavedBuffer[baseIdx + ch];
                }
                _audioMonoBuffer[i] = sum * invScale;
            }
        }
        
        // 使用歌曲整体峰值进行归一化
        float songPeak = _currentAudioPeak > 0.001f ? _currentAudioPeak : 1.0f;
        float normScale = 1f / songPeak;
        
        // SIMD 优化的归一化
        if (Vector.IsHardwareAccelerated)
        {
            int vectorSize = Vector<float>.Count;
            var scaleVec = new Vector<float>(normScale);
            int i = 0;
            
            for (; i <= sampleCount - vectorSize; i += vectorSize)
            {
                var vec = new Vector<float>(_audioMonoBuffer, i);
                (vec * scaleVec).CopyTo(_audioMonoBuffer, i);
            }
            
            // 处理剩余采样
            for (; i < sampleCount; i++)
            {
                _audioMonoBuffer[i] *= normScale;
            }
        }
        else
        {
            for (int i = 0; i < sampleCount; i++)
            {
                _audioMonoBuffer[i] *= normScale;
            }
        }

        // 布局：上 55% 波形，下 45% 频谱
        float waveH = region.Height * 0.55f;
        float specH = region.Height * 0.45f;
        float waveTop = region.Top;
        float specTop = waveTop + waveH;

        // 绘制波形
        DrawWaveformSkia(SKRect.Create(region.Left, waveTop, region.Width, waveH), _audioMonoBuffer);

        // 绘制频谱
        DrawSpectrumSkia(SKRect.Create(region.Left, specTop, region.Width, specH), _audioMonoBuffer);
    }

    // 波形（折线）
    private void DrawWaveformSkia(SKRect region, float[] samples)
    {
        if (samples.Length < 2) return;

        // 显示窗口
        int windowSize = (int)(AudioOutputSampleRate * WaveformLengthMs / 1000f);
        windowSize = Math.Clamp(windowSize, 64, samples.Length);
        
        // 计算起始索引（默认从末尾开始）
        int startIdx = samples.Length - windowSize;
        
        // Zero-crossing Trigger：找到正向过零点作为波形起始位置
        if (WaveformTriggerEnabled && windowSize < samples.Length - 100)
        {
            // 搜索范围：从默认起始位置向前搜索半个窗口大小
            int searchStart = Math.Max(0, startIdx - windowSize / 2);
            int searchEnd = startIdx;
            int triggerOffset = -1;
            
            // 寻找正向过零点（从负到正的穿越）
            for (int i = searchStart + 1; i < searchEnd; i++)
            {
                if (samples[i - 1] < 0 && samples[i] >= 0)
                {
                    triggerOffset = i;
                    break;
                }
            }
            
            // 如果找到触发点，使用它作为起始位置
            if (triggerOffset >= 0)
            {
                // 如果新触发点与上一帧差异太大，进行平滑
                int maxJump = windowSize / 4;
                if (_lastTriggerOffset > 0 && Math.Abs(triggerOffset - _lastTriggerOffset) > maxJump)
                {
                    // 限制跳变幅度
                    triggerOffset = _lastTriggerOffset + Math.Sign(triggerOffset - _lastTriggerOffset) * maxJump;
                }
                startIdx = Math.Clamp(triggerOffset, 0, samples.Length - windowSize);
                _lastTriggerOffset = triggerOffset;
            }
        }

        // 已在 DrawAudioVisualizerSkia 中归一化
        float centerY = region.MidY;
        float amplitude = region.Height * 0.45f;
        float lineWidth = Math.Max(1.5f, WaveformLineWidth * ResolutionScale);

        // 下采样点数（折线顶点数）
        int pointCount = Math.Min(512, windowSize); 
        if (_waveformPointCache == null || _waveformPointCache.Length < pointCount)
        {
            _waveformPointCache = new SKPoint[pointCount];
        }
        var points = _waveformPointCache;

        for (int i = 0; i < pointCount; i++)
        {
            // 每个点对应的样本索引
            int sampleIdx = startIdx + (i * windowSize / pointCount);
            // 直接使用已归一化的样本
            float v = samples[sampleIdx];
            float x = region.Left + (i / (float)(pointCount - 1)) * region.Width;
            float y = centerY - v * amplitude;
            points[i] = new SKPoint(x, y);
        }
        _strokePaint.Color = SKColors.White;
        _strokePaint.StrokeWidth = lineWidth;
        // 使用 ArraySegment 避免额外分配（SkiaSharp 需要数组）
        if (pointCount == points.Length)
        {
            _frameCanvas.DrawPoints(SKPointMode.Polygon, points, _strokePaint);
        }
        else
        {
            // 只有在点数不同时才需要创建子数组
            _frameCanvas.DrawPoints(SKPointMode.Polygon, points[..pointCount], _strokePaint);
        }
    }

    // 频谱绘制
    private void DrawSpectrumSkia(SKRect region, float[] samples)
    {
        int fftSize = GetValidFftSize();
        
        if (_audioHistoryBuffer == null || _audioHistoryBuffer.Length != fftSize)
        {
            _audioHistoryBuffer = new float[fftSize];
            _audioHistoryWritePos = 0;
        }
        
        // samples 已在 DrawAudioVisualizerSkia 中归一化到 [-1, 1]
        for (int i = 0; i < samples.Length; i++)
        {
            _audioHistoryBuffer[_audioHistoryWritePos] = samples[i];
            _audioHistoryWritePos = (_audioHistoryWritePos + 1) % fftSize;
        }

        int n = fftSize;
        int halfN = n / 2;

        if (_fftReal == null || _fftReal.Length != n)
        {
            _fftReal = new double[n];
            _fftImag = new double[n];
            _fftMagnitudes = new float[halfN];
        }

        // 使用 Blackman-Harris 窗口（比 Hann 窗口有更好的旁瓣抑制，-92dB vs -31dB）
        PrecomputeBlackmanHarrisWindow(n);

        for (int i = 0; i < n; i++)
        {
            int idx = (_audioHistoryWritePos + i) % n;
            _fftReal[i] = _audioHistoryBuffer[idx] * _blackmanHarrisWindow[i];
            _fftImag[i] = 0;
        }

        ComputeFFT(_fftReal, _fftImag, n);
        ComputeMagnitudesSimd(_fftReal, _fftImag, _fftMagnitudes, halfN, n);

        // 频率范围
        int barCount = Math.Clamp(SpectrumBarCount, 16, 128);
        
        // 复用 bars 数组
        if (_spectrumBarsCache == null || _spectrumBarsCache.Length != barCount)
        {
            _spectrumBarsCache = new float[barCount];
        }
        float[] bars = _spectrumBarsCache;

        // 预计算频率映射缓存（仅在参数变化时重新计算）
        if (_spectrumBin0Cache == null || _spectrumCacheBarCount != barCount || _spectrumCacheFftSize != n)
        {
            float freqPerBin = (float)AudioOutputSampleRate / n;
            float minFreq = 20f;
            float nyquistFreq = AudioOutputSampleRate / 2f;
            float maxFreq = nyquistFreq * 0.98f;
            float logMin = (float)Math.Log10(minFreq);
            float logMax = (float)Math.Log10(maxFreq);
            float logRange = logMax - logMin;
            
            _spectrumBin0Cache = new int[barCount];
            _spectrumBin1Cache = new int[barCount];
            _spectrumCenterFreqCache = new float[barCount];
            
            for (int i = 0; i < barCount; i++)
            {
                float t0 = i / (float)barCount;
                float t1 = (i + 1) / (float)barCount;
                float freqLo = (float)Math.Pow(10, logMin + t0 * logRange);
                float freqHi = (float)Math.Pow(10, logMin + t1 * logRange);
                _spectrumCenterFreqCache[i] = (float)Math.Sqrt(freqLo * freqHi);
                
                float binLo = freqLo / freqPerBin;
                float binHi = freqHi / freqPerBin;
                _spectrumBin0Cache[i] = Math.Max(1, (int)binLo);
                _spectrumBin1Cache[i] = Math.Min(halfN - 1, (int)Math.Ceiling(binHi));
                if (_spectrumBin1Cache[i] < _spectrumBin0Cache[i]) 
                    _spectrumBin1Cache[i] = _spectrumBin0Cache[i];
            }
            _spectrumCacheBarCount = barCount;
            _spectrumCacheFftSize = n;
        }

        // 使用缓存的频率映射计算频谱（避免每帧重复 Math.Pow/Log）
        bool hasSlope = SpectrumSlope > 0.01f || SpectrumSlope < -0.01f;
        float log2Base = (float)Math.Log(2);
        
        for (int i = 0; i < barCount; i++)
        {
            int bin0 = _spectrumBin0Cache[i];
            int bin1 = _spectrumBin1Cache[i];
            
            // 使用 RMS 计算频带能量
            float sumSq = 0f;
            int binCount = bin1 - bin0 + 1;
            for (int k = bin0; k <= bin1; k++)
            {
                sumSq += _fftMagnitudes[k] * _fftMagnitudes[k];
            }
            float rmsMag = binCount > 0 ? (float)Math.Sqrt(sumSq / binCount) : 0f;

            // 转换为 dB（输入已归一化到 [-1, 1]）
            float db = 20f * (float)Math.Log10(rmsMag + 1e-10f);
            
            // 斜率补偿
            if (hasSlope)
            {
                float octaveFromRef = (float)Math.Log(_spectrumCenterFreqCache[i] / 1000f) / log2Base;
                db += octaveFromRef * SpectrumSlope;
            }
            
            // 映射到 0~1（-60dB ~ 0dB 动态范围）
            bars[i] = Math.Clamp((db + 60f) / 60f, 0f, 1f);
        }

        // 初始化平滑缓冲区
        if (_smoothedSpectrum == null || _smoothedSpectrum.Length != barCount)
        {
            _smoothedSpectrum = new float[barCount];
            Array.Copy(bars, _smoothedSpectrum, barCount);
        }

        // 指数平滑（基于时间常数 τ）- 仅在参数变化时重新计算
        if (_cachedAttackMs != SpectrumAttackMs || _cachedReleaseMs != SpectrumReleaseMs || _cachedOutputFps != OutputFps)
        {
            float dt = 1000f / OutputFps;
            float attackTau = Math.Max(1f, SpectrumAttackMs);
            float releaseTau = Math.Max(1f, SpectrumReleaseMs);
            _cachedAttackAlpha = 1f - (float)Math.Exp(-dt / attackTau);
            _cachedReleaseAlpha = 1f - (float)Math.Exp(-dt / releaseTau);
            _cachedAttackMs = SpectrumAttackMs;
            _cachedReleaseMs = SpectrumReleaseMs;
            _cachedOutputFps = OutputFps;
        }
        float attackAlpha = _cachedAttackAlpha;
        float releaseAlpha = _cachedReleaseAlpha;
        
        for (int i = 0; i < barCount; i++)
        {
            float current = bars[i];
            float previous = _smoothedSpectrum[i];
            
            // 根据信号方向选择不同的时间常数
            if (current > previous)
            {
                // 信号上升：使用 attack（快速响应）
                _smoothedSpectrum[i] = previous + attackAlpha * (current - previous);
            }
            else
            {
                // 信号下降：使用 release（慢速衰减）
                _smoothedSpectrum[i] = previous + releaseAlpha * (current - previous);
            }
        }

        float barWidth = region.Width / barCount;
        float gap = barWidth * 0.12f;
        float actualWidth = barWidth - gap;

        // 批量绘制频谱柱
        using var barPath = new SKPath();
        
        for (int i = 0; i < barCount; i++)
        {
            float x = region.Left + i * barWidth + gap / 2f;
            float h = _smoothedSpectrum[i] * region.Height;
            if (h >= 0.5f)
            {
                float y = region.Bottom - h;
                barPath.AddRect(SKRect.Create(x, y, actualWidth, h));
            }
        }
        
        // 绘制频谱柱（白色）
        _fillPaint.Color = SKColors.White;
        _frameCanvas.DrawPath(barPath, _fillPaint);
    }
    
    // Blackman-Harris 窗口（4-term，旁瓣抑制 -92dB）
    private float[] _blackmanHarrisWindow = null;
    private void PrecomputeBlackmanHarrisWindow(int n)
    {
        if (_blackmanHarrisWindow != null && _blackmanHarrisWindow.Length == n) return;
        
        _blackmanHarrisWindow = new float[n];
        const double a0 = 0.35875;
        const double a1 = 0.48829;
        const double a2 = 0.14128;
        const double a3 = 0.01168;
        double factor = 2.0 * Math.PI / (n - 1);
        
        for (int i = 0; i < n; i++)
        {
            double t = factor * i;
            _blackmanHarrisWindow[i] = (float)(a0 - a1 * Math.Cos(t) + a2 * Math.Cos(2 * t) - a3 * Math.Cos(3 * t));
        }
    }
    
    // A-weighting 曲线（IEC 61672:2003 标准）
    // 模拟人耳对不同频率的敏感度差异
    private float ComputeAWeighting(float freq)
    {
        // A-weighting 公式的简化版本
        double f2 = freq * freq;
        double f4 = f2 * f2;
        
        // 标准 A-weighting 传递函数
        double ra = (12194.0 * 12194.0 * f4) /
                    ((f2 + 20.6 * 20.6) *
                     Math.Sqrt((f2 + 107.7 * 107.7) * (f2 + 737.9 * 737.9)) *
                     (f2 + 12194.0 * 12194.0));
        
        // 归一化到 1kHz = 0dB
        double ra1000 = (12194.0 * 12194.0 * 1e12) /
                        ((1e6 + 20.6 * 20.6) *
                         Math.Sqrt((1e6 + 107.7 * 107.7) * (1e6 + 737.9 * 737.9)) *
                         (1e6 + 12194.0 * 12194.0));
        
        // 转换为线性增益
        double aWeightDb = 20.0 * Math.Log10(ra / ra1000 + 1e-10);
        return (float)Math.Pow(10, aWeightDb / 20.0);
    }

    private string BuildSubfileDisplayLine(SubFile subfile)
    {
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

        // 曲名：优先 TrackTitle，没有则使用不带扩展名的文件名
        string title = !string.IsNullOrWhiteSpace(subfile.TrackTitle)
            ? subfile.TrackTitle
            : System.IO.Path.GetFileNameWithoutExtension(subfile.FileName);
        line += title;

        // 作曲家：优先使用 Composer，其次 Artist
        string composer = subfile.ComposerName ?? subfile.ArtistName;
        if (!string.IsNullOrWhiteSpace(composer))
        {
            line += " // " + composer;
        }

        // 风格：存在时添加 " [Genre]"
        if (!string.IsNullOrWhiteSpace(subfile.Genre))
        {
            line += " [" + subfile.Genre + "]";
        }
        
        // 始终在最后添加文件后缀名标识
        if (!string.IsNullOrEmpty(subfile.FileName))
        {
            string ext = System.IO.Path.GetExtension(subfile.FileName);
            if (!string.IsNullOrEmpty(ext))
            {
                line += $" ({ext.TrimStart('.').ToUpperInvariant()})";
            }
        }

        return line;
    }
    
    // 绘制钢琴卷帘可视化
    private void DrawPianoRoll(SKRect region, double currentTimeMs, SubFile currentSubfile = null)
    {
        if (_currentMidiVisualizer == null) return;
        
        // 时间窗口
        double windowMs = PianoRollWindowMs;
        
        // 获取音符范围
        int minNote = _currentMidiVisualizer.MinNoteNumber;
        int maxNote = _currentMidiVisualizer.MaxNoteNumber;
        int noteRange = maxNote - minNote + 1;
        if (noteRange <= 0) return;
        
        // 垂直布局：X 轴是音高，Y 轴是时间
        float noteWidth = region.Width / noteRange;
        float msPerPixel = (float)(windowMs / region.Height);
        
        // 播放头始终固定在中间
        float playheadY = region.MidY;
        
        // 根据 MIDI 元数据计算小节时长
        double msPerBar = 2000.0;  // 默认值：120 BPM, 4/4 拍
        if (currentSubfile?.MidiMetadata != null)
        {
            // 使用 MIDI 文件的实际 Tempo 和拍号
            msPerBar = currentSubfile.MidiMetadata.GetBarDurationMs(currentTimeMs);
        }
        _pianoRollGridPaint.Color = new SKColor(60, 60, 60);
        _pianoRollGridPaint.StrokeWidth = 1;
        
        double halfWindowMs = windowMs / 2.0;
        double visibleStartMs = currentTimeMs - halfWindowMs;
        double visibleEndMs = currentTimeMs + halfWindowMs;
        
        double firstBarMs = Math.Floor(visibleStartMs / msPerBar) * msPerBar;
        for (double barMs = firstBarMs; barMs <= visibleEndMs; barMs += msPerBar)
        {
            float y = playheadY - (float)(barMs - currentTimeMs) / msPerPixel;
            if (y >= region.Top && y <= region.Bottom)
            {
                _frameCanvas.DrawLine(region.Left, y, region.Right, y, _pianoRollGridPaint);
            }
        }
        
        // 获取可见音符
        _currentMidiVisualizer.GetVisibleNotes(currentTimeMs, windowMs, _visibleNotesBuffer);
        
        // 按通道分组批量绘制音符
        using var notePath = new SKPath();
        using var activePath = new SKPath();
        
        // 预计算所有音符的矩形
        int lastChannel = -1;
        SKColor lastColor = SKColors.Transparent;
        float halfWidth = noteWidth * 0.4f;
        
        foreach (var note in _visibleNotesBuffer)
        {
            // 跳过打击乐通道（在底部单独显示）
            if (_currentMidiVisualizer.IsDrumChannel(note.Channel)) continue;
            
            float noteX = region.Left + (note.NoteNumber - minNote + 0.5f) * noteWidth;
            
            // Y 坐标
            float noteStartY = playheadY - (float)(note.StartMs - currentTimeMs) / msPerPixel;
            float noteEndY = playheadY - (float)(note.EndMs - currentTimeMs) / msPerPixel;
            if (noteStartY > noteEndY) (noteStartY, noteEndY) = (noteEndY, noteStartY);
            
            float noteHeight = Math.Max(3, noteEndY - noteStartY);
            var noteRect = new SKRect(noteX - halfWidth, noteStartY, noteX + halfWidth, noteStartY + noteHeight);
            
            // 颜色变化时，先绘制之前的批次
            var (r, g, b) = MidiVisualizer.GetChannelColor(note.Channel);
            float velocityScale = 0.5f + (note.Velocity / 127f) * 0.5f;
            byte alpha = (byte)(220 * velocityScale);
            var noteColor = new SKColor((byte)(r * velocityScale), (byte)(g * velocityScale), (byte)(b * velocityScale), alpha);
            
            if (note.Channel != lastChannel && notePath.PointCount > 0)
            {
                _pianoRollNotePaint.Color = lastColor;
                _frameCanvas.DrawPath(notePath, _pianoRollNotePaint);
                notePath.Reset();
            }
            
            notePath.AddRect(noteRect);
            lastChannel = note.Channel;
            lastColor = noteColor;
            
            // 正在播放的音符
            if (note.StartMs <= currentTimeMs && note.EndMs >= currentTimeMs)
            {
                activePath.AddRect(noteRect);
            }
        }
        
        // 绘制最后一批音符
        if (notePath.PointCount > 0)
        {
            _pianoRollNotePaint.Color = lastColor;
            _frameCanvas.DrawPath(notePath, _pianoRollNotePaint);
        }
        
        // 绘制活动音符高亮
        if (activePath.PointCount > 0)
        {
            _strokePaint.Color = SKColors.White;
            _strokePaint.StrokeWidth = 1.5f;
            _frameCanvas.DrawPath(activePath, _strokePaint);
        }
        
        // 播放指示器 "▶" 已移至主绘制循环，统一由 _visualizerTransition 管理位置
        
        // 绘制底部打击乐器显示区域（在遮罩之前）
        DrawDrumIndicators(region, currentTimeMs);
        
        // 渐变遮罩
        float fadeHeight = region.Height * 0.15f;
        
        using (var topShader = SKShader.CreateLinearGradient(
            new SKPoint(region.Left, region.Top),
            new SKPoint(region.Left, region.Top + fadeHeight),
            new[] { new SKColor(16, 16, 16, 255), new SKColor(16, 16, 16, 0) },
            SKShaderTileMode.Clamp))
        {
            _gradientPaint.Shader = topShader;
            _frameCanvas.DrawRect(new SKRect(region.Left, region.Top, region.Right, region.Top + fadeHeight), _gradientPaint);
        }
        
        using (var bottomShader = SKShader.CreateLinearGradient(
            new SKPoint(region.Left, region.Bottom - fadeHeight),
            new SKPoint(region.Left, region.Bottom),
            new[] { new SKColor(16, 16, 16, 0), new SKColor(16, 16, 16, 255) },
            SKShaderTileMode.Clamp))
        {
            _gradientPaint.Shader = bottomShader;
            _frameCanvas.DrawRect(new SKRect(region.Left, region.Bottom - fadeHeight, region.Right, region.Bottom), _gradientPaint);
        }
        
        _gradientPaint.Shader = null;
    }
    
    // 绘制打击乐器指示器（底部居中，支持多行）
    private void DrawDrumIndicators(SKRect region, double currentTimeMs)
    {
        if (_currentMidiVisualizer == null) return;
        
        // 获取当前 MIDI 使用的打击乐音符（缓存）
        var usedDrums = _currentMidiVisualizer.GetUsedDrumNotes();
        if (usedDrums.Count == 0) return;
        
        // 更新打击乐音符列表（按音符编号排序）
        if (_currentDrumNotes == null || _currentDrumNotes.Length != usedDrums.Count)
        {
            _currentDrumNotes = usedDrums.OrderBy(n => n).ToArray();
        }
        
        // 更新打击乐触发状态
        _currentMidiVisualizer.GetActiveDrumNotes(currentTimeMs, DrumTriggerWindowMs, _activeDrumNotes);
        foreach (var (noteNumber, velocity, triggerTime) in _activeDrumNotes)
        {
            if (!_drumTriggerTimes.TryGetValue(noteNumber, out double lastTime) || triggerTime > lastTime)
            {
                _drumTriggerTimes[noteNumber] = triggerTime;
                _drumVelocities[noteNumber] = velocity;
            }
        }
        
        // 打击乐器显示区域参数
        float scale = OutputVideoWidth / 1920f;
        float drumSize = 28 * scale;
        float drumSpacing = 4 * scale;
        float velocityBarWidth = 3 * scale;
        float rowSpacing = 6 * scale;
        
        // 计算每行最多能放多少个
        float itemWidth = drumSize + drumSpacing + velocityBarWidth;
        float maxRowWidth = region.Width * 0.9f;
        int maxPerRow = Math.Max(1, (int)((maxRowWidth + drumSpacing) / itemWidth));
        
        // 计算行数
        int drumCount = _currentDrumNotes.Length;
        int rowCount = (drumCount + maxPerRow - 1) / maxPerRow;
        rowCount = Math.Min(rowCount, 2);  // 最多两行
        
        // 计算底部位置（在淡出遮罩上方）
        float totalHeight = rowCount * drumSize + (rowCount - 1) * rowSpacing;
        float bottomMargin = region.Height * 0.17f;
        float baseY = region.Bottom - bottomMargin - totalHeight / 2f;
        
        int drumIndex = 0;
        for (int row = 0; row < rowCount && drumIndex < drumCount; row++)
        {
            // 计算这一行有多少个
            int itemsInRow = Math.Min(maxPerRow, drumCount - drumIndex);
            float rowWidth = itemsInRow * itemWidth - drumSpacing;
            float startX = region.MidX - rowWidth / 2f;
            float drumY = baseY + row * (drumSize + rowSpacing);
            
            for (int i = 0; i < itemsInRow && drumIndex < drumCount; i++, drumIndex++)
            {
                int noteNumber = _currentDrumNotes[drumIndex];
                float currentX = startX + i * itemWidth;
                
                // 计算动画进度（打击效果）
                float hitProgress = 0f;
                float velocityProgress = 0f;
                int velocity = 64;
                
                if (_drumTriggerTimes.TryGetValue(noteNumber, out double triggerTime))
                {
                    double elapsed = currentTimeMs - triggerTime;
                    
                    // 打击动画（较快衰减）
                    if (elapsed >= 0 && elapsed < DrumAnimDurationMs)
                    {
                        hitProgress = 1f - (float)(elapsed / DrumAnimDurationMs);
                        hitProgress = hitProgress * hitProgress;  // 缓动
                    }
                    
                    // 力度条动画（更缓慢衰减）
                    if (elapsed >= 0 && elapsed < DrumVelocityDecayMs)
                    {
                        velocityProgress = 1f - (float)(elapsed / DrumVelocityDecayMs);
                        velocityProgress = MathF.Sqrt(velocityProgress);  // 缓慢衰减
                    }
                    
                    if (_drumVelocities.TryGetValue(noteNumber, out int v)) velocity = v;
                }
                
                // 打击感缩放效果
                float hitScale = 1f + hitProgress * 0.2f;
                float scaledSize = drumSize * hitScale;
                float offsetX = (scaledSize - drumSize) / 2f;
                
                // 方框位置
                var boxRect = new SKRect(
                    currentX - offsetX,
                    drumY - offsetX,
                    currentX + scaledSize - offsetX,
                    drumY + scaledSize - offsetX
                );
                
                // 默认白色空心框，触发时变成白色实心
                if (hitProgress > 0.1f)
                {
                    // 触发时：实心白色方块
                    byte fillAlpha = (byte)(255 * hitProgress);
                    _fillPaint.Color = new SKColor(255, 255, 255, fillAlpha);
                    _frameCanvas.DrawRect(boxRect, _fillPaint);
                }
                
                // 白色边框（始终显示）
                _strokePaint.Color = SKColors.White;
                _strokePaint.StrokeWidth = 1.2f * scale;
                _frameCanvas.DrawRect(boxRect, _strokePaint);
                
                // 打击乐器简称（在方框内）
                string label = MidiVisualizer.GetDrumShortName(noteNumber);
                float fontSize = drumSize * 0.38f;
                // 触发时文字变黑，否则白色
                var textColor = hitProgress > 0.3f ? SKColors.Black : SKColors.White;
                DrawText(currentX + drumSize / 2f, drumY + drumSize / 2f, fontSize, label,
                    textColor, VerticalAlign.Center, HorizontalAlign.Center);
                
                // 力度指示条（右侧，更缓和的衰减）
                float barX = currentX + drumSize + 1.5f * scale;
                float velocityRatio = velocity / 127f;
                float barHeight = drumSize * velocityRatio * velocityProgress;
                float barY = drumY + drumSize - barHeight;
                
                if (barHeight > 0.5f)
                {
                    byte barAlpha = (byte)(200 * velocityProgress);
                    _fillPaint.Color = new SKColor(255, 255, 255, barAlpha);
                    _frameCanvas.DrawRect(new SKRect(barX, barY, barX + velocityBarWidth, drumY + drumSize), _fillPaint);
                }
            }
        }
    }
    #endregion
}