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
        public int Note;          // 当前音符 (0-127, -1 表示无声)
        public int Volume;        // 当前音量 (0-127)
        public int Panning;       // 声像 (-64 ~ 63)
        public bool KeyOn;        // 是否按下
        public string Label;      // 通道标签
    }

    // 芯片状态
    public class ChipState
    {
        public VgmChipInfo Info;
        public ChannelState[] Channels;
        public byte[] Registers;  // 原始寄存器值
    }

    private readonly List<ChipState> _chipStates = new();
    private readonly Dictionary<byte, VgmChipTracker> _trackers = new();
    private VgmAudioSource _audioSource;
    private VgmCommandParser _parser;
    private byte[] _vgmData;
    private int _lastEventIndex = -1;
    private bool _disposed;

    // 颜色配置
    private static readonly SKColor[] ChipColors = new[]
    {
        new SKColor(0xFF, 0x66, 0x66), // 红
        new SKColor(0x66, 0xFF, 0x66), // 绿
        new SKColor(0x66, 0x66, 0xFF), // 蓝
        new SKColor(0xFF, 0xFF, 0x66), // 黄
        new SKColor(0xFF, 0x66, 0xFF), // 紫
        new SKColor(0x66, 0xFF, 0xFF), // 青
        new SKColor(0xFF, 0xAA, 0x66), // 橙
        new SKColor(0xAA, 0x66, 0xFF), // 紫蓝
    };

    public IReadOnlyList<ChipState> ChipStates => _chipStates;
    public VgmHeader Header => _audioSource?.Header ?? default;
    public Gd3Tag Gd3 => _audioSource?.Gd3;
    public string SystemName { get; private set; } = "";
    public Gd3Language Gd3Language { get; set; } = Gd3Language.English;

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
        
        uint targetTick = VgmCommandParser.MsToTick(timeMs);
        int targetIndex = _parser.GetEventIndexAtTick(targetTick);
        
        // 如果时间倒退，需要重置并从头开始
        if (targetIndex < _lastEventIndex)
        {
            foreach (var tracker in _trackers.Values)
            {
                tracker.Reset();
            }
            _lastEventIndex = -1;
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

    // 获取芯片颜色
    public static SKColor GetChipColor(int chipIndex)
    {
        return ChipColors[chipIndex % ChipColors.Length];
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
