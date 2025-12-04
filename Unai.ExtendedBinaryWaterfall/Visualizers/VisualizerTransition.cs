using System;

namespace Unai.ExtendedBinaryWaterfall;

// 可视化模式枚举
public enum VisualizerMode
{
    BinaryWaterfall,  // 二进制瀑布（默认）
    PianoRoll,        // 钢琴卷帘（MIDI）
    VgmView           // VGM 芯片可视化
}

// 可视化模式切换动画控制器
public class VisualizerTransition
{
    private VisualizerMode _currentMode = VisualizerMode.BinaryWaterfall;
    private VisualizerMode _targetMode = VisualizerMode.BinaryWaterfall;
    private float _transitionProgress = 1f;  // 0 = 开始过渡, 1 = 完成
    private double _labelTimer = 0;          // 标签显示计时器
    private bool _isTransitioning = false;
    
    // 播放头 Y 位置动画相关
    private float _playheadCurrentY = 0f;    // 当前播放头 Y 位置
    private float _playheadTargetY = 0f;     // 目标播放头 Y 位置
    private float _playheadStartY = 0f;      // 动画开始时的 Y 位置（固定值，避免飘动）
    private float _playheadAnimProgress = 1f; // 播放头动画进度（0=开始, 1=完成）
    private bool _playheadAnimating = false;  // 是否正在动画中
    private float _playheadWaterfallY = 0f;   // 瀑布模式下的实际 Y 位置（持续更新）
    private float _playheadPianoRollY = 0f;   // 钢琴窗模式下的固定 Y 位置（居中）

    // 动画参数
    public float TransitionDuration { get; set; } = 0.5f;    // 过渡动画时长（秒）
    public float LabelFadeInDuration { get; set; } = 0.3f;   // 标签淡入时长
    public float LabelHoldDuration { get; set; } = 2.0f;     // 标签保持时长
    public float LabelFadeOutDuration { get; set; } = 0.5f;  // 标签淡出时长
    public float PlayheadAnimDuration { get; set; } = 0.35f; // 播放头滑动动画时长（秒）

    // 当前模式
    public VisualizerMode CurrentMode => _currentMode;
    
    // 目标模式
    public VisualizerMode TargetMode => _targetMode;
    
    // 是否正在过渡
    public bool IsTransitioning => _isTransitioning;

    // 缓动函数：快→慢（EaseOutCubic）
    private static float EaseOutCubic(float t)
    {
        return 1f - MathF.Pow(1f - t, 3f);
    }

    // 设置初始模式（无动画，用于程序启动时）
    public void SetInitialMode(VisualizerMode mode)
    {
        _currentMode = mode;
        _targetMode = mode;
        _transitionProgress = 1f;
        _isTransitioning = false;
        // 不显示标签（跳过整个标签动画）
        _labelTimer = LabelFadeInDuration + LabelHoldDuration + LabelFadeOutDuration + 1f;
    }
    
    // 切换到新模式（带动画）
    public void SwitchTo(VisualizerMode newMode)
    {
        if (newMode != _targetMode)
        {
            _targetMode = newMode;
            _transitionProgress = 0f;
            _labelTimer = 0;
            _isTransitioning = true;
            
            // 启动播放头动画
            _playheadAnimProgress = 0f;
            _playheadAnimating = true;
            
            // 保存动画起始位置（固定值，避免动画过程中飘动）
            _playheadStartY = (newMode == VisualizerMode.PianoRoll) 
                ? _playheadWaterfallY   // 从瀑布位置开始
                : _playheadPianoRollY;  // 从钢琴窗位置开始
            
            // 目标位置：钢琴窗模式用居中位置，瀑布模式用当前瀑布位置
            _playheadTargetY = (newMode == VisualizerMode.PianoRoll) 
                ? _playheadPianoRollY 
                : _playheadWaterfallY;
        }
    }

    // 更新动画状态（每帧调用）
    public void Update(float deltaSeconds)
    {
        // 过渡动画
        if (_transitionProgress < 1f)
        {
            _transitionProgress += deltaSeconds / TransitionDuration;
            if (_transitionProgress >= 1f)
            {
                _transitionProgress = 1f;
                _currentMode = _targetMode;
                _isTransitioning = false;
            }
        }

        // 标签计时器（在过渡开始后持续计时）
        if (_labelTimer < LabelFadeInDuration + LabelHoldDuration + LabelFadeOutDuration + 1f)
        {
            _labelTimer += deltaSeconds;
        }
        
        // 播放头动画
        if (_playheadAnimating)
        {
            _playheadAnimProgress += deltaSeconds / PlayheadAnimDuration;
            if (_playheadAnimProgress >= 1f)
            {
                _playheadAnimProgress = 1f;
                _playheadAnimating = false;
                _playheadCurrentY = _playheadTargetY;
            }
            else
            {
                // 使用 EaseOutCubic 缓动，从固定的起始位置滑动（避免飘动）
                float eased = EaseOutCubic(_playheadAnimProgress);
                _playheadCurrentY = _playheadStartY + (_playheadTargetY - _playheadStartY) * eased;
            }
        }
        else
        {
            // 非动画状态：根据当前模式决定位置
            if (_currentMode == VisualizerMode.BinaryWaterfall)
            {
                // 瀑布模式：跟随瀑布位置
                _playheadCurrentY = _playheadWaterfallY;
            }
            else
            {
                // 钢琴窗模式：固定在居中位置
                _playheadCurrentY = _playheadPianoRollY;
            }
        }
    }

    // 计算滑动偏移量（像素）
    // 返回：(瀑布偏移, 钢琴卷帘偏移)
    public (float waterfallOffset, float pianoRollOffset) GetOffsets(float panelWidth)
    {
        float eased = EaseOutCubic(_transitionProgress);

        if (_targetMode == VisualizerMode.PianoRoll)
        {
            // 瀑布向左滑出，钢琴卷帘从右滑入
            return (
                waterfallOffset: -panelWidth * eased,
                pianoRollOffset: panelWidth * (1f - eased)
            );
        }
        else
        {
            // 钢琴卷帘向右滑出，瀑布从左滑入
            return (
                waterfallOffset: -panelWidth * (1f - eased),
                pianoRollOffset: panelWidth * eased
            );
        }
    }

    // 获取标签状态
    // 返回：(标签文本, 透明度 0-1)
    public (string label, float alpha) GetLabelState()
    {
        string label = _targetMode switch
        {
            VisualizerMode.BinaryWaterfall => "Binary Waterfall",
            VisualizerMode.PianoRoll => "Piano Roll",
            VisualizerMode.VgmView => "VGM View",
            _ => ""
        };

        float alpha;
        float totalFadeTime = LabelFadeInDuration + LabelHoldDuration + LabelFadeOutDuration;

        if (_labelTimer < LabelFadeInDuration)
        {
            // 淡入阶段
            alpha = (float)(_labelTimer / LabelFadeInDuration);
        }
        else if (_labelTimer < LabelFadeInDuration + LabelHoldDuration)
        {
            // 保持阶段
            alpha = 1f;
        }
        else if (_labelTimer < totalFadeTime)
        {
            // 淡出阶段
            float fadeOutProgress = (float)((_labelTimer - LabelFadeInDuration - LabelHoldDuration) / LabelFadeOutDuration);
            alpha = 1f - fadeOutProgress;
        }
        else
        {
            // 完全透明
            alpha = 0f;
        }

        return (label, Math.Clamp(alpha, 0f, 1f));
    }

    // 根据文件扩展名判断应使用的可视化模式
    public static VisualizerMode GetModeForFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) 
            return VisualizerMode.BinaryWaterfall;

        string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
        
        return ext switch
        {
            ".mid" => VisualizerMode.PianoRoll,
            ".midi" => VisualizerMode.PianoRoll,
            ".vgm" => VisualizerMode.VgmView,
            ".vgz" => VisualizerMode.VgmView,
            _ => VisualizerMode.BinaryWaterfall
        };
    }
    
    // 更新瀑布模式下的播放头 Y 位置（每帧调用）
    public void SetWaterfallPlayheadY(float y)
    {
        _playheadWaterfallY = y;
        // 如果正在从钢琴窗切换到瀑布，更新目标位置
        if (_playheadAnimating && _targetMode == VisualizerMode.BinaryWaterfall)
        {
            _playheadTargetY = y;
        }
    }
    
    // 设置钢琴窗模式下的播放头 Y 位置（固定居中位置）
    public void SetPianoRollPlayheadY(float y)
    {
        _playheadPianoRollY = y;
    }
    
    // 获取当前播放头 Y 位置（用于统一绘制）
    public float GetPlayheadY()
    {
        return _playheadCurrentY;
    }
    
    // 初始化播放头位置（程序启动时调用）
    public void InitializePlayheadY(float waterfallY, float pianoRollY)
    {
        _playheadWaterfallY = waterfallY;
        _playheadPianoRollY = pianoRollY;
        _playheadCurrentY = (_currentMode == VisualizerMode.BinaryWaterfall) ? waterfallY : pianoRollY;
        _playheadTargetY = _playheadCurrentY;
    }
}
