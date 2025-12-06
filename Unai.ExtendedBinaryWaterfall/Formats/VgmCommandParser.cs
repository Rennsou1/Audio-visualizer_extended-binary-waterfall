using System;
using System.Collections.Generic;

namespace Unai.ExtendedBinaryWaterfall;

// VGM 命令事件
public struct VgmEvent
{
    public uint Tick;       // Tick
    public byte ChipType;   // 芯片类型
    public byte ChipIndex;  // 芯片实例（0 或 1，用于双芯片）
    public byte Port;       // 端口号/地址高字节
    public byte Register;   // 寄存器地址/地址低字节
    public byte Value;      // 写入值（高字节）
    public byte Value2;     // 写入值（低字节，16位芯片使用）
}

// VGM 命令解析器
public class VgmCommandParser
{
    private readonly List<VgmEvent> _events = new();
    private readonly VgmHeader _header;
    private readonly byte[] _data;
    private readonly byte[] _streamChipType = new byte[16];  // DAC Stream ID 对应的芯片类型
    
    // 芯片类型常量
    public const byte CHIP_SN76489 = 0x01;
    public const byte CHIP_YM2413 = 0x02;
    public const byte CHIP_YM2612 = 0x03;
    public const byte CHIP_YM2151 = 0x04;
    public const byte CHIP_YM2203 = 0x05;
    public const byte CHIP_YM2608 = 0x06;
    public const byte CHIP_YM2610 = 0x07;
    public const byte CHIP_YM3812 = 0x08;
    public const byte CHIP_YM3526 = 0x09;
    public const byte CHIP_Y8950 = 0x0A;
    public const byte CHIP_AY8910 = 0x0B;
    public const byte CHIP_NESAPU = 0x0C;
    public const byte CHIP_GBDMG = 0x0D;
    public const byte CHIP_HUC6280 = 0x0E;
    public const byte CHIP_K051649 = 0x0F;
    public const byte CHIP_POKEY = 0x10;
    public const byte CHIP_QSOUND = 0x11;
    public const byte CHIP_SAA1099 = 0x12;
    public const byte CHIP_SEGAPCM = 0x13;
    public const byte CHIP_OKIM6295 = 0x14;
    public const byte CHIP_RF5C68 = 0x15;
    public const byte CHIP_RF5C164 = 0x16;
    public const byte CHIP_MULTIPCM = 0x17;
    public const byte CHIP_K053260 = 0x18;
    public const byte CHIP_K054539 = 0x19;
    public const byte CHIP_C140 = 0x1A;
    public const byte CHIP_C352 = 0x1B;
    public const byte CHIP_SCSP = 0x1C;
    public const byte CHIP_WSWAN = 0x1D;
    public const byte CHIP_VSU = 0x1E;
    public const byte CHIP_X1010 = 0x1F;
    public const byte CHIP_YMF278B = 0x20;
    public const byte CHIP_YMF271 = 0x21;
    public const byte CHIP_YMZ280B = 0x22;
    public const byte CHIP_GA20 = 0x23;
    public const byte CHIP_ES5503 = 0x24;
    public const byte CHIP_ES5506 = 0x25;
    public const byte CHIP_YMF262 = 0x26;  // OPL3
    public const byte CHIP_PWM = 0x27;
    public const byte CHIP_UPD7759 = 0x28;
    public const byte CHIP_OKIM6258 = 0x29;
    
    public IReadOnlyList<VgmEvent> Events => _events;
    public uint TotalTicks { get; private set; }
    
    public VgmCommandParser(byte[] vgmData, VgmHeader header)
    {
        _data = vgmData;
        _header = header;
    }
    
    // 清理事件列表释放内存
    public void Clear()
    {
        _events.Clear();
        _events.TrimExcess();  // 释放多余容量
        TotalTicks = 0;
    }
    
    // 将 DAC Stream 的芯片类型映射到内部芯片类型常量
    // VGM 规范中的芯片顺序与我们的常量不完全一致
    private byte MapDacChipType(byte dacChipType)
    {
        return dacChipType switch
        {
            0x00 => CHIP_SN76489,
            0x01 => CHIP_YM2413,
            0x02 => CHIP_YM2612,
            0x03 => CHIP_YM2151,
            0x04 => CHIP_SEGAPCM,
            0x05 => CHIP_RF5C68,
            0x06 => CHIP_YM2203,
            0x07 => CHIP_YM2608,
            0x08 => CHIP_YM2610,
            0x09 => CHIP_YM3812,
            0x0A => CHIP_YM3526,
            0x0B => CHIP_Y8950,
            0x0C => CHIP_YMF262,
            0x0D => CHIP_YMF278B,
            0x0E => CHIP_YMF271,
            0x0F => CHIP_YMZ280B,
            0x10 => CHIP_RF5C164,
            0x11 => CHIP_PWM,
            0x12 => CHIP_AY8910,
            0x13 => CHIP_GBDMG,
            0x14 => CHIP_NESAPU,
            0x15 => CHIP_MULTIPCM,
            0x16 => CHIP_UPD7759,
            0x17 => CHIP_OKIM6258,
            0x18 => CHIP_OKIM6295,
            0x19 => CHIP_K051649,
            0x1A => CHIP_K054539,
            0x1B => CHIP_HUC6280,
            0x1C => CHIP_C140,
            0x1D => CHIP_K053260,
            0x1E => CHIP_POKEY,
            0x1F => CHIP_QSOUND,
            0x20 => CHIP_SCSP,
            0x21 => CHIP_WSWAN,
            0x22 => CHIP_VSU,
            0x23 => CHIP_SAA1099,
            0x24 => CHIP_ES5503,
            0x25 => CHIP_ES5506,
            0x26 => CHIP_X1010,
            0x27 => CHIP_C352,
            0x28 => CHIP_GA20,
            _ => 0
        };
    }
    
    // 解析所有命令
    public void Parse()
    {
        _events.Clear();
        // 预估事件数量以减少重新分配（假设平均每4字节一个事件）
        int estimatedEvents = Math.Max(1000, _data.Length / 4);
        if (_events.Capacity < estimatedEvents)
            _events.Capacity = estimatedEvents;
        
        // 计算 VGM 数据起始偏移（版本 < 1.50 固定为 0x40，否则为 DataOffset + 0x34）
        int dataOffset;
        if (_header.Version < 0x150 || _header.DataOffset == 0)
            dataOffset = 0x40;
        else
            dataOffset = (int)(_header.DataOffset + 0x34);
        
        int pos = dataOffset;
        uint tick = 0;
        
        while (pos < _data.Length)
        {
            byte cmd = _data[pos++];
            
            switch (cmd)
            {
                // SN76489 写入
                case 0x50:
                    if (pos < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_SN76489, Register = 0, Value = _data[pos++] });
                    }
                    break;
                
                // YM2413 写入
                case 0x51:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YM2413, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // YM2612 端口 0 写入
                case 0x52:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YM2612, Port = 0, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // YM2612 端口 1 写入
                case 0x53:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YM2612, Port = 1, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // YM2151 写入
                case 0x54:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YM2151, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // YM2203 写入
                case 0x55:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YM2203, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // YM2608 端口 0 写入
                case 0x56:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YM2608, Port = 0, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // YM2608 端口 1 写入
                case 0x57:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YM2608, Port = 1, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // YM2610 端口 0/1 写入
                case 0x58:
                case 0x59:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YM2610, Port = (byte)(cmd - 0x58), Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // YM3812 写入
                case 0x5A:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YM3812, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // YM3526 写入
                case 0x5B:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YM3526, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // AY-3-8910 写入
                case 0xA0:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_AY8910, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // NES APU 写入
                case 0xB4:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_NESAPU, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // Game Boy DMG 写入
                case 0xB3:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_GBDMG, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // HuC6280 写入
                case 0xB9:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_HUC6280, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // K051649 (SCC) 写入
                case 0xD2:
                    if (pos + 2 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_K051649, Port = _data[pos], Register = _data[pos + 1], Value = _data[pos + 2] });
                        pos += 3;
                    }
                    break;
                
                // POKEY 写入
                case 0xBB:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_POKEY, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // QSound 写入: mmll rr (mm=数据MSB, ll=数据LSB, rr=寄存器)
                case 0xC4:
                    if (pos + 2 < _data.Length)
                    {
                        // Register=rr, Port=mm(data high), Value=ll(data low)
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_QSOUND, Register = _data[pos + 2], Port = _data[pos], Value = _data[pos + 1] });
                        pos += 3;
                    }
                    break;
                
                // SAA1099 写入
                case 0xBD:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_SAA1099, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // SegaPCM 写入 (16位地址)
                case 0xC0:
                    if (pos + 2 < _data.Length)
                    {
                        // Port=高字节, Register=低字节
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_SEGAPCM, Port = _data[pos + 1], Register = _data[pos], Value = _data[pos + 2] });
                        pos += 3;
                    }
                    break;
                
                // OKIM6295 写入 (支持双芯片: aa的bit7选择芯片)
                // 但OKIM6295命令本身也需要bit7来区分采样选择和Key Off
                // VGM规范假设OKIM6295采样选择时直接使用完整的aa字节
                case 0xB8:
                    if (pos + 1 < _data.Length)
                    {
                        // 对于OKIM6295，直接传递aa和dd，不处理双芯片选择
                        // （双芯片通过Clock bit30激活，但在实践中很少使用）
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_OKIM6295, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // Y8950 写入
                case 0x5C:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_Y8950, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // YMZ280B 写入
                case 0x5D:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YMZ280B, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // YMF262 (OPL3) 端口 0/1 写入
                case 0x5E:
                case 0x5F:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YMF262, Port = (byte)(cmd - 0x5E), Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // RF5C68 写入
                case 0xB0:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_RF5C68, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // RF5C164 写入
                case 0xB1:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_RF5C164, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // MultiPCM 写入
                case 0xB5:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_MULTIPCM, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // K053260 写入
                case 0xBA:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_K053260, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // WonderSwan 写入
                case 0xBC:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_WSWAN, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // ES5506 8-bit 写入
                case 0xBE:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_ES5506, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // GA20 写入
                case 0xBF:
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_GA20, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // RF5C68 RAM 写入
                case 0xC1:
                    if (pos + 2 < _data.Length)
                    {
                        pos += 3;
                    }
                    break;
                
                // RF5C164 RAM 写入
                case 0xC2:
                    if (pos + 2 < _data.Length)
                    {
                        pos += 3;
                    }
                    break;
                
                // MultiPCM bank
                case 0xC3:
                    if (pos + 2 < _data.Length)
                    {
                        pos += 3;
                    }
                    break;
                
                // SCSP 写入
                case 0xC5:
                    if (pos + 2 < _data.Length)
                    {
                        // mmll dd : mm=高位，ll=低位
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_SCSP, Register = _data[pos], Port = _data[pos + 1], Value = _data[pos + 2] });
                        pos += 3;
                    }
                    break;
                
                // WonderSwan RAM 写入
                case 0xC6:
                    if (pos + 2 < _data.Length)
                    {
                        pos += 3;
                    }
                    break;
                
                // VSU 写入
                case 0xC7:
                    if (pos + 2 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_VSU, Register = _data[pos], Value = _data[pos + 2] });
                        pos += 3;
                    }
                    break;
                
                // X1-010 写入
                case 0xC8:
                    if (pos + 2 < _data.Length)
                    {
                        // mmll dd : mm=高位，ll=低位
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_X1010, Register = _data[pos], Port = _data[pos + 1], Value = _data[pos + 2] });
                        pos += 3;
                    }
                    break;
                
                // YMF278B 写入
                case 0xD0:
                    if (pos + 2 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YMF278B, Port = _data[pos], Register = _data[pos + 1], Value = _data[pos + 2] });
                        pos += 3;
                    }
                    break;
                
                // YMF271 写入
                case 0xD1:
                    if (pos + 2 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YMF271, Port = _data[pos], Register = _data[pos + 1], Value = _data[pos + 2] });
                        pos += 3;
                    }
                    break;
                
                // K054539 写入
                case 0xD3:
                    if (pos + 2 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_K054539, Port = _data[pos], Register = _data[pos + 1], Value = _data[pos + 2] });
                        pos += 3;
                    }
                    break;
                
                // C140 写入
                case 0xD4:
                    if (pos + 2 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_C140, Port = _data[pos], Register = _data[pos + 1], Value = _data[pos + 2] });
                        pos += 3;
                    }
                    break;
                
                // ES5503 写入
                case 0xD5:
                    if (pos + 2 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_ES5503, Port = _data[pos], Register = _data[pos + 1], Value = _data[pos + 2] });
                        pos += 3;
                    }
                    break;
                
                // ES5506 16-bit 写入: aa ddee
                case 0xD6:
                    if (pos + 2 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_ES5506, Register = _data[pos], Value = _data[pos + 1], Value2 = _data[pos + 2] });
                        pos += 3;
                    }
                    break;
                
                // C352 写入: aabb ddee (aa=地址MSB, bb=地址LSB, dd=数据MSB, ee=数据LSB)
                case 0xE1:
                    if (pos + 3 < _data.Length)
                    {
                        // Register=aa, Port=bb, Value=dd, Value2=ee
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_C352, Register = _data[pos], Port = _data[pos + 1], Value = _data[pos + 2], Value2 = _data[pos + 3] });
                        pos += 4;
                    }
                    break;
                
                // 等待 N 采样
                case 0x61:
                    if (pos + 1 < _data.Length)
                    {
                        tick += (uint)(_data[pos] | (_data[pos + 1] << 8));
                        pos += 2;
                    }
                    break;
                
                // 等待 735 采样（60Hz 帧）
                case 0x62:
                    tick += 735;
                    break;
                
                // 等待 882 采样（50Hz 帧）
                case 0x63:
                    tick += 882;
                    break;
                
                // 数据结束
                case 0x66:
                    TotalTicks = tick;
                    return;
                
                // 等待 1-16 采样
                case >= 0x70 and <= 0x7F:
                    tick += (uint)(cmd - 0x6F);
                    break;
                
                // YM2612 端口 0 地址 2A 写入 + 短等待 (DAC 数据)
                case >= 0x80 and <= 0x8F:
                    // 生成 DAC 写入事件 (寄存器 0x2A)
                    _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YM2612, Port = 0, Register = 0x2A, Value = 0x80 });
                    tick += (uint)(cmd - 0x80);
                    break;
                
                // 数据块
                case 0x67:
                    if (pos + 6 < _data.Length)
                    {
                        pos++; // 跳过 0x66
                        byte blockType = _data[pos++];
                        uint blockSize = (uint)(_data[pos] | (_data[pos + 1] << 8) | (_data[pos + 2] << 16) | (_data[pos + 3] << 24));
                        pos += 4 + (int)blockSize;
                    }
                    break;
                
                // PCM RAM 写入: 68 66 cc oo oo oo dd dd dd ss ss ss (12 bytes total)
                // 格式: cc=芯片类型, oo=RAM偏移(3字节), dd=数据块偏移(3字节), ss=大小(3字节)
                case 0x68:
                    if (pos + 10 < _data.Length)
                    {
                        pos++; // 跳过 0x66
                        pos += 10; // cc(1) + oo(3) + dd(3) + ss(3) = 10 bytes
                    }
                    break;
                
                // DAC 流控制命令
                case 0x90:  // Setup Stream: ss tt pp cc
                    if (pos + 3 < _data.Length)
                    {
                        byte streamId = _data[pos];
                        byte chipType = _data[pos + 1];
                        // 记录流对应的芯片类型 (bit 7 用于第二芯片)
                        if (streamId < 16) _streamChipType[streamId] = chipType;
                        pos += 4;
                    }
                    break;
                case 0x91:  // Set Stream Data
                    pos += 4;
                    break;
                case 0x92:  // Set Stream Frequency: ss ff ff ff ff
                    if (pos + 4 < _data.Length)
                    {
                        byte streamId = _data[pos];
                        uint freq = (uint)(_data[pos + 1] | (_data[pos + 2] << 8) | 
                                          (_data[pos + 3] << 16) | (_data[pos + 4] << 24));
                        // 根据流对应的芯片类型生成事件
                        if (streamId < 16 && _streamChipType[streamId] != 0)
                        {
                            byte chipType = (byte)(_streamChipType[streamId] & 0x7F);
                            byte chipIndex = (byte)((_streamChipType[streamId] >> 7) & 0x01);
                            // 使用特殊寄存器 0xFF 表示 DAC 采样率
                            // Value = 频率低 16 位的高字节, Value2 = 频率低 16 位的低字节
                            // Port = 频率高 16 位的低字节, Register = 0xFF (特殊标记)
                            _events.Add(new VgmEvent
                            {
                                Tick = tick,
                                ChipType = MapDacChipType(chipType),
                                ChipIndex = chipIndex,
                                Register = 0xFF,  // 特殊: DAC 采样率
                                Port = (byte)((freq >> 16) & 0xFF),
                                Value = (byte)((freq >> 8) & 0xFF),
                                Value2 = (byte)(freq & 0xFF)
                            });
                        }
                        pos += 5;
                    }
                    break;
                case 0x93:  // Start Stream: ss aa*4 mm ll*4
                    if (pos + 9 < _data.Length)
                    {
                        byte streamId = _data[pos];
                        byte lengthMode = _data[pos + 5];
                        // 为对应芯片生成 Key On 事件 (Register=0xFE表示DAC流开始)
                        if (streamId < 16 && _streamChipType[streamId] != 0)
                        {
                            byte chipType = (byte)(_streamChipType[streamId] & 0x7F);
                            byte chipIndex = (byte)((_streamChipType[streamId] >> 7) & 0x01);
                            _events.Add(new VgmEvent
                            {
                                Tick = tick,
                                ChipType = MapDacChipType(chipType),
                                ChipIndex = chipIndex,
                                Register = 0xFE,  // 特殊: DAC 流开始
                                Value = streamId,
                                Value2 = lengthMode
                            });
                        }
                        pos += 10;
                    }
                    break;
                case 0x94:  // Stop Stream: ss
                    if (pos < _data.Length)
                    {
                        byte streamId = _data[pos];
                        // 为对应芯片生成 Key Off 事件 (Register=0xFD表示DAC流停止)
                        if (streamId < 16 && _streamChipType[streamId] != 0)
                        {
                            byte chipType = (byte)(_streamChipType[streamId] & 0x7F);
                            byte chipIndex = (byte)((_streamChipType[streamId] >> 7) & 0x01);
                            _events.Add(new VgmEvent
                            {
                                Tick = tick,
                                ChipType = MapDacChipType(chipType),
                                ChipIndex = chipIndex,
                                Register = 0xFD,  // 特殊: DAC 流停止
                                Value = streamId
                            });
                        }
                        else if (streamId == 0xFF)
                        {
                            // 0xFF = 停止所有流
                            for (int i = 0; i < 16; i++)
                            {
                                if (_streamChipType[i] != 0)
                                {
                                    byte ct = (byte)(_streamChipType[i] & 0x7F);
                                    byte ci = (byte)((_streamChipType[i] >> 7) & 0x01);
                                    _events.Add(new VgmEvent
                                    {
                                        Tick = tick,
                                        ChipType = MapDacChipType(ct),
                                        ChipIndex = ci,
                                        Register = 0xFD,
                                        Value = (byte)i
                                    });
                                }
                            }
                        }
                        pos += 1;
                    }
                    break;
                case 0x95:  // Start Stream (fast): ss bb bb ff
                    if (pos + 3 < _data.Length)
                    {
                        byte streamId = _data[pos];
                        byte flags = _data[pos + 3];
                        // 为对应芯片生成 Key On 事件
                        if (streamId < 16 && _streamChipType[streamId] != 0)
                        {
                            byte chipType = (byte)(_streamChipType[streamId] & 0x7F);
                            byte chipIndex = (byte)((_streamChipType[streamId] >> 7) & 0x01);
                            _events.Add(new VgmEvent
                            {
                                Tick = tick,
                                ChipType = MapDacChipType(chipType),
                                ChipIndex = chipIndex,
                                Register = 0xFE,  // 特殊: DAC 流开始
                                Value = streamId,
                                Value2 = flags
                            });
                        }
                        pos += 4;
                    }
                    break;
                
                // 双芯片命令 (0x30+xx = 第二芯片的 0x50+xx)
                case 0x30:  // 第二SN76489
                    if (pos < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_SN76489, ChipIndex = 1, Value = _data[pos++] });
                    }
                    break;
                case 0x3F:  // Game Gear PSG stereo (第二芯片)
                    if (pos < _data.Length)
                    {
                        pos++;
                    }
                    break;
                    
                // Game Gear PSG stereo
                case 0x4F:
                    if (pos < _data.Length)
                    {
                        pos++;
                    }
                    break;
                    
                // PWM 写入: a ddd (a=通道, ddd=12位数据)
                case 0xB2:
                    if (pos + 1 < _data.Length)
                    {
                        pos += 2;
                    }
                    break;
                    
                // uPD7759 写入
                case 0xB6:
                    if (pos + 1 < _data.Length)
                    {
                        pos += 2;
                    }
                    break;
                    
                // OKIM6258 写入: aa dd (aa=寄存器, dd=数据)
                case 0xB7:
                    if (pos + 1 < _data.Length)
                    {
                        byte reg = _data[pos];
                        byte val = _data[pos + 1];
                        // bit7 of reg = chip index for dual chip
                        _events.Add(new VgmEvent
                        {
                            Tick = tick,
                            ChipType = CHIP_OKIM6258,
                            ChipIndex = (byte)((reg >> 7) & 0x01),
                            Register = (byte)(reg & 0x7F),
                            Value = val
                        });
                        pos += 2;
                    }
                    break;
                    
                // PCM Seek (设置DAC数据读取位置)
                case 0xE0:
                    if (pos + 3 < _data.Length)
                    {
                        pos += 4;
                    }
                    break;
                
                // 双芯片 YM/AY 系列 (0xA1-0xAF = 第二芯片的 0x51-0x5F)
                case 0xA1:  // 第二YM2413
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YM2413, ChipIndex = 1, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                case 0xA2:  // 第二YM2612 端口0
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YM2612, ChipIndex = 1, Port = 0, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                case 0xA3:  // 第二YM2612 端口1
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YM2612, ChipIndex = 1, Port = 1, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                case 0xA4:  // 第二YM2151
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YM2151, ChipIndex = 1, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                case 0xA5:  // 第二YM2203
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YM2203, ChipIndex = 1, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                case 0xA6:  // 第二YM2608 端口0
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YM2608, ChipIndex = 1, Port = 0, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                case 0xA7:  // 第二YM2608 端口1
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YM2608, ChipIndex = 1, Port = 1, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                case 0xA8:  // 第二YM2610 端口0
                case 0xA9:  // 第二YM2610 端口1
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YM2610, ChipIndex = 1, Port = (byte)(cmd - 0xA8), Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                case 0xAA:  // 第二YM3812
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YM3812, ChipIndex = 1, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                case 0xAB:  // 第二YM3526
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YM3526, ChipIndex = 1, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                case 0xAC:  // 第二Y8950
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_Y8950, ChipIndex = 1, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                case 0xAD:  // 第二YMZ280B
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YMZ280B, ChipIndex = 1, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                case 0xAE:  // 第二YMF262 端口0
                case 0xAF:  // 第二YMF262 端口1
                    if (pos + 1 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_YMF262, ChipIndex = 1, Port = (byte)(cmd - 0xAE), Register = _data[pos], Value = _data[pos + 1] });
                        pos += 2;
                    }
                    break;
                
                // 其他命令（跳过）
                default:
                    // 未知命令，按VGM规范跳过
                    if (cmd >= 0x31 && cmd <= 0x3E)
                        pos += 1;  // 双芯片命令，1操作数
                    else if (cmd >= 0x40 && cmd <= 0x4E)
                        pos += 2;  // 2操作数 (v1.60+)
                    else if (cmd >= 0xC9 && cmd <= 0xCF)
                        pos += 3;  // 3操作数
                    else if (cmd >= 0xD7 && cmd <= 0xDF)
                        pos += 3;  // 3操作数
                    else if (cmd >= 0xE2 && cmd <= 0xFF)
                        pos += 4;  // 4操作数
                    break;
            }
        }
        
        TotalTicks = tick;
    }
    
    // 获取指定时间点的事件索引（二分查找）
    public int GetEventIndexAtTick(uint tick)
    {
        if (_events.Count == 0) return -1;
        
        int lo = 0, hi = _events.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_events[mid].Tick <= tick)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo;
    }
    
    // 将毫秒转换为 VGM tick（44100Hz 采样）
    public static uint MsToTick(double ms)
    {
        return (uint)(ms * 44.1);
    }
    
    // 将 VGM tick 转换为毫秒
    public static double TickToMs(uint tick)
    {
        return tick / 44.1;
    }
}
