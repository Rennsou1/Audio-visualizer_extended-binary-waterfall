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
        public int PanLeft;           // 左声道音量 (0-127)
        public int PanRight;          // 右声道音量 (0-127)
        public float DisplayPanLeft;  // 显示用左声道 (带衰减)
        public float DisplayPanRight; // 显示用右声道 (带衰减)
        public bool KeyOn;            // 是否按下
        public string Label;          // 通道标签
    }

    // 芯片状态
    public class ChipState
    {
        public VgmChipInfo Info;
        public ChannelState[] Channels;
        public byte[] Registers;
    }

    private readonly List<ChipState> _chipStates = new();
    private readonly Dictionary<byte, VgmChipTracker> _trackers = new();
    private VgmAudioSource _audioSource;
    private VgmCommandParser _parser;
    private byte[] _vgmData;
    private int _lastEventIndex = -1;
    private bool _disposed;
    private double _lastUpdateTime;
    
    // 声像衰减设置（毫秒）
    private const float PAN_RELEASE_MS = 300f;

    public IReadOnlyList<ChipState> ChipStates => _chipStates;
    public VgmHeader Header => _audioSource?.Header ?? default;
    public Gd3Tag Gd3 => _audioSource?.Gd3;
    public VgmAudioSource AudioSource => _audioSource;
    public string SystemName { get; private set; } = "";
    public Gd3Language Gd3Language { get; set; } = Gd3Language.English;
    
    // 获取 VGM 版本字符串（BCD格式解析）
    public string GetVersionString()
    {
        uint ver = Header.Version;
        // VGM版本号是BCD格式：0x161 = 1.61
        int major = (int)(ver >> 8);
        int minor = (int)(ver & 0xFF);
        return $"{major}.{minor:X2}";
    }
    
    // 获取芯片列表字符串
    public string GetChipsString()
    {
        if (_chipStates.Count == 0) return "";
        var names = new List<string>();
        foreach (var chip in _chipStates)
        {
            names.Add(chip.Info.Name);
        }
        return string.Join(", ", names);
    }

    // 初始化可视化器
    public void Initialize(VgmAudioSource audioSource)
    {
        _audioSource = audioSource;
        _chipStates.Clear();
        _trackers.Clear();
        _lastEventIndex = -1;

        if (audioSource?.ChipList == null) return;

        // 根据芯片列表初始化状态和追踪器
        foreach (var chip in audioSource.ChipList)
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
            
            // 创建对应的追踪器
            VgmChipTracker tracker = chip.Name switch
            {
                "YM2612" => new YM2612Tracker(),
                "SN76489" => new SN76489Tracker(),
                "AY-3-8910" => new AY8910Tracker(),
                "YM2151" => new YM2151Tracker(),
                "YM2413" => new YM2413Tracker(),
                "YM2203" => new YM2203Tracker(),
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
                "K054539" => new K054539Tracker(),
                "MultiPCM" => new MultiPCMTracker(),
                "SCSP" => new ScspTracker(),
                "WonderSwan" => new WSwanTracker(),
                "X1-010" => new X1010Tracker(),
                "OKIM6295" => new OKIM6295Tracker(),
                "SegaPCM" => new SegaPCMTracker(),
                "YMZ280B" => new YMZ280BTracker(),
                "GA20" => new GA20Tracker(),
                _ => null
            };
            
            if (tracker != null)
            {
                byte chipType = GetChipTypeForName(chip.Name);
                _trackers[chipType] = tracker;
            }
        }

        SystemName = VgmFormat.GetSystemName(audioSource.Header);
    }
    
    // 加载 VGM 数据并解析命令
    public void LoadVgmData(byte[] data, VgmHeader header)
    {
        _vgmData = data;
        _parser = new VgmCommandParser(data, header);
        _parser.Parse();
        _lastEventIndex = -1;
        
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
        
        // 循环支持：如果有循环点且时间超过循环点，使用模运算计算循环内时间
        double effectiveTimeMs = timeMs;
        if (_audioSource != null && _audioSource.HasLoop)
        {
            // 循环点时间（毫秒）
            double loopPointMs = (_audioSource.Header.TotalSamples - _audioSource.Header.LoopSamples) / 44100.0 * 1000.0;
            // 循环段时间（毫秒）
            double loopLengthMs = _audioSource.Header.LoopSamples / 44100.0 * 1000.0;
            
            if (loopLengthMs > 0 && timeMs > loopPointMs)
            {
                // 计算循环内偏移
                double loopOffset = (timeMs - loopPointMs) % loopLengthMs;
                effectiveTimeMs = loopPointMs + loopOffset;
            }
        }
        
        uint targetTick = VgmCommandParser.MsToTick(effectiveTimeMs);
        int targetIndex = _parser.GetEventIndexAtTick(targetTick);
        
        // 计算时间差（用于声像衰减，使用原始时间保持动画连续）
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
        for (int i = _lastEventIndex + 1; i <= targetIndex && i < _parser.Events.Count; i++)
        {
            var evt = _parser.Events[i];
            if (_trackers.TryGetValue(evt.ChipType, out var tracker))
            {
                tracker.ProcessEvent(evt);
            }
        }
        
        _lastEventIndex = targetIndex;
        
        // 更新可视化状态
        foreach (var state in _chipStates)
        {
            byte chipType = GetChipTypeForName(state.Info.Name);
            if (_trackers.TryGetValue(chipType, out var tracker))
            {
                tracker.UpdateVisualizerState(state);
            }
            
            // 更新声像显示（带衰减）
            UpdatePanDisplay(state, deltaMs);
        }
    }
    
    // 更新声像显示（带衰减效果）
    private void UpdatePanDisplay(ChipState state, float deltaMs)
    {
        float decayFactor = deltaMs > 0 ? deltaMs / PAN_RELEASE_MS : 0;
        
        foreach (var ch in state.Channels)
        {
            // 计算目标声像值（从音量和Panning计算左右声道）
            float targetLeft = 0, targetRight = 0;
            
            if (ch.KeyOn && ch.Volume > 0)
            {
                float vol = ch.Volume / 127f;
                
                // 如果有独立的左右声道值
                if (ch.PanLeft > 0 || ch.PanRight > 0)
                {
                    targetLeft = ch.PanLeft / 127f * vol;
                    targetRight = ch.PanRight / 127f * vol;
                }
                else
                {
                    // 从Panning值计算（-64~63）
                    float pan = ch.Panning / 64f; // -1 ~ 1
                    targetLeft = vol * (1f - Math.Max(0, pan));
                    targetRight = vol * (1f + Math.Min(0, pan));
                }
            }
            
            // 应用衰减（只往下衰减，不往上）
            if (targetLeft > ch.DisplayPanLeft)
                ch.DisplayPanLeft = targetLeft;
            else
                ch.DisplayPanLeft = Math.Max(0, ch.DisplayPanLeft - decayFactor);
                
            if (targetRight > ch.DisplayPanRight)
                ch.DisplayPanRight = targetRight;
            else
                ch.DisplayPanRight = Math.Max(0, ch.DisplayPanRight - decayFactor);
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
        "NES APU" => VgmCommandParser.CHIP_NESAPU,
        "GB DMG" => VgmCommandParser.CHIP_GBDMG,
        "HuC6280" => VgmCommandParser.CHIP_HUC6280,
        "YM3812" => VgmCommandParser.CHIP_YM3812,
        "YM3526" => VgmCommandParser.CHIP_YM3526,
        "Y8950" => VgmCommandParser.CHIP_Y8950,
        "YMF262" => VgmCommandParser.CHIP_YMF262,
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
        "X1-010" => VgmCommandParser.CHIP_X1010,
        "OKIM6295" => VgmCommandParser.CHIP_OKIM6295,
        "SegaPCM" => VgmCommandParser.CHIP_SEGAPCM,
        "YMZ280B" => VgmCommandParser.CHIP_YMZ280B,
        "GA20" => VgmCommandParser.CHIP_GA20,
        _ => 0
    };

    // 获取通道标签
    private static string GetChannelLabel(string chipName, int channelIndex)
    {
        return chipName switch
        {
            "YM2612" => channelIndex < 6 ? $"FM{channelIndex + 1}" : "DAC",
            "YM2151" => $"FM{channelIndex + 1}",
            "YM2608" => channelIndex < 6 ? $"FM{channelIndex + 1}" 
                      : channelIndex < 9 ? $"SSG{channelIndex - 5}" 
                      : channelIndex == 9 ? "ADPCM" : $"RHY{channelIndex - 9}",
            "YM2203" => channelIndex < 3 ? $"FM{channelIndex + 1}" : $"SSG{channelIndex - 2}",
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

    // 获取芯片颜色（纯白灰黑色方案）
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chipStates.Clear();
    }
}
