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

        // 计算实际 VGM 数据开始位置
        // DataOffset 是相对于 0x34 的偏移，所以实际位置 = DataOffset + 0x34
        // 如果 DataOffset = 0 或版本 < 1.50，数据从 0x40 开始
        uint dataStart = (header.Version >= 0x150 && header.DataOffset > 0) 
            ? header.DataOffset + 0x34 
            : 0x40;
        
        // 根据 VGM 规范：如果数据开始位置小于头部字段位置，该字段应视为 0
        // 辅助函数：检查某个偏移位置的字段是否有效
        bool CanReadAt(uint offset, uint size) => 
            dataStart > offset && data.Length >= offset + size;
        
        // === VGM 1.51+ 字段 (0x38-0x3F): Sega PCM ===
        // 注意：虽然这些字段在 0x40 之前，但只有 1.51+ 版本才支持
        if (header.Version >= 0x151 && CanReadAt(0x38, 8))
        {
            ms.Position = 0x38;
            header.SegaPcmClock = br.ReadUInt32();
            header.SegaPcmIfReg = br.ReadUInt32();
        }
        
        // === VGM 1.51+ 字段 (0x40-0x7F): RF5C68, YM2203, YM2608 等 ===
        if (header.Version >= 0x151 && CanReadAt(0x40, 0x40))
        {
            ms.Position = 0x40;
            header.Rf5c68Clock = br.ReadUInt32();   // 0x40
            header.Ym2203Clock = br.ReadUInt32();   // 0x44
            header.Ym2608Clock = br.ReadUInt32();   // 0x48
            header.Ym2610Clock = br.ReadUInt32();   // 0x4C
            header.Ym3812Clock = br.ReadUInt32();   // 0x50
            header.Ym3526Clock = br.ReadUInt32();   // 0x54
            header.Y8950Clock = br.ReadUInt32();    // 0x58
            header.Ymf262Clock = br.ReadUInt32();   // 0x5C
            header.Ymf278bClock = br.ReadUInt32();  // 0x60
            header.Ymf271Clock = br.ReadUInt32();   // 0x64
            header.Ymz280bClock = br.ReadUInt32();  // 0x68
            header.Rf5c164Clock = br.ReadUInt32();  // 0x6C
            header.PwmClock = br.ReadUInt32();      // 0x70
            header.Ay8910Clock = br.ReadUInt32();   // 0x74
            header.Ay8910Type = br.ReadByte();      // 0x78
            header.Ay8910Flags = br.ReadByte();     // 0x79
            header.Ym2203Ay8910Flags = br.ReadByte(); // 0x7A
            header.Ym2608Ay8910Flags = br.ReadByte(); // 0x7B
            header.VolumeModifier = br.ReadByte();  // 0x7C (VGM 1.60)
            header.Reserved1 = br.ReadByte();       // 0x7D
            header.LoopBase = br.ReadByte();        // 0x7E (VGM 1.60)
            header.LoopModifier = br.ReadByte();    // 0x7F (VGM 1.51)
        }

        // === VGM 1.61+ 字段 (0x80-0xB7): GB DMG, NES APU, MultiPCM 等 ===
        if (header.Version >= 0x161 && CanReadAt(0x80, 0x38))
        {
            ms.Position = 0x80; // 确保从正确位置开始
            header.GbDmgClock = br.ReadUInt32();    // 0x80
            header.NesApuClock = br.ReadUInt32();   // 0x84
            header.MultiPcmClock = br.ReadUInt32(); // 0x88
            header.Upd7759Clock = br.ReadUInt32();  // 0x8C
            header.Okim6258Clock = br.ReadUInt32(); // 0x90
            header.Okim6258Flags = br.ReadByte();   // 0x94
            header.K054539Flags = br.ReadByte();    // 0x95
            header.C140Type = br.ReadByte();        // 0x96
            header.Reserved2 = br.ReadByte();       // 0x97
            header.Okim6295Clock = br.ReadUInt32(); // 0x98
            header.K051649Clock = br.ReadUInt32();  // 0x9C
            header.K054539Clock = br.ReadUInt32();  // 0xA0
            header.HuC6280Clock = br.ReadUInt32();  // 0xA4
            header.C140Clock = br.ReadUInt32();     // 0xA8
            header.K053260Clock = br.ReadUInt32();  // 0xAC
            header.PokeyClock = br.ReadUInt32();    // 0xB0
            header.QsoundClock = br.ReadUInt32();   // 0xB4
        }
        
        // === VGM 1.71+ 字段 (0xB8): SCSP ===
        if (header.Version >= 0x171 && CanReadAt(0xB8, 4))
        {
            ms.Position = 0xB8;
            header.ScspClock = br.ReadUInt32();     // 0xB8
        }
        
        // === VGM 1.70+ 字段 (0xBC): Extra Header Offset ===
        if (header.Version >= 0x170 && CanReadAt(0xBC, 4))
        {
            ms.Position = 0xBC;
            header.ExtraHdrOffset = br.ReadUInt32(); // 0xBC
        }

        // === VGM 1.71+ 字段 (0xC0-0xEF): WonderSwan, VSU, SAA1099 等 ===
        if (header.Version >= 0x171 && CanReadAt(0xC0, 0x28))
        {
            ms.Position = 0xC0;
            header.Wswan_Clock = br.ReadUInt32();   // 0xC0
            header.Vsu_Clock = br.ReadUInt32();     // 0xC4
            header.Saa1099Clock = br.ReadUInt32();  // 0xC8
            header.Es5503Clock = br.ReadUInt32();   // 0xCC
            header.Es5506Clock = br.ReadUInt32();   // 0xD0
            header.Es5503Channels = br.ReadByte();  // 0xD4
            header.Es5506Channels = br.ReadByte();  // 0xD5
            header.C352ClockDiv = br.ReadByte();    // 0xD6
            header.Reserved3 = br.ReadByte();       // 0xD7
            header.X1_010Clock = br.ReadUInt32();   // 0xD8
            header.C352Clock = br.ReadUInt32();     // 0xDC
            header.Ga20Clock = br.ReadUInt32();     // 0xE0
            header.Mikey_Clock = br.ReadUInt32();   // 0xE4 (VGM 1.72 中添加，为兼容性保留)
        }

        // 修正 DataOffset（版本 < 1.50 固定为 0x40）
        if (header.Version < 0x150 || header.DataOffset == 0)
            header.DataOffset = 0x0C; // 相对于 0x34，即数据从 0x40 开始
        
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
        // YM2610/YM2610B: bit31=1表示YM2610B (6 FM), bit31=0表示YM2610 (4 FM)
        // 但有些VGM文件没有正确设置bit31，所以统一使用16通道以支持动态检测
        bool isYm2610B = (header.Ym2610Clock & 0x80000000) != 0;
        AddChip(isYm2610B ? "YM2610B" : "YM2610", header.Ym2610Clock & 0x7FFFFFFF, 16);
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

    // 获取系统名称（优先使用 GD3 标签中的系统名，否则根据芯片推断）
    public static string GetSystemName(VgmHeader header, Gd3Tag gd3 = null, Gd3Language lang = Gd3Language.English)
    {
        // 优先使用 GD3 标签中的系统名称
        if (gd3 != null)
        {
            string systemName = gd3.GetSystemName(lang);
            if (!string.IsNullOrWhiteSpace(systemName))
                return systemName;
        }
        
        // GD3 不可用时，根据芯片组合推断系统
        return GetSystemNameFromChips(header);
    }
    
    // 根据芯片组合推断系统名称
    public static string GetSystemNameFromChips(VgmHeader header)
    {
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
        if (header.C352Clock > 0)
            return "Namco Arcade";
        if (header.ScspClock > 0)
            return "Sega Saturn / Model 2";
        if (header.Wswan_Clock > 0)
            return "Bandai WonderSwan";
        if (header.PokeyClock > 0)
            return "Atari 8-bit";
        
        return "Unknown System";
    }
}
