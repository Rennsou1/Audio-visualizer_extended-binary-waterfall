using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using System.Linq;
using MidiNote = Melanchall.DryWetMidi.Interaction.Note;

namespace Unai.ExtendedBinaryWaterfall;

// MIDI 文件元数据
public class MidiMetadata
{
    // 初始 BPM（第一个 Tempo 事件的值，默认 120）
    public double InitialBpm { get; set; } = 120.0;
    
    // 初始拍号（默认 4/4）
    public int TimeSignatureNumerator { get; set; } = 4;
    public int TimeSignatureDenominator { get; set; } = 4;
    
    // MIDI 通道数量（包含音符的通道数）
    public int ChannelCount { get; set; } = 0;
    
    // MIDI 类型（GM/GS/XG/GM2）
    public string MidiType { get; set; } = "GM";
    
    // 总音符数
    public int TotalNoteCount { get; set; } = 0;
    
    // 最大复音数（同时发声的最大音符数）
    public int MaxPolyphony { get; set; } = 0;
    
    // MIDI 总时长（毫秒）
    public double TotalDurationMs { get; set; } = 0;
    
    // MIDI 标题（从 SequenceTrackName 或 Text 事件读取）
    public string Title { get; set; } = null;
    
    // Tempo Map（用于实时 BPM 查询）
    public TempoMap TempoMap { get; set; } = null;
    
    // 打击乐通道列表（0-indexed，通道 9 = MIDI 通道 10）
    public System.Collections.Generic.HashSet<int> DrumChannels { get; set; } = new() { 9 };
    
    // 获取格式化的拍号字符串
    public string TimeSignatureString => $"{TimeSignatureNumerator}/{TimeSignatureDenominator}";
    
    // 获取格式化的 BPM 字符串
    public string BpmString => $"{InitialBpm:F0}";
    
    // 从 MIDI 文件解析元数据
    public static MidiMetadata FromMidiFile(string midiFilePath)
    {
        var metadata = new MidiMetadata();
        
        try
        {
            var midiFile = MidiFile.Read(midiFilePath);
            metadata.TempoMap = midiFile.GetTempoMap();
            
            // 获取初始 Tempo
            var tempoChanges = metadata.TempoMap.GetTempoChanges();
            if (tempoChanges.Any())
            {
                var firstTempo = tempoChanges.First();
                // Tempo 以微秒/四分音符表示，转换为 BPM
                metadata.InitialBpm = 60000000.0 / firstTempo.Value.MicrosecondsPerQuarterNote;
            }
            
            // 获取初始拍号
            var timeSignatureChanges = metadata.TempoMap.GetTimeSignatureChanges();
            if (timeSignatureChanges.Any())
            {
                var firstTimeSignature = timeSignatureChanges.First();
                metadata.TimeSignatureNumerator = firstTimeSignature.Value.Numerator;
                metadata.TimeSignatureDenominator = firstTimeSignature.Value.Denominator;
            }
            
            // 获取所有音符
            var notes = midiFile.GetNotes().ToList();
            metadata.TotalNoteCount = notes.Count;
            
            // 计算使用的通道数
            var usedChannels = notes.Select(n => n.Channel).Distinct().ToList();
            metadata.ChannelCount = usedChannels.Count;
            
            // 检测 MIDI 类型和打击乐通道（通过 SysEx 消息和 Bank Select）
            (metadata.MidiType, metadata.DrumChannels) = DetectMidiTypeAndDrumChannels(midiFile);
            
            // 计算最大复音数
            metadata.MaxPolyphony = CalculateMaxPolyphony(notes, metadata.TempoMap);
            
            // 计算 MIDI 总时长
            var duration = midiFile.GetDuration<MetricTimeSpan>();
            metadata.TotalDurationMs = duration.TotalMicroseconds / 1000.0;
            
            // 读取 MIDI 标题（从 SequenceTrackName 或 Text 事件）
            metadata.Title = ExtractMidiTitle(midiFile);
        }
        catch (System.Exception ex)
        {
            Logger.Warning($"[MidiMetadata] 无法解析 MIDI 元数据: {ex.Message}");
        }
        
        return metadata;
    }
    
    // 从 MIDI 文件提取标题（SequenceTrackName 或 Text 事件）
    private static string ExtractMidiTitle(MidiFile midiFile)
    {
        try
        {
            foreach (var trackChunk in midiFile.GetTrackChunks())
            {
                foreach (var midiEvent in trackChunk.Events)
                {
                    // 优先使用 SequenceTrackNameEvent（标准的曲目名称）
                    if (midiEvent is SequenceTrackNameEvent trackNameEvent)
                    {
                        string name = trackNameEvent.Text?.Trim();
                        if (!string.IsNullOrEmpty(name))
                            return name;
                    }
                    // 其次使用 TextEvent（有些 MIDI 用这个存标题）
                    else if (midiEvent is TextEvent textEvent)
                    {
                        string text = textEvent.Text?.Trim();
                        if (!string.IsNullOrEmpty(text) && text.Length <= 100)
                            return text;
                    }
                }
            }
        }
        catch
        {
            // 忽略解析错误
        }
        return null;
    }
    
    // 检测 MIDI 类型和打击乐通道
    private static (string midiType, System.Collections.Generic.HashSet<int> drumChannels) DetectMidiTypeAndDrumChannels(MidiFile midiFile)
    {
        var drumChannels = new System.Collections.Generic.HashSet<int> { 9 };  // 默认通道 10（索引 9）
        string midiType = "GM";
        
        // 用于跟踪每个通道的 Bank Select MSB
        var channelBankMsb = new System.Collections.Generic.Dictionary<int, byte>();
        
        // 遍历所有事件
        foreach (var trackChunk in midiFile.GetTrackChunks())
        {
            foreach (var midiEvent in trackChunk.Events)
            {
                // 检测 SysEx 消息
                if (midiEvent is NormalSysExEvent sysEx)
                {
                    var data = sysEx.Data;
                    if (data != null && data.Length >= 3)
                    {
                        // Roland GS: F0 41 10 42 12 ...
                        if (data[0] == 0x41 && data.Length >= 5 && data[2] == 0x42)
                        {
                            midiType = "GS";
                            // 检测 GS 打击乐通道设置: F0 41 10 42 12 40 1x 15 [01或02] xx F7
                            // 地址 40 1x 15 = USE FOR RHYTHM PART
                            if (data.Length >= 9 && data[3] == 0x12 && data[4] == 0x40 && (data[5] & 0xF0) == 0x10 && data[6] == 0x15)
                            {
                                int blockNumber = data[5] & 0x0F;
                                byte rhythmMode = data[7];
                                // 将 block number 转换为 MIDI 通道
                                // Block 0 = 通道 10, Block 1-9 = 通道 1-9, Block A-F = 通道 11-16
                                int channel = blockNumber == 0 ? 9 : (blockNumber <= 9 ? blockNumber - 1 : blockNumber);
                                if (rhythmMode == 1 || rhythmMode == 2)
                                {
                                    drumChannels.Add(channel);
                                }
                            }
                        }
                        // Yamaha XG: F0 43 10 4C ...
                        else if (data[0] == 0x43 && data.Length >= 4 && data[2] == 0x4C)
                        {
                            midiType = "XG";
                            // XG 打击乐通道设置: F0 43 10 4C 08 0n 07 [值] F7
                            // n = 通道号, 值 = 打击乐 kit
                            if (data.Length >= 7 && data[3] == 0x08 && data[5] == 0x07)
                            {
                                int channel = data[4] & 0x0F;
                                drumChannels.Add(channel);
                            }
                        }
                        // GM2 System On: F0 7E 7F 09 03 F7
                        else if (data[0] == 0x7E && data.Length >= 4 && data[2] == 0x09 && data[3] == 0x03)
                        {
                            midiType = "GM2";
                        }
                        // GM System On: F0 7E 7F 09 01 F7
                        else if (data[0] == 0x7E && data.Length >= 4 && data[2] == 0x09 && data[3] == 0x01)
                        {
                            midiType = "GM";
                        }
                    }
                }
                // 检测 Bank Select MSB (CC#0) - 用于 GM2 打击乐检测
                else if (midiEvent is ControlChangeEvent cc && cc.ControlNumber == 0)
                {
                    channelBankMsb[(int)cc.Channel] = (byte)cc.ControlValue;
                }
                // 检测 Program Change - 如果之前有 Bank MSB = 0x78 (120)，则为打击乐通道
                else if (midiEvent is ProgramChangeEvent pc)
                {
                    int channel = (int)pc.Channel;
                    if (channelBankMsb.TryGetValue(channel, out byte bankMsb) && bankMsb == 0x78)
                    {
                        drumChannels.Add(channel);
                        if (midiType == "GM") midiType = "GM2";
                    }
                }
            }
        }
        
        return (midiType, drumChannels);
    }
    
    // 计算最大复音数
    private static int CalculateMaxPolyphony(System.Collections.Generic.List<MidiNote> notes, TempoMap tempoMap)
    {
        if (notes.Count == 0) return 0;
        
        // 创建事件列表：音符开始 (+1) 和结束 (-1)
        var events = new System.Collections.Generic.List<(long time, int delta)>();
        foreach (var note in notes)
        {
            long startTime = note.Time;
            long endTime = note.Time + note.Length;
            events.Add((startTime, 1));
            events.Add((endTime, -1));
        }
        
        // 按时间排序（相同时间时，结束事件在开始事件之前）
        events.Sort((a, b) =>
        {
            int cmp = a.time.CompareTo(b.time);
            if (cmp != 0) return cmp;
            return a.delta.CompareTo(b.delta);
        });
        
        int currentPolyphony = 0;
        int maxPolyphony = 0;
        
        foreach (var (_, delta) in events)
        {
            currentPolyphony += delta;
            if (currentPolyphony > maxPolyphony)
                maxPolyphony = currentPolyphony;
        }
        
        return maxPolyphony;
    }
    
    // 获取指定时间点的 BPM
    public double GetBpmAtTime(double timeMs)
    {
        if (TempoMap == null) return InitialBpm;
        
        try
        {
            var metricTime = new MetricTimeSpan((long)(timeMs * 1000));  // 转换为微秒
            var tempo = TempoMap.GetTempoAtTime(metricTime);
            return 60000000.0 / tempo.MicrosecondsPerQuarterNote;
        }
        catch
        {
            return InitialBpm;
        }
    }
    
    // 获取指定时间点的拍号
    public (int numerator, int denominator) GetTimeSignatureAtTime(double timeMs)
    {
        if (TempoMap == null) return (TimeSignatureNumerator, TimeSignatureDenominator);
        
        try
        {
            var metricTime = new MetricTimeSpan((long)(timeMs * 1000));
            var ts = TempoMap.GetTimeSignatureAtTime(metricTime);
            return (ts.Numerator, ts.Denominator);
        }
        catch
        {
            return (TimeSignatureNumerator, TimeSignatureDenominator);
        }
    }
    
    // 获取一个小节的时长（毫秒）
    public double GetBarDurationMs(double timeMs)
    {
        double bpm = GetBpmAtTime(timeMs);
        var (numerator, denominator) = GetTimeSignatureAtTime(timeMs);
        
        // 一拍的时长（毫秒）
        double beatMs = 60000.0 / bpm;
        
        // 一小节的拍数（根据拍号分母调整）
        // 例如：4/4 = 4 拍，6/8 = 3 拍（因为 8 分音符是半拍）
        double beatsPerBar = numerator * (4.0 / denominator);
        
        return beatMs * beatsPerBar;
    }
}
