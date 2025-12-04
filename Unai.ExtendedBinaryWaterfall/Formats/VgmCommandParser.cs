using System;
using System.Collections.Generic;

namespace Unai.ExtendedBinaryWaterfall;

// VGM 命令事件
public struct VgmEvent
{
    public uint Tick;       // Tick
    public byte ChipType;   // 芯片类型
    public byte ChipIndex;  // 芯片实例（0 或 1，用于双芯片）
    public byte Port;       // 端口号（部分芯片有多个端口）
    public byte Register;   // 寄存器地址
    public byte Value;      // 写入值
}

// VGM 命令解析器
public class VgmCommandParser
{
    private readonly List<VgmEvent> _events = new();
    private readonly VgmHeader _header;
    private readonly byte[] _data;
    
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
    
    public IReadOnlyList<VgmEvent> Events => _events;
    public uint TotalTicks { get; private set; }
    
    public VgmCommandParser(byte[] vgmData, VgmHeader header)
    {
        _data = vgmData;
        _header = header;
    }
    
    // 解析所有命令
    public void Parse()
    {
        _events.Clear();
        
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
                
                // QSound 写入
                case 0xC4:
                    if (pos + 2 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_QSOUND, Register = _data[pos], Value = _data[pos + 1] });
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
                
                // SegaPCM 写入
                case 0xC0:
                    if (pos + 2 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_SEGAPCM, Register = (byte)(_data[pos] | (_data[pos + 1] << 8)), Value = _data[pos + 2] });
                        pos += 3;
                    }
                    break;
                
                // OKIM6295 写入
                case 0xB8:
                    if (pos + 1 < _data.Length)
                    {
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
                
                // ES5506 16-bit 写入
                case 0xD6:
                    if (pos + 2 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_ES5506, Register = _data[pos], Value = _data[pos + 1] });
                        pos += 3;
                    }
                    break;
                
                // C352 写入
                case 0xE1:
                    if (pos + 3 < _data.Length)
                    {
                        _events.Add(new VgmEvent { Tick = tick, ChipType = CHIP_C352, Register = _data[pos], Value = _data[pos + 2] });
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
                
                // YM2612 端口 0 写入 + 短等待
                case >= 0x80 and <= 0x8F:
                    // 写入 DAC 数据并等待
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
                
                // 其他命令（跳过）
                default:
                    // 未知命令，尝试跳过
                    if (cmd >= 0x30 && cmd <= 0x4E)
                        pos += 1;
                    else if (cmd >= 0x40 && cmd <= 0x4E)
                        pos += 2;
                    else if (cmd >= 0xC0 && cmd <= 0xDF)
                        pos += 3;
                    else if (cmd >= 0xE0 && cmd <= 0xFF)
                        pos += 4;
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
