using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Unai.ExtendedBinaryWaterfall;

// VGM 文件头结构（v1.71 规范）
public struct VgmHeader
{
    public uint FileId;           // "Vgm " (0x206D6756)
    public uint EofOffset;        // 文件结束偏移
    public uint Version;          // 版本号 (BCD: 0x171 = 1.71)
    public uint Sn76489Clock;     // SN76489 时钟
    public uint Ym2413Clock;      // YM2413 (OPLL) 时钟
    public uint Gd3Offset;        // GD3 标签偏移
    public uint TotalSamples;     // 总采样数
    public uint LoopOffset;       // 循环偏移
    public uint LoopSamples;      // 循环采样数
    public uint Rate;             // 录制速率 (Hz)
    public ushort Sn76489Feedback; // SN76489 反馈
    public byte Sn76489ShiftWidth; // SN76489 移位宽度
    public byte Sn76489Flags;     // SN76489 标志
    public uint Ym2612Clock;      // YM2612 (OPN2) 时钟
    public uint Ym2151Clock;      // YM2151 (OPM) 时钟
    public uint DataOffset;       // VGM 数据偏移
    public uint SegaPcmClock;     // SegaPCM 时钟
    public uint SegaPcmIfReg;     // SegaPCM 接口寄存器
    public uint Rf5c68Clock;      // RF5C68 时钟
    public uint Ym2203Clock;      // YM2203 (OPN) 时钟
    public uint Ym2608Clock;      // YM2608 (OPNA) 时钟
    public uint Ym2610Clock;      // YM2610 (OPNB) 时钟
    public uint Ym3812Clock;      // YM3812 (OPL2) 时钟
    public uint Ym3526Clock;      // YM3526 (OPL) 时钟
    public uint Y8950Clock;       // Y8950 时钟
    public uint Ymf262Clock;      // YMF262 (OPL3) 时钟
    public uint Ymf278bClock;     // YMF278B (OPL4) 时钟
    public uint Ymf271Clock;      // YMF271 时钟
    public uint Ymz280bClock;     // YMZ280B 时钟
    public uint Rf5c164Clock;     // RF5C164 时钟
    public uint PwmClock;         // PWM 时钟
    public uint Ay8910Clock;      // AY-3-8910 时钟
    public byte Ay8910Type;       // AY8910 芯片类型
    public byte Ay8910Flags;      // AY8910 标志
    public byte Ym2203Ay8910Flags;// YM2203 SSG 标志
    public byte Ym2608Ay8910Flags;// YM2608 SSG 标志
    public byte VolumeModifier;   // 音量修正
    public byte Reserved1;
    public byte LoopBase;         // 循环基数
    public byte LoopModifier;     // 循环修正
    public uint GbDmgClock;       // Game Boy DMG 时钟
    public uint NesApuClock;      // NES APU 时钟
    public uint MultiPcmClock;    // MultiPCM 时钟
    public uint Upd7759Clock;     // uPD7759 时钟
    public uint Okim6258Clock;    // OKIM6258 时钟
    public byte Okim6258Flags;    // OKIM6258 标志
    public byte K054539Flags;     // K054539 标志
    public byte C140Type;         // C140 芯片类型
    public byte Reserved2;
    public uint Okim6295Clock;    // OKIM6295 时钟
    public uint K051649Clock;     // K051649 (SCC) 时钟
    public uint K054539Clock;     // K054539 时钟
    public uint HuC6280Clock;     // HuC6280 时钟
    public uint C140Clock;        // C140 时钟
    public uint K053260Clock;     // K053260 时钟
    public uint PokeyClock;       // POKEY 时钟
    public uint QsoundClock;      // QSound 时钟
    public uint ScspClock;        // SCSP 时钟
    public uint ExtraHdrOffset;   // 扩展头偏移
    public uint Wswan_Clock;      // WonderSwan 时钟
    public uint Vsu_Clock;        // VSU 时钟
    public uint Saa1099Clock;     // SAA1099 时钟
    public uint Es5503Clock;      // ES5503 时钟
    public uint Es5506Clock;      // ES5506 时钟
    public byte Es5503Channels;   // ES5503 通道数 (0xD4)
    public byte Es5506Channels;   // ES5506 通道数 (0xD5)
    public byte C352ClockDiv;     // C352 时钟分频 (0xD6)
    public byte Reserved3;        // 保留字节 (0xD7)
    public uint X1_010Clock;      // X1-010 时钟 (0xD8)
    public uint C352Clock;        // C352 时钟
    public uint Ga20Clock;        // GA20 时钟
    public uint Mikey_Clock;      // Atari Lynx 时钟
}

// GD3 语言选项
public enum Gd3Language
{
    English = 0,
    Japanese = 1
}

// GD3 标签信息
public class Gd3Tag
{
    public string TrackNameEn { get; set; } = "";
    public string TrackNameJp { get; set; } = "";
    public string GameNameEn { get; set; } = "";
    public string GameNameJp { get; set; } = "";
    public string SystemNameEn { get; set; } = "";
    public string SystemNameJp { get; set; } = "";
    public string AuthorEn { get; set; } = "";
    public string AuthorJp { get; set; } = "";
    public string ReleaseDate { get; set; } = "";
    public string Converter { get; set; } = "";
    public string Notes { get; set; } = "";

    // 根据语言获取曲目名（优先选择指定语言，fallback 到另一种语言）
    public string GetTrackName(Gd3Language lang) => 
        lang == Gd3Language.Japanese 
            ? (!string.IsNullOrEmpty(TrackNameJp) ? TrackNameJp : TrackNameEn)
            : (!string.IsNullOrEmpty(TrackNameEn) ? TrackNameEn : TrackNameJp);

    // 根据语言获取游戏名
    public string GetGameName(Gd3Language lang) => 
        lang == Gd3Language.Japanese 
            ? (!string.IsNullOrEmpty(GameNameJp) ? GameNameJp : GameNameEn)
            : (!string.IsNullOrEmpty(GameNameEn) ? GameNameEn : GameNameJp);

    // 根据语言获取系统名
    public string GetSystemName(Gd3Language lang) => 
        lang == Gd3Language.Japanese 
            ? (!string.IsNullOrEmpty(SystemNameJp) ? SystemNameJp : SystemNameEn)
            : (!string.IsNullOrEmpty(SystemNameEn) ? SystemNameEn : SystemNameJp);

    // 根据语言获取作者名
    public string GetAuthor(Gd3Language lang) => 
        lang == Gd3Language.Japanese 
            ? (!string.IsNullOrEmpty(AuthorJp) ? AuthorJp : AuthorEn)
            : (!string.IsNullOrEmpty(AuthorEn) ? AuthorEn : AuthorJp);
}

// VGM 芯片信息
public class VgmChipInfo
{
    public string Name { get; set; } = "";
    public uint Clock { get; set; }
    public int ChannelCount { get; set; }
    public bool IsDualChip { get; set; }
}

// VGM 文件解析器
public static class VgmFormat
{
    public const uint VGM_MAGIC = 0x206D6756; // "Vgm "
    public const uint GD3_MAGIC = 0x20336447; // "Gd3 "

    // 从文件加载 VGM（支持 VGZ 压缩格式）
    public static byte[] LoadVgmFile(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        
        // 检测 GZip 压缩（VGZ 格式）
        if (data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B)
        {
            using var ms = new MemoryStream(data);
            using var gzip = new GZipStream(ms, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
        
        return data;
    }

    // 解析 VGM 头部
    public static VgmHeader ParseHeader(byte[] data)
    {
        if (data.Length < 0x40)
            throw new InvalidDataException("VGM file too small");

        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);

        var header = new VgmHeader
        {
            FileId = br.ReadUInt32(),
            EofOffset = br.ReadUInt32(),
            Version = br.ReadUInt32(),
            Sn76489Clock = br.ReadUInt32(),
            Ym2413Clock = br.ReadUInt32(),
            Gd3Offset = br.ReadUInt32(),
            TotalSamples = br.ReadUInt32(),
            LoopOffset = br.ReadUInt32(),
            LoopSamples = br.ReadUInt32(),
            Rate = br.ReadUInt32(),
            Sn76489Feedback = br.ReadUInt16(),
            Sn76489ShiftWidth = br.ReadByte(),
            Sn76489Flags = br.ReadByte(),
            Ym2612Clock = br.ReadUInt32(),
            Ym2151Clock = br.ReadUInt32(),
            DataOffset = br.ReadUInt32(),
        };

        if (header.FileId != VGM_MAGIC)
            throw new InvalidDataException("Invalid VGM file signature");

        // 计算实际头部大小：VGM数据偏移+0x34，或者0x40（如果DataOffset=0或版本<1.50）
        uint headerSize = header.DataOffset > 0 ? header.DataOffset + 0x34 : 0x40;
        
        // 版本 1.50+ 有更多字段（但必须在头部范围内）
        if (header.Version >= 0x150 && headerSize >= 0x80 && data.Length >= 0x80)
        {
            header.SegaPcmClock = br.ReadUInt32();
            header.SegaPcmIfReg = br.ReadUInt32();
            header.Rf5c68Clock = br.ReadUInt32();
            header.Ym2203Clock = br.ReadUInt32();
            header.Ym2608Clock = br.ReadUInt32();
            header.Ym2610Clock = br.ReadUInt32();
            header.Ym3812Clock = br.ReadUInt32();
            header.Ym3526Clock = br.ReadUInt32();
            header.Y8950Clock = br.ReadUInt32();
            header.Ymf262Clock = br.ReadUInt32();
            header.Ymf278bClock = br.ReadUInt32();
            header.Ymf271Clock = br.ReadUInt32();
            header.Ymz280bClock = br.ReadUInt32();
            header.Rf5c164Clock = br.ReadUInt32();
            header.PwmClock = br.ReadUInt32();
            header.Ay8910Clock = br.ReadUInt32();
            header.Ay8910Type = br.ReadByte();
            header.Ay8910Flags = br.ReadByte();
            header.Ym2203Ay8910Flags = br.ReadByte();
            header.Ym2608Ay8910Flags = br.ReadByte();
            header.VolumeModifier = br.ReadByte();
            header.Reserved1 = br.ReadByte();
            header.LoopBase = br.ReadByte();
            header.LoopModifier = br.ReadByte();
        }

        // 版本 1.51+ 有更多字段（0x80-0xBF 区域）
        if (header.Version >= 0x151 && headerSize >= 0xC0 && data.Length >= 0xC0)
        {
            header.GbDmgClock = br.ReadUInt32();
            header.NesApuClock = br.ReadUInt32();
            header.MultiPcmClock = br.ReadUInt32();
            header.Upd7759Clock = br.ReadUInt32();
            header.Okim6258Clock = br.ReadUInt32();
            header.Okim6258Flags = br.ReadByte();
            header.K054539Flags = br.ReadByte();
            header.C140Type = br.ReadByte();
            header.Reserved2 = br.ReadByte();
            header.Okim6295Clock = br.ReadUInt32();
            header.K051649Clock = br.ReadUInt32();
            header.K054539Clock = br.ReadUInt32();
            header.HuC6280Clock = br.ReadUInt32();
            header.C140Clock = br.ReadUInt32();
            header.K053260Clock = br.ReadUInt32();
            header.PokeyClock = br.ReadUInt32();
            header.QsoundClock = br.ReadUInt32();
        }

        // 版本 1.70+ 有更多字段（0xC0-0xFF 区域）
        if (header.Version >= 0x170 && headerSize >= 0x100 && data.Length >= 0x100)
        {
            header.ScspClock = br.ReadUInt32();
            header.ExtraHdrOffset = br.ReadUInt32();
            header.Wswan_Clock = br.ReadUInt32();
            header.Vsu_Clock = br.ReadUInt32();
            header.Saa1099Clock = br.ReadUInt32();
            header.Es5503Clock = br.ReadUInt32();
            header.Es5506Clock = br.ReadUInt32();
            header.Es5503Channels = br.ReadByte();
            header.Es5506Channels = br.ReadByte();
            header.C352ClockDiv = br.ReadByte();
            header.Reserved3 = br.ReadByte();
            header.X1_010Clock = br.ReadUInt32();
            header.C352Clock = br.ReadUInt32();
            header.Ga20Clock = br.ReadUInt32();
            header.Mikey_Clock = br.ReadUInt32();
        }

        // 修正 DataOffset（版本 < 1.50 固定为 0x40）
        if (header.Version < 0x150 || header.DataOffset == 0)
            header.DataOffset = 0x0C; // 相对于 0x34
        
        return header;
    }

    // 解析 GD3 标签
    public static Gd3Tag ParseGd3(byte[] data, uint gd3Offset)
    {
        var tag = new Gd3Tag();
        
        if (gd3Offset == 0) return tag;
        
        // GD3 偏移是相对于 0x14 的
        uint absOffset = gd3Offset + 0x14;
        if (absOffset + 12 >= data.Length) return tag;

        using var ms = new MemoryStream(data);
        ms.Position = absOffset;
        using var br = new BinaryReader(ms);

        uint magic = br.ReadUInt32();
        if (magic != GD3_MAGIC) return tag;

        uint version = br.ReadUInt32();
        uint length = br.ReadUInt32();

        if (absOffset + 12 + length > data.Length) return tag;

        // 读取 UTF-16LE 字符串
        byte[] stringData = br.ReadBytes((int)length);
        string[] strings = Encoding.Unicode.GetString(stringData)
            .Split('\0', StringSplitOptions.None);

        if (strings.Length > 0) tag.TrackNameEn = strings[0];
        if (strings.Length > 1) tag.TrackNameJp = strings[1];
        if (strings.Length > 2) tag.GameNameEn = strings[2];
        if (strings.Length > 3) tag.GameNameJp = strings[3];
        if (strings.Length > 4) tag.SystemNameEn = strings[4];
        if (strings.Length > 5) tag.SystemNameJp = strings[5];
        if (strings.Length > 6) tag.AuthorEn = strings[6];
        if (strings.Length > 7) tag.AuthorJp = strings[7];
        if (strings.Length > 8) tag.ReleaseDate = strings[8];
        if (strings.Length > 9) tag.Converter = strings[9];
        if (strings.Length > 10) tag.Notes = strings[10];

        return tag;
    }

    // 获取 VGM 中使用的芯片列表
    public static VgmChipInfo[] GetChipList(VgmHeader header)
    {
        var chips = new System.Collections.Generic.List<VgmChipInfo>();

        void AddChip(string name, uint clock, int channels)
        {
            if (clock == 0) return;
            bool dual = (clock & 0x40000000) != 0;
            chips.Add(new VgmChipInfo
            {
                Name = name,
                Clock = clock & 0x3FFFFFFF,
                ChannelCount = channels,
                IsDualChip = dual
            });
        }

        AddChip("SN76489", header.Sn76489Clock, 4);
        AddChip("YM2413", header.Ym2413Clock, 9);
        AddChip("YM2612", header.Ym2612Clock, 6);
        AddChip("YM2151", header.Ym2151Clock, 8);
        AddChip("SegaPCM", header.SegaPcmClock, 16);
        AddChip("RF5C68", header.Rf5c68Clock, 8);
        AddChip("YM2203", header.Ym2203Clock, 6);
        AddChip("YM2608", header.Ym2608Clock, 16);
        AddChip("YM2610", header.Ym2610Clock, 14);
        AddChip("YM3812", header.Ym3812Clock, 9);
        AddChip("YM3526", header.Ym3526Clock, 9);
        AddChip("Y8950", header.Y8950Clock, 9);
        AddChip("YMF262", header.Ymf262Clock, 18);
        AddChip("YMF278B", header.Ymf278bClock, 24);
        AddChip("YMF271", header.Ymf271Clock, 12);
        AddChip("YMZ280B", header.Ymz280bClock, 8);
        AddChip("RF5C164", header.Rf5c164Clock, 8);
        AddChip("PWM", header.PwmClock, 2);
        AddChip("AY-3-8910", header.Ay8910Clock, 3);
        AddChip("GB DMG", header.GbDmgClock, 4);
        AddChip("NES APU", header.NesApuClock, 5);
        AddChip("MultiPCM", header.MultiPcmClock, 28);
        AddChip("uPD7759", header.Upd7759Clock, 1);
        AddChip("OKIM6258", header.Okim6258Clock, 1);
        AddChip("OKIM6295", header.Okim6295Clock, 4);
        AddChip("K051649", header.K051649Clock, 5);
        AddChip("K054539", header.K054539Clock, 8);
        AddChip("HuC6280", header.HuC6280Clock, 6);
        AddChip("C140", header.C140Clock, 24);
        AddChip("K053260", header.K053260Clock, 4);
        AddChip("POKEY", header.PokeyClock, 4);
        AddChip("QSound", header.QsoundClock, 16);
        AddChip("SCSP", header.ScspClock, 32);
        AddChip("WonderSwan", header.Wswan_Clock, 4);
        AddChip("VSU", header.Vsu_Clock, 6);
        AddChip("SAA1099", header.Saa1099Clock, 6);
        AddChip("ES5503", header.Es5503Clock, 32);
        AddChip("ES5506", header.Es5506Clock, 32);
        AddChip("X1-010", header.X1_010Clock, 16);
        AddChip("C352", header.C352Clock, 32);
        AddChip("GA20", header.Ga20Clock, 4);

        return chips.ToArray();
    }

    // 计算 VGM 数据起始偏移（绝对位置）
    public static uint GetDataOffset(VgmHeader header)
    {
        if (header.Version < 0x150)
            return 0x40;
        return header.DataOffset + 0x34;
    }

    // 计算播放时长（秒）
    public static double GetDurationSeconds(VgmHeader header)
    {
        return header.TotalSamples / 44100.0;
    }

    // 从文件路径加载头部（便捷方法）
    public static VgmHeader LoadHeader(string path)
    {
        byte[] data = LoadVgmFile(path);
        return ParseHeader(data);
    }

    // 获取系统名称
    public static string GetSystemName(VgmHeader header)
    {
        // 根据芯片组合推断系统
        if (header.Ym2612Clock > 0 && header.Sn76489Clock > 0)
            return "Sega Genesis / Mega Drive";
        if (header.Ym2612Clock > 0)
            return "Sega Genesis / Mega Drive";
        if (header.Ym2151Clock > 0 && header.SegaPcmClock > 0)
            return "Sega Arcade";
        if (header.Ym2151Clock > 0)
            return "Arcade / X68000";
        if (header.Ym2608Clock > 0)
            return "NEC PC-9801";
        if (header.Ym2203Clock > 0)
            return "NEC PC-8801";
        if (header.NesApuClock > 0)
            return "Nintendo Entertainment System";
        if (header.GbDmgClock > 0)
            return "Nintendo Game Boy";
        if (header.HuC6280Clock > 0)
            return "NEC PC Engine";
        if (header.Sn76489Clock > 0)
            return "Sega Master System";
        if (header.QsoundClock > 0)
            return "Capcom CPS Arcade";
        if (header.Ay8910Clock > 0)
            return "MSX / ZX Spectrum";
        
        return "Unknown System";
    }
}
