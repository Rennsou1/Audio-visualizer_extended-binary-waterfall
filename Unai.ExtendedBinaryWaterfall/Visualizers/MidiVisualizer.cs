using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace Unai.ExtendedBinaryWaterfall;

// 打击乐器类型枚举
public enum DrumType
{
    Kick,           // 大鼓
    Snare,          // 军鼓
    HiHatClosed,    // 闭合踩镲
    HiHatOpen,      // 开放踩镲
    Tom,            // 通通鼓
    Crash,          // 碎音镲
    Ride,           // 叮叮镲
    Other           // 其他打击乐器
}

// MIDI 音符可视化数据
public struct NoteVisualData
{
    public int NoteNumber;      // 音高 0-127 (60 = C4)
    public double StartMs;      // 开始时间（毫秒）
    public double EndMs;        // 结束时间（毫秒）
    public int Velocity;        // 力度 0-127
    public int Channel;         // MIDI 通道 0-15
    
    // 持续时间（毫秒）
    public double DurationMs => EndMs - StartMs;
}

// MIDI 可视化数据提取器：从 MIDI 文件提取音符数据用于钢琴卷帘渲染
public class MidiVisualizer : IDisposable
{
    // 预计算的音符数据（按开始时间排序）
    private List<NoteVisualData> _notes = new();
    // 按结束时间排序的索引（用于快速统计已播放音符数）
    private int[] _notesByEndTimeIndex;
    private double _totalDurationMs;
    private string _filePath;
    // 最大音符时长（用于扩展可见范围搜索）
    private double _maxNoteDurationMs = 0;
    
    // 文件路径（用于判断是否需要重新加载）
    public string FilePath => _filePath;
    
    // 音符范围（用于自适应渲染）
    public int MinNoteNumber { get; private set; } = 127;
    public int MaxNoteNumber { get; private set; } = 0;
    
    // 总时长
    public double TotalDurationMs => _totalDurationMs;
    
    // 音符总数
    public int NoteCount => _notes.Count;
    
    // 打击乐通道列表（从 MidiMetadata 获取）
    private HashSet<int> _drumChannels = new() { 9 };
    public HashSet<int> DrumChannels => _drumChannels;
    
    // 设置打击乐通道列表
    public void SetDrumChannels(HashSet<int> channels)
    {
        _drumChannels = channels ?? new HashSet<int> { 9 };
        _usedDrumNotes = null;  // 清除缓存，下次重新计算
    }
    
    // 判断通道是否为打击乐通道
    public bool IsDrumChannel(int channel) => _drumChannels.Contains(channel);
    
    // 获取当前时刻之前已完成播放的音符数（使用二分查找）
    public int GetPlayedNoteCount(double currentTimeMs)
    {
        if (_notes.Count == 0 || _notesByEndTimeIndex == null) return 0;
        
        // 二分查找：找到第一个结束时间 > currentTimeMs 的音符在排序索引中的位置
        int left = 0, right = _notesByEndTimeIndex.Length;
        while (left < right)
        {
            int mid = (left + right) / 2;
            if (_notes[_notesByEndTimeIndex[mid]].EndMs <= currentTimeMs)
                left = mid + 1;
            else
                right = mid;
        }
        return left;
    }
    
    // 获取当前正在播放的音符数（复音数）- 从可见音符中计算
    public int GetActiveNoteCount(double currentTimeMs, List<NoteVisualData> visibleNotes)
    {
        int count = 0;
        foreach (var note in visibleNotes)
        {
            if (note.StartMs <= currentTimeMs && note.EndMs >= currentTimeMs)
                count++;
        }
        return count;
    }

    public MidiVisualizer()
    {
    }

    // 加载 MIDI 文件并提取音符数据
    public void LoadMidi(string midiPath)
    {
        _filePath = midiPath;
        _notes.Clear();
        MinNoteNumber = 127;
        MaxNoteNumber = 0;

        try
        {
            var midiFile = MidiFile.Read(midiPath);
            var tempoMap = midiFile.GetTempoMap();

            // 获取所有音符并转换为毫秒时间
            foreach (var note in midiFile.GetNotes())
            {
                // 将 MIDI ticks 转换为实际毫秒
                var startTime = note.TimeAs<MetricTimeSpan>(tempoMap);
                var duration = note.LengthAs<MetricTimeSpan>(tempoMap);

                double startMs = startTime.TotalMilliseconds;
                double endMs = startMs + duration.TotalMilliseconds;

                _notes.Add(new NoteVisualData
                {
                    NoteNumber = note.NoteNumber,
                    StartMs = startMs,
                    EndMs = endMs,
                    Velocity = note.Velocity,
                    Channel = note.Channel
                });

                // 更新音符范围
                if (note.NoteNumber < MinNoteNumber) MinNoteNumber = note.NoteNumber;
                if (note.NoteNumber > MaxNoteNumber) MaxNoteNumber = note.NoteNumber;
            }

            // 按开始时间排序（用于二分查找优化）
            _notes.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

            // 构建按结束时间排序的索引（用于快速统计已播放音符数）
            _notesByEndTimeIndex = Enumerable.Range(0, _notes.Count).ToArray();
            Array.Sort(_notesByEndTimeIndex, (a, b) => _notes[a].EndMs.CompareTo(_notes[b].EndMs));

            // 计算最大音符时长（用于可见范围搜索）
            _maxNoteDurationMs = 0;
            foreach (var n in _notes)
            {
                double dur = n.EndMs - n.StartMs;
                if (dur > _maxNoteDurationMs) _maxNoteDurationMs = dur;
            }

            // 计算总时长
            var totalLength = midiFile.GetDuration<MetricTimeSpan>();
            _totalDurationMs = totalLength.TotalMilliseconds;

            // 扩展音符范围（至少显示 2 个八度）
            int noteRange = MaxNoteNumber - MinNoteNumber;
            if (noteRange < 24)
            {
                int expand = (24 - noteRange) / 2;
                MinNoteNumber = Math.Max(0, MinNoteNumber - expand);
                MaxNoteNumber = Math.Min(127, MaxNoteNumber + expand);
            }

            Logger.Info($"[MidiVisualizer] 已加载: {Path.GetFileName(midiPath)}, " +
                       $"音符数={_notes.Count}, 时长={TimeSpan.FromMilliseconds(_totalDurationMs):mm\\:ss}, " +
                       $"音域={GetNoteName(MinNoteNumber)}-{GetNoteName(MaxNoteNumber)}");
        }
        catch (Exception ex)
        {
            Logger.Error($"[MidiVisualizer] 加载 MIDI 失败: {ex.Message}");
            throw;
        }
    }

    // 获取当前时间窗口内的可见音符
    // 音符可见条件：StartMs < 窗口结束 且 EndMs > 窗口开始
    public void GetVisibleNotes(double currentMs, double windowMs, List<NoteVisualData> result)
    {
        result.Clear();
        
        if (_notes.Count == 0) return;

        double startWindow = currentMs - windowMs / 2;
        double endWindow = currentMs + windowMs / 2;

        // 二分查找：找到第一个 StartMs >= (startWindow - maxNoteDuration) 的音符
        // 这样可以确保不遗漏任何开始较早但持续很长的音符
        double searchStart = startWindow - _maxNoteDurationMs;
        int startIdx = BinarySearchByStartTime(searchStart);

        // 线性扫描可见音符
        for (int i = startIdx; i < _notes.Count; i++)
        {
            var note = _notes[i];
            
            // 如果音符开始时间超过窗口末尾，后面都不需要看了
            if (note.StartMs > endWindow) break;
            
            // 检查音符是否实际可见（结束时间 > 窗口开始）
            if (note.EndMs > startWindow)
            {
                result.Add(note);
            }
        }
    }

    // 二分查找：找到第一个 StartMs >= targetMs 的音符索引
    private int BinarySearchByStartTime(double targetMs)
    {
        int left = 0, right = _notes.Count;
        
        while (left < right)
        {
            int mid = (left + right) / 2;
            if (_notes[mid].StartMs < targetMs)
                left = mid + 1;
            else
                right = mid;
        }
        
        return left;
    }

    // 获取音符名称（如 C4, D#5）
    public static string GetNoteName(int noteNumber)
    {
        string[] noteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        int octave = (noteNumber / 12) - 1;
        int noteIndex = noteNumber % 12;
        return $"{noteNames[noteIndex]}{octave}";
    }

    // 判断音符是否为黑键
    public static bool IsBlackKey(int noteNumber)
    {
        int noteIndex = noteNumber % 12;
        // C#, D#, F#, G#, A# 是黑键
        return noteIndex == 1 || noteIndex == 3 || noteIndex == 6 || noteIndex == 8 || noteIndex == 10;
    }

    // 预缓存的通道颜色（避免每帧计算）
    private static readonly (byte r, byte g, byte b)[] _channelColors =
    {
        ((byte)255, (byte)100, (byte)100),  // 0: 红
        ((byte)100, (byte)255, (byte)100),  // 1: 绿
        ((byte)100, (byte)100, (byte)255),  // 2: 蓝
        ((byte)255, (byte)255, (byte)100),  // 3: 黄
        ((byte)255, (byte)100, (byte)255),  // 4: 品红
        ((byte)100, (byte)255, (byte)255),  // 5: 青
        ((byte)255, (byte)180, (byte)100),  // 6: 橙
        ((byte)180, (byte)100, (byte)255),  // 7: 紫
        ((byte)100, (byte)255, (byte)180),  // 8: 薄荷绿
        ((byte)200, (byte)200, (byte)200),  // 9: 鼓（灰色）
        ((byte)150, (byte)200, (byte)255),  // 10: 浅蓝
        ((byte)255, (byte)200, (byte)150),  // 11: 浅橙
        ((byte)200, (byte)255, (byte)150),  // 12: 浅绿
        ((byte)255, (byte)150, (byte)200),  // 13: 粉红
        ((byte)150, (byte)255, (byte)200),  // 14: 浅青
        ((byte)200, (byte)150, (byte)255),  // 15: 浅紫
    };

    // 根据 MIDI 通道返回颜色（使用缓存数组）
    public static (byte r, byte g, byte b) GetChannelColor(int channel)
    {
        return _channelColors[channel & 0xF];  // 等同于 channel % 16，但更快
    }
    
    // 获取打击乐器类型（根据 GM 标准 note number 映射）
    public static DrumType GetDrumType(int noteNumber)
    {
        return noteNumber switch
        {
            35 or 36 => DrumType.Kick,           // Bass Drum
            38 or 40 => DrumType.Snare,          // Snare
            42 or 44 => DrumType.HiHatClosed,    // Closed Hi-Hat
            46 => DrumType.HiHatOpen,            // Open Hi-Hat
            41 or 43 or 45 or 47 or 48 or 50 => DrumType.Tom,  // Toms
            49 or 57 => DrumType.Crash,          // Crash Cymbal
            51 or 59 => DrumType.Ride,           // Ride Cymbal
            _ => DrumType.Other
        };
    }
    
    // 获取打击乐器符号
    public static string GetDrumSymbol(DrumType type)
    {
        return type switch
        {
            DrumType.Kick => "×",
            DrumType.Snare => "◇",
            DrumType.HiHatClosed => "○",
            DrumType.HiHatOpen => "●",
            DrumType.Tom => "◎",
            DrumType.Crash => "☆",
            DrumType.Ride => "△",
            _ => "□"
        };
    }
    
    // 获取当前时刻活动的打击乐音符（用于打击乐显示）
    public void GetActiveDrumNotes(double currentTimeMs, double triggerWindowMs, List<(int noteNumber, int velocity, double triggerTimeMs)> result)
    {
        result.Clear();
        if (_notes.Count == 0) return;
        
        // 只查看最近 triggerWindowMs 内开始的打击乐音符
        double startWindow = currentTimeMs - triggerWindowMs;
        
        foreach (var note in _notes)
        {
            // 检查是否为打击乐通道
            if (!_drumChannels.Contains(note.Channel)) continue;
            
            // 音符在触发窗口内开始
            if (note.StartMs >= startWindow && note.StartMs <= currentTimeMs)
            {
                result.Add((note.NoteNumber, note.Velocity, note.StartMs));
            }
        }
    }
    
    // 获取 MIDI 文件中使用的所有打击乐音符
    private HashSet<int> _usedDrumNotes = null;
    public HashSet<int> GetUsedDrumNotes()
    {
        if (_usedDrumNotes != null) return _usedDrumNotes;
        
        _usedDrumNotes = new HashSet<int>();
        foreach (var note in _notes)
        {
            if (_drumChannels.Contains(note.Channel))
            {
                _usedDrumNotes.Add(note.NoteNumber);
            }
        }
        return _usedDrumNotes;
    }
    
    // 获取打击乐器简称（用于显示，支持 GM/GS/XG 标准）
    public static string GetDrumShortName(int noteNumber)
    {
        return noteNumber switch
        {
            // GS 扩展 (27-34)
            27 => "HQ",    // High Q / Filter Snap
            28 => "SN",    // Slap Noise
            29 => "SP",    // Scratch Push
            30 => "SL",    // Scratch Pull
            31 => "DS",    // Drum Sticks
            32 => "SQ",    // Square Click
            33 => "MC",    // Metronome Click
            34 => "MB",    // Metronome Bell
            
            // GM 标准 (35-81)
            35 => "BD2",   // Acoustic Bass Drum
            36 => "BD",    // Bass Drum 1
            37 => "SS",    // Side Stick
            38 => "SD",    // Acoustic Snare
            39 => "CP",    // Hand Clap
            40 => "ES",    // Electric Snare
            41 => "LF2",   // Low Floor Tom
            42 => "CH",    // Closed Hi-Hat
            43 => "LF",    // High Floor Tom
            44 => "PH",    // Pedal Hi-Hat
            45 => "LT",    // Low Tom
            46 => "OH",    // Open Hi-Hat
            47 => "LM",    // Low-Mid Tom
            48 => "HM",    // Hi-Mid Tom
            49 => "CC",    // Crash Cymbal 1
            50 => "HT",    // High Tom
            51 => "RC",    // Ride Cymbal 1
            52 => "CN",    // Chinese Cymbal
            53 => "RB",    // Ride Bell
            54 => "TB",    // Tambourine
            55 => "SC",    // Splash Cymbal
            56 => "CB",    // Cowbell
            57 => "C2",    // Crash Cymbal 2
            58 => "VS",    // Vibraslap
            59 => "R2",    // Ride Cymbal 2
            60 => "HB",    // High Bongo
            61 => "LB",    // Low Bongo
            62 => "MH",    // Mute High Conga
            63 => "OC",    // Open High Conga
            64 => "LC",    // Low Conga
            65 => "HI",    // High Timbale
            66 => "LI",    // Low Timbale
            67 => "HA",    // High Agogo
            68 => "LA",    // Low Agogo
            69 => "CA",    // Cabasa
            70 => "MR",    // Maracas
            71 => "SW",    // Short Whistle
            72 => "LW",    // Long Whistle
            73 => "SG",    // Short Guiro
            74 => "LG",    // Long Guiro
            75 => "CL",    // Claves
            76 => "HW",    // High Woodblock
            77 => "LW",    // Low Woodblock
            78 => "MQ",    // Mute Cuica
            79 => "OQ",    // Open Cuica
            80 => "MT",    // Mute Triangle
            81 => "OT",    // Open Triangle
            
            // GS 扩展 (82-87)
            82 => "SH",    // Shaker
            83 => "JB",    // Jingle Bell
            84 => "BT",    // Belltree
            85 => "CT",    // Castanets
            86 => "MS",    // Mute Surdo
            87 => "OS",    // Open Surdo
            
            _ => $"{noteNumber}"
        };
    }

    public void Dispose()
    {
        _notes.Clear();
    }
}

// MIDI 可视化器缓存：管理多个文件的可视化数据
public static class MidiVisualizerCache
{
    private static readonly Dictionary<string, MidiVisualizer> _cache = new();

    // 获取或创建 MIDI 可视化器
    public static MidiVisualizer GetOrCreate(string midiPath)
    {
        string fullPath = Path.GetFullPath(midiPath);
        
        if (_cache.TryGetValue(fullPath, out var visualizer))
        {
            return visualizer;
        }

        visualizer = new MidiVisualizer();
        visualizer.LoadMidi(midiPath);
        _cache[fullPath] = visualizer;
        
        return visualizer;
    }

    // 预加载 MIDI 文件（在后台线程调用）
    public static void Preload(string midiPath)
    {
        try
        {
            GetOrCreate(midiPath);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[MidiVisualizerCache] 预加载失败: {ex.Message}");
        }
    }

    // 清理缓存
    public static void Clear()
    {
        foreach (var visualizer in _cache.Values)
        {
            visualizer.Dispose();
        }
        _cache.Clear();
    }
}
