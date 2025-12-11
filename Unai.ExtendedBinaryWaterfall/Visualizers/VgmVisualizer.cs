using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Unai.ExtendedBinaryWaterfall;

// VGM 可视化器
public class VgmVisualizer : IDisposable
{
    // 芯片通道状态
    public class ChannelState
    {
        public int Note;              // 当前音符 (0-127, -1 表示无声)
        public int Volume;            // 当前音量 (0-127)
        public int Panning;           // 声像 (-64 ~ 63，0=居中)
        public int PanLeft;           // 前左声道音量 (0-255)
        public int PanRight;          // 前右声道音量 (0-255)
        public int RearLeft;          // 后左声道音量 (0-255, C352等四声道芯片)
        public int RearRight;         // 后右声道音量 (0-255)
        public float SmoothedPanLeft; // 平滑后的左声道输入值 (减少输入抖动)
        public float SmoothedPanRight;// 平滑后的右声道输入值
        public float SmoothedRearLeft;// 平滑后的后左声道
        public float SmoothedRearRight;//平滑后的后右声道
        public float DisplayPanLeft;  // 显示用前左声道 (带衰减)
        public float DisplayPanRight; // 显示用前右声道 (带衰减)
        public float DisplayRearLeft; // 显示用后左声道 (带衰减)
        public float DisplayRearRight;// 显示用后右声道 (带衰减)
        public float PeakPanLeft;     // 峰值保持前左 (避免跳动)
        public float PeakPanRight;    // 峰值保持前右
        public float PeakRearLeft;    // 峰值保持后左
        public float PeakRearRight;   // 峰值保持后右
        public bool HasQuadChannel;   // 是否是四声道 (C352)
        public bool KeyOn;            // 是否按下
        public bool PrevKeyOn;        // 上一帧的 KeyOn 状态（用于检测触发）
        public float AttackFlash;     // 打击闪光强度 (0-1)，触发时为1，快速衰减
        public string Label;          // 通道标签
        public int Detune;            // Detune 音高偏移值 (有符号)
    }

    // 芯片状态
    public class ChipState
    {
        public VgmChipInfo Info;
        public ChannelState[] Channels;
        public byte[] Registers;
    }
    
    // 音符历史记录（用于 Piano Roll 视图）
    public class NoteHistoryEntry
    {
        public int Note;       // 音符 (0-127)
        public int Volume;     // 音量 (0-127)
        public int Detune;     // Detune 值
        public int Channel;    // 通道号
        public double StartTime; // 开始时间 (ms)
        public double Duration;  // 持续时间 (ms)，0 表示仍在播放
    }
    
    // MultiPCM 专用历史记录
    private readonly List<NoteHistoryEntry> _multiPcmHistory = new();
    private const int MAX_HISTORY_ENTRIES = 500;  // 最大历史记录数
    private const double HISTORY_WINDOW_MS = 5000; // 历史窗口时长 (ms)

    private readonly List<ChipState> _chipStates = new();
    private readonly Dictionary<byte, VgmChipTracker> _trackers = new();
    private VgmCommandParser _parser;
    private byte[] _vgmData;
    private int _lastEventIndex = -1;
    private bool _disposed;
    private double _lastUpdateTime;
    
    // 保存 VgmAudioSource 的数据副本（避免持有引用导致悬空指针）
    private VgmHeader _header;
    private Gd3Tag _gd3;
    private VgmChipInfo[] _chipList;
    private bool _hasLoop;
    private int _loopCount = 2;       // 用户设置的循环次数
    private bool _fadeOutEnabled;     // 是否启用淡出
    private double _fadeOutDuration;  // 淡出时长（秒）
    
    // 缓存的循环点计算值（避免每帧重复计算）
    private double _cachedLoopPointMs;
    private double _cachedLoopLengthMs;
    private double _cachedTotalMs;         // VGM 文件原始时长（毫秒）
    private double _cachedPlayDurationMs;  // 实际播放总时长（考虑循环+淡出）
    private bool _loopCacheValid;
    
    // 芯片类型查找表（避免每帧调用 GetChipTypeForName）
    private byte[] _chipTypeLookup;
    
    // 钢琴键盘自适应偏移和缩放
    private float _pianoOffset;           // 当前偏移量（八度数）
    private float _pianoOffsetTarget;     // 目标偏移量
    private float _pianoDisplayOctaves;   // 当前显示的八度数（用于缩放）
    private float _pianoDisplayOctavesTarget; // 目标显示八度数
    private const float PIANO_SLIDE_SPEED = 6f;   // 滑动速度（较慢，约500ms完成）
    private const float PIANO_ZOOM_SPEED = 4f;    // 缩放速度（更慢，约750ms完成）
    
    // 滑动稳定性控制
    private float _lastSlideTime;         // 上次触发滑动的时间累计
    private int _stableMinNote = -1;      // 稳定的最小音符（用于滞后）
    private int _stableMaxNote = -1;      // 稳定的最大音符
    private float _noteStableTimer;       // 音符稳定计时器
    private const float NOTE_STABLE_DELAY = 0.15f; // 音符变化后稳定延迟（秒）
    private const float SLIDE_COOLDOWN = 0.3f;     // 滑动冷却时间（秒）
    
    // 钢琴偏移和缩放属性
    public float PianoOffset => _pianoOffset;
    public float PianoDisplayOctaves => _pianoDisplayOctaves;
    
    // 更新钢琴偏移和缩放（根据活跃音符范围自适应）
    // baseDisplayOctaves: 基础显示八度数
    // totalOctaves: 总八度数（默认11个，o0-o10）
    // defaultOffsetOctave: 默认起始八度
    public void UpdatePianoOffset(int minActiveNote, int maxActiveNote, int baseDisplayOctaves, float deltaTime, int totalOctaves = 11, int defaultOffsetOctave = 1)
    {
        // 初始化显示八度数和默认偏移
        if (_pianoDisplayOctaves == 0)
        {
            _pianoDisplayOctaves = baseDisplayOctaves;
            _pianoDisplayOctavesTarget = baseDisplayOctaves;
            _pianoOffset = defaultOffsetOctave;
            _pianoOffsetTarget = defaultOffsetOctave;
        }
        
        // 累计滑动冷却时间
        _lastSlideTime += deltaTime;
        _noteStableTimer += deltaTime;
        
        // 当前显示范围（半音）
        float displayMin = _pianoOffset * 12;
        float displayMax = displayMin + _pianoDisplayOctaves * 12;
        
        // 边距定义
        int hardEdge = 3;     // 硬边缘（超出必须立即滑动）
        int softEdge = 12;    // 软边缘（超出时触发居中，但有滞后）
        int centerMargin = 18; // 居中后的安全边距
        
        if (minActiveNote >= 0 && maxActiveNote >= 0)
        {
            // 检测音符范围是否有显著变化（添加滞后避免抖动）
            if (_stableMinNote < 0 || _stableMaxNote < 0)
            {
                _stableMinNote = minActiveNote;
                _stableMaxNote = maxActiveNote;
                _noteStableTimer = 0;
            }
            else
            {
                // 只有超出当前稳定范围时才更新
                if (minActiveNote < _stableMinNote || maxActiveNote > _stableMaxNote)
                {
                    _stableMinNote = Math.Min(_stableMinNote, minActiveNote);
                    _stableMaxNote = Math.Max(_stableMaxNote, maxActiveNote);
                    _noteStableTimer = 0;
                }
                // 音符范围收缩时，延迟更新（避免快速变化）
                else if (_noteStableTimer > NOTE_STABLE_DELAY * 3)
                {
                    // 缓慢收缩稳定范围
                    if (minActiveNote > _stableMinNote) _stableMinNote++;
                    if (maxActiveNote < _stableMaxNote) _stableMaxNote--;
                }
            }
            
            int noteRange = _stableMaxNote - _stableMinNote;
            int requiredRange = noteRange + centerMargin * 2;
            int requiredOctaves = (int)Math.Ceiling(requiredRange / 12f);
            
            // 扩展：立即响应
            if (requiredOctaves > _pianoDisplayOctavesTarget)
            {
                _pianoDisplayOctavesTarget = Math.Clamp(requiredOctaves, baseDisplayOctaves, totalOctaves);
            }
            // 收缩：需要较大差距且稳定后才收缩
            else if (requiredOctaves < _pianoDisplayOctavesTarget - 2 && _noteStableTimer > NOTE_STABLE_DELAY * 5)
            {
                _pianoDisplayOctavesTarget = Math.Clamp(requiredOctaves + 2, baseDisplayOctaves, totalOctaves);
            }
            
            // 检测是否需要滑动
            bool needsSlide = false;
            
            // 硬边缘检测：超出必须滑动
            if (_stableMinNote < displayMin + hardEdge || _stableMaxNote > displayMax - hardEdge)
            {
                needsSlide = true;
            }
            // 软边缘检测：超出且冷却时间已过
            else if (_lastSlideTime > SLIDE_COOLDOWN)
            {
                if (_stableMinNote < displayMin + softEdge || _stableMaxNote > displayMax - softEdge)
                {
                    needsSlide = true;
                }
            }
            
            if (needsSlide)
            {
                // 计算目标偏移（使活跃音符居中）
                int centerNote = (_stableMinNote + _stableMaxNote) / 2;
                float displayRange = _pianoDisplayOctavesTarget * 12;
                float targetOffset = (centerNote - displayRange / 2) / 12f;
                
                // 限制偏移范围
                float maxOffset = Math.Max(0f, totalOctaves - _pianoDisplayOctavesTarget);
                float newTarget = Math.Clamp(targetOffset, 0f, maxOffset);
                
                // 只有目标变化足够大时才更新（避免微小抖动）
                if (Math.Abs(newTarget - _pianoOffsetTarget) > 0.3f)
                {
                    _pianoOffsetTarget = newTarget;
                    _lastSlideTime = 0; // 重置冷却
                }
            }
        }
        else
        {
            // 无活跃音符时，延迟恢复默认
            if (_noteStableTimer > NOTE_STABLE_DELAY * 10)
            {
                _pianoDisplayOctavesTarget = baseDisplayOctaves;
                _stableMinNote = -1;
                _stableMaxNote = -1;
            }
        }
        
        // 平滑过渡偏移（使用非线性插值，避免抖动）
        float offsetDiff = _pianoOffsetTarget - _pianoOffset;
        if (Math.Abs(offsetDiff) > 0.005f)
        {
            // 差距大时快速移动，差距小时慢速（指数衰减）
            float speed = PIANO_SLIDE_SPEED * (0.3f + Math.Abs(offsetDiff) * 0.7f);
            _pianoOffset += offsetDiff * Math.Min(1f, speed * deltaTime);
        }
        else
        {
            _pianoOffset = _pianoOffsetTarget;
        }
        
        // 平滑过渡缩放（更慢的速度）
        float scaleDiff = _pianoDisplayOctavesTarget - _pianoDisplayOctaves;
        if (Math.Abs(scaleDiff) > 0.005f)
        {
            float speed = PIANO_ZOOM_SPEED * (0.2f + Math.Abs(scaleDiff) * 0.3f);
            _pianoDisplayOctaves += scaleDiff * Math.Min(1f, speed * deltaTime);
        }
        else
        {
            _pianoDisplayOctaves = _pianoDisplayOctavesTarget;
        }
        
        // 限制范围
        float currentMaxOffset = Math.Max(0f, totalOctaves - _pianoDisplayOctaves);
        _pianoOffset = Math.Clamp(_pianoOffset, 0f, currentMaxOffset);
        _pianoDisplayOctaves = Math.Clamp(_pianoDisplayOctaves, baseDisplayOctaves, totalOctaves);
    }
    
    // 缓存的芯片列表字符串（避免每帧创建新字符串）
    private string _cachedChipsString;
    private string _cachedVersionString;
    
    // 声像平滑设置（毫秒）
    // 平衡抖动抑制和冲击感
    private const float PAN_RELEASE_MS = 120f;   // 释放时间（保持冲击感）
    private const float PAN_ATTACK_MS = 15f;     // 上升时间（快速响应）
    private const float PEAK_DECAY_MS = 150f;    // 峰值衰减时间（较大值抑制抖动）

    public IReadOnlyList<ChipState> ChipStates => _chipStates;
    public VgmHeader Header => _header;
    public Gd3Tag Gd3 => _gd3;
    public string SystemName { get; private set; } = "";
    public Gd3Language Gd3Language { get; set; } = Gd3Language.English;
    
    // 获取 VGM 版本字符串（BCD格式解析，带缓存）
    public string GetVersionString()
    {
        if (_cachedVersionString != null) return _cachedVersionString;
        uint ver = Header.Version;
        // VGM版本号是BCD格式：0x161 = 1.61
        int major = (int)(ver >> 8);
        int minor = (int)(ver & 0xFF);
        _cachedVersionString = $"{major}.{minor:X2}";
        return _cachedVersionString;
    }
    
    // 获取芯片列表字符串（带缓存，避免每帧创建新字符串）
    public string GetChipsString()
    {
        if (_cachedChipsString != null) return _cachedChipsString;
        if (_chipStates.Count == 0) return "";
        
        // 使用 Span 和 stackalloc 避免临时数组分配
        var names = new string[_chipStates.Count];
        for (int i = 0; i < _chipStates.Count; i++)
        {
            names[i] = _chipStates[i].Info.Name;
        }
        _cachedChipsString = string.Join(", ", names);
        return _cachedChipsString;
    }

    // 初始化可视化器（保存数据副本，避免持有 VgmAudioSource 引用）
    public void Initialize(VgmAudioSource audioSource)
    {
        _chipStates.Clear();
        _trackers.Clear();
        _lastEventIndex = -1;
        _loopCacheValid = false;
        
        // 清除缓存
        _cachedChipsString = null;
        _cachedVersionString = null;
        _cachedLoopPointMs = 0;
        _cachedLoopLengthMs = 0;
        _cachedTotalMs = 0;
        _cachedPlayDurationMs = 0;
        
        if (audioSource == null) return;
        
        // 保存数据副本
        _header = audioSource.Header;
        _gd3 = audioSource.Gd3;
        _chipList = audioSource.ChipList;
        _hasLoop = audioSource.HasLoop;
        _loopCount = audioSource.LoopCount;
        _fadeOutEnabled = audioSource.FadeOutEnabled;
        _fadeOutDuration = audioSource.FadeOutDuration;

        if (_chipList == null) return;

        // 根据芯片列表初始化状态和追踪器
        foreach (var chip in _chipList)
        {
            var state = new ChipState
            {
                Info = chip,
                Channels = new ChannelState[chip.ChannelCount],
                Registers = new byte[256]
            };

            for (int i = 0; i < chip.ChannelCount; i++)
            {
                state.Channels[i] = new ChannelState
                {
                    Note = -1,
                    Volume = 0,
                    Panning = 0,
                    KeyOn = false,
                    Label = GetChannelLabel(chip.Name, i)
                };
            }

            _chipStates.Add(state);
        }
        
        // 构建芯片类型查找表（避免每帧调用 GetChipTypeForName）
        // 支持双芯片：第二个芯片的类型 = 基础类型 + 0x80
        _chipTypeLookup = new byte[_chipStates.Count];
        for (int i = 0; i < _chipStates.Count; i++)
        {
            string name = _chipStates[i].Info.Name ?? "";
            bool isSecondChip = name.Length > 3 && name.EndsWith(" #2");
            if (isSecondChip)
                name = name.Substring(0, name.Length - 3); // 移除 " #2" 后缀
            byte baseType = GetChipTypeForName(name);
            _chipTypeLookup[i] = isSecondChip ? (byte)(baseType | 0x80) : baseType;
        }
        
        // 创建对应的追踪器（支持双芯片）
        foreach (var chip in _chipList)
        {
            // 处理双芯片：移除 " #2" 后缀获取基础芯片名
            string baseName = chip.Name ?? "";
            bool isSecondChip = baseName.Length > 3 && baseName.EndsWith(" #2");
            if (isSecondChip)
                baseName = baseName.Substring(0, baseName.Length - 3);
            
            VgmChipTracker tracker = baseName switch
            {
                "YM2612" => new YM2612Tracker(),
                "SN76489" => new SN76489Tracker(),
                "AY-3-8910" => new AY8910Tracker(),
                "YM2151" => new YM2151Tracker(),
                "YM2413" => new YM2413Tracker(),
                "YM2203" => new YM2203Tracker(),
                "YM2608" => new YM2608Tracker(),
                "YM2610" or "YM2610B" => new YM2610Tracker(),
                "NES APU" => new NesApuTracker(),
                "GB DMG" => new GbDmgTracker(),
                "HuC6280" => new HuC6280Tracker(),
                "YM3812" or "YM3526" or "Y8950" or "YMF262" => new OplTracker(),
                "QSound" => new QSoundTracker(),
                "K051649" => new K051649Tracker(),
                "POKEY" => new PokeyTracker(),
                "SAA1099" => new SAA1099Tracker(),
                "RF5C68" or "RF5C164" => new RF5CTracker(),
                "C140" => new C140Tracker(),
                "C352" => new C352Tracker(),
                "K053260" => new K053260Tracker(),
                  "K054539" => new K054539Tracker2(),
                "MultiPCM" => new MultiPCMTracker(),
                "SCSP" => new ScspTracker(),
                "WonderSwan" => new WSwanTracker(),
                "X1-010" => new X1010Tracker(),
                "OKIM6295" => new OKIM6295Tracker(),
                "OKIM6258" => new OKIM6258Tracker(),
                "uPD7759" => new GenericPcmTracker(),
                "SegaPCM" => new SegaPCMTracker(),
                "YMZ280B" => new YMZ280BTracker(),
                "GA20" => new GA20Tracker(),
                "PWM" => new GenericPcmTracker(),
                "VSU" => new GenericPcmTracker(),
                "YMF278B" => new OplTracker(),  // OPL4 兼容 OPL3
                "YMF271" => new YMF271Tracker(),
                "ES5503" => new GenericPcmTracker(),
                "ES5506" => new GenericPcmTracker(),
                _ => null
            };
            
            if (tracker != null)
            {
                // 设置芯片时钟频率（从 VGM 头读取）
                tracker.Clock = chip.Clock;
                byte baseType = GetChipTypeForName(baseName);
                // 第二个芯片使用 baseType | 0x80 作为键
                byte chipType = isSecondChip ? (byte)(baseType | 0x80) : baseType;
                _trackers[chipType] = tracker;
                
                // RF5C68/RF5C164 特殊处理：某些 VGM 文件头部声明 RF5C164 但命令使用 RF5C68
                // 为兼容性，同时注册两个芯片类型到同一个追踪器
                if (baseName == "RF5C68" || baseName == "RF5C164")
                {
                    _trackers[VgmCommandParser.CHIP_RF5C68] = tracker;
                    _trackers[VgmCommandParser.CHIP_RF5C164] = tracker;
                }
            }
        }

        // 使用 GD3 标签中的系统名，否则根据芯片推断
        SystemName = VgmFormat.GetSystemName(_header, _gd3, Gd3Language);
    }
    
    // 加载 VGM 数据并解析命令
    public void LoadVgmData(byte[] data, VgmHeader header)
    {
        _vgmData = data;
        _parser = new VgmCommandParser(data, header);
        _parser.Parse();
        _lastEventIndex = -1;
        _loopCacheValid = false;
        
        // 重置所有追踪器
        foreach (var tracker in _trackers.Values)
        {
            tracker.Reset();
        }
        
        Logger.Info($"[VgmVisualizer] 解析完成，共 {_parser.Events.Count} 个事件，总时长 {VgmCommandParser.TickToMs(_parser.TotalTicks):F1} ms");
    }
    
    // 更新到指定时间点的状态
    public void UpdateToTime(double timeMs)
    {
        if (_parser == null || _parser.Events.Count == 0) return;
        
        // 缓存循环点计算（只计算一次）
        if (!_loopCacheValid)
        {
            // 计算 VGM 文件原始时长（毫秒）
            _cachedTotalMs = _header.TotalSamples / 44100.0 * 1000.0;
            
            // 有循环点时缓存循环信息
            if (_hasLoop && _header.LoopSamples > 0)
            {
                _cachedLoopPointMs = (_header.TotalSamples - _header.LoopSamples) / 44100.0 * 1000.0;
                _cachedLoopLengthMs = _header.LoopSamples / 44100.0 * 1000.0;
            }
            
            // 计算实际播放总时长（与 VgmAudioSource.CalculateTotalLength 逻辑一致）
            _cachedPlayDurationMs = _cachedTotalMs;
            if (_hasLoop && _cachedLoopLengthMs > 0)
            {
                // LoopCount=1: 播放到循环结束点（即 _cachedTotalMs）
                // LoopCount=2: 额外播放1次循环
                if (_loopCount > 1)
                {
                    _cachedPlayDurationMs += _cachedLoopLengthMs * (_loopCount - 1);
                }
                // 淡出期间继续播放循环
                if (_fadeOutEnabled && _fadeOutDuration > 0)
                {
                    _cachedPlayDurationMs += _fadeOutDuration * 1000.0;
                }
            }
            
            _loopCacheValid = true;
        }
        
        // 时间检查：如果时间为负数（新歌曲还没开始）或数据无效，不更新
        if (timeMs < 0 || _cachedTotalMs <= 0)
        {
            return;
        }
        
        // 时间超过实际播放时长，停止更新（歌曲已结束）
        if (timeMs > _cachedPlayDurationMs)
        {
            return;
        }
        
        // 循环支持：在实际播放时长内循环显示（包括淡出期间）
        double effectiveTimeMs = timeMs;
        if (_cachedLoopLengthMs > 0 && timeMs > _cachedLoopPointMs)
        {
            double loopOffset = (timeMs - _cachedLoopPointMs) % _cachedLoopLengthMs;
            effectiveTimeMs = _cachedLoopPointMs + loopOffset;
        }
        
        uint targetTick = VgmCommandParser.MsToTick(effectiveTimeMs);
        int targetIndex = _parser.GetEventIndexAtTick(targetTick);
        
        // 计算时间差（用于声像衰减）
        float deltaMs = (float)(timeMs - _lastUpdateTime);
        _lastUpdateTime = timeMs;
        
        // 如果时间倒退（包括循环跳回），需要重置并从头开始
        if (targetIndex < _lastEventIndex)
        {
            foreach (var tracker in _trackers.Values)
            {
                tracker.Reset();
            }
            _lastEventIndex = -1;
            deltaMs = 0;
        }
        
        // 处理从上次位置到当前位置的所有事件
        var events = _parser.Events;
        for (int i = _lastEventIndex + 1; i <= targetIndex && i < events.Count; i++)
        {
            var evt = events[i];
            byte chipType = evt.ChipType;
            
            // 双芯片判断：
            // 1. ChipIndex = 1 表示第二芯片 (YM系列使用0xAn命令)
            // 2. Port的bit7表示第二芯片 (其他芯片使用bit7标志)
            bool isSecondChip = evt.ChipIndex == 1 || (evt.Port & 0x80) != 0;
            
            if (isSecondChip)
            {
                // 尝试使用第二个芯片的Tracker
                byte secondChipType = (byte)(chipType | 0x80);
                if (_trackers.TryGetValue(secondChipType, out var tracker2))
                {
                    // 创建修正后的事件（清除Port的bit7，因为Tracker不需要双芯片标志）
                    var fixedEvt = new VgmEvent
                    {
                        Tick = evt.Tick,
                        ChipType = evt.ChipType,
                        ChipIndex = 0, // 对于Tracker来说是第一个（也是唯一）
                        Port = (byte)(evt.Port & 0x7F),
                        Register = evt.Register,
                        Value = evt.Value,
                        Value2 = evt.Value2
                    };
                    tracker2.ProcessEvent(fixedEvt);
                    continue;
                }
            }
            // 使用第一个芯片的Tracker
            if (_trackers.TryGetValue(chipType, out var tracker))
            {
                tracker.ProcessEvent(evt);
            }
        }
        
        _lastEventIndex = targetIndex;
        
        // 更新可视化状态（使用缓存的芯片类型查找表）
        for (int i = 0; i < _chipStates.Count; i++)
        {
            var state = _chipStates[i];
            byte chipType = _chipTypeLookup[i];
            if (_trackers.TryGetValue(chipType, out var tracker))
            {
                tracker.UpdateVisualizerState(state);
            }
            UpdatePanDisplay(state, deltaMs);
            
            // MultiPCM 专用：更新音符历史记录
            if (state.Info.Name == "MultiPCM")
            {
                UpdateMultiPcmHistory(state, timeMs);
            }
        }
    }
    
    // 获取 MultiPCM 音符历史记录（用于 Piano Roll 渲染）
    public IReadOnlyList<NoteHistoryEntry> GetMultiPcmHistory() => _multiPcmHistory;
    
    // 更新 MultiPCM 音符历史记录
    private void UpdateMultiPcmHistory(ChipState state, double currentTimeMs)
    {
        // 清理过期的历史记录
        double cutoffTime = currentTimeMs - HISTORY_WINDOW_MS;
        _multiPcmHistory.RemoveAll(e => e.StartTime + e.Duration < cutoffTime && e.Duration > 0);
        
        // 限制历史记录数量
        while (_multiPcmHistory.Count > MAX_HISTORY_ENTRIES)
        {
            _multiPcmHistory.RemoveAt(0);
        }
        
        // 检查每个通道的状态变化
        for (int ch = 0; ch < state.Channels.Length; ch++)
        {
            var channel = state.Channels[ch];
            
            // 查找该通道是否有活跃的历史记录（Duration=0 表示仍在播放）
            var activeEntry = _multiPcmHistory.Find(e => e.Channel == ch && e.Duration == 0);
            
            if (channel.KeyOn && channel.Note >= 0 && channel.Volume > 0)
            {
                // 通道活跃
                if (activeEntry == null)
                {
                    // 新音符开始
                    _multiPcmHistory.Add(new NoteHistoryEntry
                    {
                        Note = channel.Note,
                        Volume = channel.Volume,
                        Detune = channel.Detune,
                        Channel = ch,
                        StartTime = currentTimeMs,
                        Duration = 0  // 0 表示仍在播放
                    });
                }
                else if (activeEntry.Note != channel.Note)
                {
                    // 音符改变，结束旧的，开始新的
                    activeEntry.Duration = currentTimeMs - activeEntry.StartTime;
                    _multiPcmHistory.Add(new NoteHistoryEntry
                    {
                        Note = channel.Note,
                        Volume = channel.Volume,
                        Detune = channel.Detune,
                        Channel = ch,
                        StartTime = currentTimeMs,
                        Duration = 0
                    });
                }
                else
                {
                    // 同一音符，更新 Volume 和 Detune
                    activeEntry.Volume = channel.Volume;
                    activeEntry.Detune = channel.Detune;
                }
            }
            else
            {
                // 通道不活跃，结束当前音符
                if (activeEntry != null)
                {
                    activeEntry.Duration = currentTimeMs - activeEntry.StartTime;
                }
            }
        }
    }
    
    // 更新声像显示（使用双层平滑 + 峰值保持避免跳动）
    // 第一层：输入平滑（减少追踪器数据的抖动）
    // 第二层：峰值保持 + 显示平滑（减少视觉跳动）
    private void UpdatePanDisplay(ChipState state, float deltaMs)
    {
        // deltaMs = 0 表示循环跳回，保持当前显示值不变（避免跳动）
        if (deltaMs <= 0)
        {
            return;
        }
        
        // 容差值：避免微小波动导致跳动（约 5% 的变化会被忽略）
        const float TOLERANCE = 0.05f;
        // 输入平滑时间常数（毫秒）
        const float INPUT_SMOOTH_MS = 40f;
        
        float peakDecay = deltaMs / PEAK_DECAY_MS;
        float attackFactor = MathF.Min(1f, deltaMs / PAN_ATTACK_MS);
        float releaseFactor = deltaMs / PAN_RELEASE_MS;
        // 输入平滑因子（低通滤波器系数）
        float inputSmoothFactor = MathF.Min(1f, deltaMs / INPUT_SMOOTH_MS);
        
        // 打击闪光衰减速度（毫秒）
        const float ATTACK_FLASH_DECAY_MS = 80f;
        float flashDecay = deltaMs / ATTACK_FLASH_DECAY_MS;
        
        var channels = state.Channels;
        for (int i = 0; i < channels.Length; i++)
        {
            var ch = channels[i];
            
            // 检测 KeyOn 触发（从 false 变为 true）
            // 滑音（音符变化但 KeyOn 保持为 true）不触发闪光
            if (ch.KeyOn && !ch.PrevKeyOn && ch.Note >= 0 && ch.Volume > 0)
            {
                ch.AttackFlash = 1f;  // 触发闪光
            }
            
            // 更新前一帧状态
            ch.PrevKeyOn = ch.KeyOn;
            
            // 闪光衰减
            if (ch.AttackFlash > 0)
            {
                ch.AttackFlash = MathF.Max(0, ch.AttackFlash - flashDecay);
            }
            
            // 第一层：输入值平滑（低通滤波）
            // 从追踪器获取原始输入值
            float rawLeft = 0, rawRight = 0;
            float rawRearLeft = 0, rawRearRight = 0;
            
            if (ch.KeyOn && ch.Volume > 0)
            {
                // 判断音量范围：如果超过 127，使用 255 作为最大值
                float maxVol = (ch.PanLeft > 127 || ch.PanRight > 127 || ch.Volume > 127) ? 255f : 127f;
                
                if (ch.PanLeft > 0 || ch.PanRight > 0)
                {
                    rawLeft = ch.PanLeft / maxVol;
                    rawRight = ch.PanRight / maxVol;
                }
                else
                {
                    float vol = ch.Volume / maxVol;
                    float pan = ch.Panning * (1f / 64f);
                    rawLeft = vol * (1f - MathF.Max(0, pan));
                    rawRight = vol * (1f + MathF.Min(0, pan));
                }
                
                if (ch.HasQuadChannel)
                {
                    rawRearLeft = ch.RearLeft / maxVol;
                    rawRearRight = ch.RearRight / maxVol;
                }
            }
            
            // 平滑输入值（向目标值逐渐靠近，避免突变）
            ch.SmoothedPanLeft += (rawLeft - ch.SmoothedPanLeft) * inputSmoothFactor;
            ch.SmoothedPanRight += (rawRight - ch.SmoothedPanRight) * inputSmoothFactor;
            if (ch.HasQuadChannel)
            {
                ch.SmoothedRearLeft += (rawRearLeft - ch.SmoothedRearLeft) * inputSmoothFactor;
                ch.SmoothedRearRight += (rawRearRight - ch.SmoothedRearRight) * inputSmoothFactor;
            }
            
            // 使用平滑后的输入值作为目标值
            float targetLeft = ch.SmoothedPanLeft;
            float targetRight = ch.SmoothedPanRight;
            float targetRearLeft = ch.SmoothedRearLeft;
            float targetRearRight = ch.SmoothedRearRight;
            
            // 第二层：峰值保持 + 显示平滑
            // 峰值保持：目标值高于峰值时立即更新，否则缓慢衰减
            if (targetLeft >= ch.PeakPanLeft - TOLERANCE)
                ch.PeakPanLeft = targetLeft;
            else
                ch.PeakPanLeft = MathF.Max(targetLeft, ch.PeakPanLeft - peakDecay);
            
            if (targetRight >= ch.PeakPanRight - TOLERANCE)
                ch.PeakPanRight = targetRight;
            else
                ch.PeakPanRight = MathF.Max(targetRight, ch.PeakPanRight - peakDecay);
            
            // 显示值跟随峰值平滑变化（添加死区检测避免维持音量时抖动）
            float diffLeft = ch.PeakPanLeft - ch.DisplayPanLeft;
            if (MathF.Abs(diffLeft) < 0.005f)
            {
                // 死区：差值很小时直接锁定到目标值
                ch.DisplayPanLeft = ch.PeakPanLeft;
            }
            else if (diffLeft > 0)
                ch.DisplayPanLeft += diffLeft * attackFactor;
            else
                ch.DisplayPanLeft = MathF.Max(0, ch.DisplayPanLeft - releaseFactor);
                
            float diffRight = ch.PeakPanRight - ch.DisplayPanRight;
            if (MathF.Abs(diffRight) < 0.005f)
            {
                ch.DisplayPanRight = ch.PeakPanRight;
            }
            else if (diffRight > 0)
                ch.DisplayPanRight += diffRight * attackFactor;
            else
                ch.DisplayPanRight = MathF.Max(0, ch.DisplayPanRight - releaseFactor);
            
            // 四声道后声道
            if (ch.HasQuadChannel)
            {
                if (targetRearLeft >= ch.PeakRearLeft - TOLERANCE)
                    ch.PeakRearLeft = targetRearLeft;
                else
                    ch.PeakRearLeft = MathF.Max(targetRearLeft, ch.PeakRearLeft - peakDecay);
                
                if (targetRearRight >= ch.PeakRearRight - TOLERANCE)
                    ch.PeakRearRight = targetRearRight;
                else
                    ch.PeakRearRight = MathF.Max(targetRearRight, ch.PeakRearRight - peakDecay);
                
                float diffRearLeft = ch.PeakRearLeft - ch.DisplayRearLeft;
                if (MathF.Abs(diffRearLeft) < 0.005f)
                    ch.DisplayRearLeft = ch.PeakRearLeft;
                else if (diffRearLeft > 0)
                    ch.DisplayRearLeft += diffRearLeft * attackFactor;
                else
                    ch.DisplayRearLeft = MathF.Max(0, ch.DisplayRearLeft - releaseFactor);
                    
                float diffRearRight = ch.PeakRearRight - ch.DisplayRearRight;
                if (MathF.Abs(diffRearRight) < 0.005f)
                    ch.DisplayRearRight = ch.PeakRearRight;
                else if (diffRearRight > 0)
                    ch.DisplayRearRight += diffRearRight * attackFactor;
                else
                    ch.DisplayRearRight = MathF.Max(0, ch.DisplayRearRight - releaseFactor);
            }
        }
    }
    
    // 芯片名称转类型 ID
    private static byte GetChipTypeForName(string name) => name switch
    {
        "YM2612" => VgmCommandParser.CHIP_YM2612,
        "SN76489" => VgmCommandParser.CHIP_SN76489,
        "AY-3-8910" => VgmCommandParser.CHIP_AY8910,
        "YM2151" => VgmCommandParser.CHIP_YM2151,
        "YM2413" => VgmCommandParser.CHIP_YM2413,
        "YM2203" => VgmCommandParser.CHIP_YM2203,
        "YM2608" => VgmCommandParser.CHIP_YM2608,
        "YM2610" or "YM2610B" => VgmCommandParser.CHIP_YM2610,
        "NES APU" => VgmCommandParser.CHIP_NESAPU,
        "GB DMG" => VgmCommandParser.CHIP_GBDMG,
        "HuC6280" => VgmCommandParser.CHIP_HUC6280,
        "YM3812" => VgmCommandParser.CHIP_YM3812,
        "YM3526" => VgmCommandParser.CHIP_YM3526,
        "Y8950" => VgmCommandParser.CHIP_Y8950,
        "YMF262" => VgmCommandParser.CHIP_YMF262,
        "YMF278B" => VgmCommandParser.CHIP_YMF278B,
        "YMF271" => VgmCommandParser.CHIP_YMF271,
        "QSound" => VgmCommandParser.CHIP_QSOUND,
        "K051649" => VgmCommandParser.CHIP_K051649,
        "POKEY" => VgmCommandParser.CHIP_POKEY,
        "SAA1099" => VgmCommandParser.CHIP_SAA1099,
        "RF5C68" => VgmCommandParser.CHIP_RF5C68,
        "RF5C164" => VgmCommandParser.CHIP_RF5C164,
        "C140" => VgmCommandParser.CHIP_C140,
        "C352" => VgmCommandParser.CHIP_C352,
        "K053260" => VgmCommandParser.CHIP_K053260,
        "K054539" => VgmCommandParser.CHIP_K054539,
        "MultiPCM" => VgmCommandParser.CHIP_MULTIPCM,
        "SCSP" => VgmCommandParser.CHIP_SCSP,
        "WonderSwan" => VgmCommandParser.CHIP_WSWAN,
        "VSU" => VgmCommandParser.CHIP_VSU,
        "X1-010" => VgmCommandParser.CHIP_X1010,
        "OKIM6295" => VgmCommandParser.CHIP_OKIM6295,
        "OKIM6258" => VgmCommandParser.CHIP_OKIM6258,
        "uPD7759" => VgmCommandParser.CHIP_UPD7759,
        "SegaPCM" => VgmCommandParser.CHIP_SEGAPCM,
        "YMZ280B" => VgmCommandParser.CHIP_YMZ280B,
        "GA20" => VgmCommandParser.CHIP_GA20,
        "PWM" => VgmCommandParser.CHIP_PWM,
        "ES5503" => VgmCommandParser.CHIP_ES5503,
        "ES5506" => VgmCommandParser.CHIP_ES5506,
        _ => 0
    };

    // 获取通道标签
        private static string GetChannelLabel(string chipName, int channelIndex)
        {
            // 归一化芯片名：去掉 “#2” 等后缀，保证双芯片用同一套命名规则
            string baseName = chipName ?? string.Empty;
            int hashIdx = baseName.IndexOf('#');
            if (hashIdx > 0)
                baseName = baseName[..hashIdx].TrimEnd();

            return baseName switch
        {
            // YM2612: 新布局 - FM1-3, OP2-4(Extended), FM4-5, DAC
            "YM2612" => channelIndex switch
            {
                0 => "FM1",
                1 => "FM2",
                2 => "FM3",
                3 => "OP2",  // FM3 Extended OP2
                4 => "OP3",  // FM3 Extended OP3
                5 => "OP4",  // FM3 Extended OP4
                6 => "FM4",
                7 => "FM5",
                8 => "DAC",
                _ => $"CH{channelIndex + 1}"
            },
            "YM2151" => $"FM{channelIndex + 1}",
            // YM2608: 新布局 - FM1-3, OP2-4, FM4-6, SSG1-3, ADPCM, RHY1-6
            "YM2608" => channelIndex switch
            {
                < 3 => $"FM{channelIndex + 1}",
                < 6 => $"OP{channelIndex - 1}",     // OP2-4
                < 9 => $"FM{channelIndex - 2}",     // FM4-6 (索引6-8 → FM4-6)
                < 12 => $"SSG{channelIndex - 8}",   // SSG1-3 (索引9-11)
                12 => "ADPCM",
                _ => $"RHY{channelIndex - 12}"      // RHY1-6 (索引13-18)
            },
            // YM2610/YM2610B: 新布局 - FM1-3, OP2-4, FM4-6, SSG1-3, PCMA1-6, PCMB
            "YM2610" or "YM2610B" => channelIndex switch
            {
                < 3 => $"FM{channelIndex + 1}",
                < 6 => $"OP{channelIndex - 1}",     // OP2-4
                < 9 => $"FM{channelIndex - 2}",     // FM4-6 (索引6-8)
                < 12 => $"SSG{channelIndex - 8}",   // SSG1-3 (索引9-11)
                < 18 => $"PCMA{channelIndex - 11}", // PCMA1-6 (索引12-17)
                _ => "PCMB"                         // PCMB (索引18)
            },
            // YM2203: 新布局 - FM1-3, OP2-4(Extended), SSG1-3
            "YM2203" => channelIndex switch
            {
                < 3 => $"FM{channelIndex + 1}",
                < 6 => $"OP{channelIndex - 1}",  // OP2-4
                _ => $"SSG{channelIndex - 5}"    // SSG1-3 (索引6-8)
            },
            "SN76489" => channelIndex < 3 ? $"T{channelIndex + 1}" : "NOI",
            "AY-3-8910" => $"CH{(char)('A' + channelIndex)}",
            "NES APU" => channelIndex switch
            {
                0 => "PU1", 1 => "PU2", 2 => "TRI", 3 => "NOI", _ => "DMC"
            },
            "GB DMG" => channelIndex switch
            {
                0 => "CH1", 1 => "CH2", 2 => "WAV", _ => "NOI"
            },
            "HuC6280" => $"CH{channelIndex + 1}",
            "QSound" => $"CH{channelIndex + 1:D2}",
            "SegaPCM" => $"CH{channelIndex + 1:D2}",
            "OKIM6295" => $"CH{channelIndex + 1}",
            "RF5C68" or "RF5C164" => $"CH{channelIndex + 1}",
            "C140" => $"CH{channelIndex + 1:D2}",
            _ => $"CH{channelIndex + 1}"
        };
    }

    // 更新通道状态（由音频渲染器调用）
    public void UpdateChannelState(int chipIndex, int channelIndex, int note, int volume, bool keyOn)
    {
        if (chipIndex < 0 || chipIndex >= _chipStates.Count) return;
        var chip = _chipStates[chipIndex];
        if (channelIndex < 0 || channelIndex >= chip.Channels.Length) return;

        var channel = chip.Channels[channelIndex];
        channel.Note = note;
        channel.Volume = volume;
        channel.KeyOn = keyOn;
    }

    // 更新寄存器值
    public void UpdateRegister(int chipIndex, int address, byte value)
    {
        if (chipIndex < 0 || chipIndex >= _chipStates.Count) return;
        var chip = _chipStates[chipIndex];
        if (address < 0 || address >= chip.Registers.Length) return;
        chip.Registers[address] = value;
    }

    // 获取芯片颜色
    public static SKColor GetChipColor(int chipIndex)
    {
        // 不同芯片使用不同灰度
        byte gray = (byte)(200 - (chipIndex % 4) * 20);
        return new SKColor(gray, gray, gray);
    }

    // 计算组件高度
    public int CalculateTotalHeight(int chipPanelHeight)
    {
        return _chipStates.Count * chipPanelHeight;
    }

    // 获取音符名称
    public static string GetNoteName(int note)
    {
        if (note < 0) return "--";
        string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        int octave = note / 12;
        int semitone = note % 12;
        return $"{names[semitone]}{octave}";
    }

    // 获取频率对应的音符
    public static int FrequencyToNote(double frequency)
    {
        if (frequency <= 0) return -1;
        double note = 12 * Math.Log2(frequency / 440.0) + 69;
        return (int)Math.Round(note);
    }

    // FM 频率转音符（YM2612/YM2151 等）
    public static int FmFnumToNote(int fnum, int block)
    {
        // YM2612: F-Number 公式 F-Num = (144 * f * 2^20 / M / 2^(B-1))
        // 简化计算
        double freq = fnum * Math.Pow(2, block - 21) * 7670454.0 / 144;
        return FrequencyToNote(freq);
    }

    // PSG 频率转音符（SN76489 等）
    public static int PsgPeriodToNote(int period, int clockHz)
    {
        if (period <= 0) return -1;
        double freq = clockHz / (32.0 * period);
        return FrequencyToNote(freq);
    }
    
    // PCM 播放速率转音符（C352/SegaPCM/K054539 等）
    // ratio: 播放速率相对于基准速率的倍率（1.0=原始音高）
    // baseNote: 基准音高对应的音符（默认 C4=60）
    public static int PcmRatioToNote(double ratio, int baseNote = 60)
    {
        if (ratio <= 0) return -1;
        // 12音阶系统：每八度倍频，每半音相差 2^(1/12)
        double semitones = 12.0 * Math.Log2(ratio);
        return baseNote + (int)Math.Round(semitones);
    }
    
    // 计算频率相对于音符的音分偏移 (cent = 1/100 半音)
    // 返回值: -50 ~ +50 (超出范围会被截断)
    public static int FrequencyToCent(double frequency, int note)
    {
        if (frequency <= 0 || note < 0) return 0;
        // 音符对应的标准频率: A4 (note 69) = 440 Hz
        double noteFreq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
        // cent = 1200 * log2(actualFreq / noteFreq)
        double cents = 1200.0 * Math.Log2(frequency / noteFreq);
        // 限制范围到 -50 ~ +50
        return Math.Clamp((int)Math.Round(cents), -50, 50);
    }
    
    // 从频率同时获取音符和音分偏移
    public static (int note, int cent) FrequencyToNoteAndCent(double frequency)
    {
        if (frequency <= 0) return (-1, 0);
        double exactNote = 12.0 * Math.Log2(frequency / 440.0) + 69.0;
        int note = (int)Math.Round(exactNote);
        double noteFreq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
        double cents = 1200.0 * Math.Log2(frequency / noteFreq);
        return (note, Math.Clamp((int)Math.Round(cents), -50, 50));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        // 释放所有状态
        _chipStates.Clear();
        _trackers.Clear();
        
        // 释放解析器持有的数据
        _parser = null;
        _vgmData = null;
        
        // 释放缓存
        _cachedChipsString = null;
        _cachedVersionString = null;
        _chipTypeLookup = null;
        _chipList = null;
    }
    
    // 清理资源但保留实例（用于重复使用）
    public void Clear()
    {
        _chipStates.Clear();
        _trackers.Clear();
        _parser = null;
        _vgmData = null;
        _cachedChipsString = null;
        _cachedVersionString = null;
        _chipTypeLookup = null;
        _chipList = null;
        _lastEventIndex = -1;
        _loopCacheValid = false;
    }
}
