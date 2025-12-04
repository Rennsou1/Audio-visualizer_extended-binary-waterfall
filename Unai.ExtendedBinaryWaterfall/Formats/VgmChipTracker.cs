using System;

namespace Unai.ExtendedBinaryWaterfall;

// 芯片状态追踪器基类
public abstract class VgmChipTracker
{
    public abstract void ProcessEvent(VgmEvent evt);
    public abstract void Reset();
    public abstract void UpdateVisualizerState(VgmVisualizer.ChipState state);
}

// YM2612 状态追踪器（Mega Drive/Genesis FM 芯片）
public class YM2612Tracker : VgmChipTracker
{
    // 6 个 FM 通道 + 1 个 DAC
    private readonly int[] _fnum = new int[6];
    private readonly int[] _block = new int[6];
    private readonly int[] _tl = new int[6];      // 总电平（音量）
    private readonly bool[] _keyOn = new bool[6];
    private bool _dacEnable;
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        int port = evt.Port;
        int chOffset = port * 3;
        
        // Key On/Off 寄存器 (0x28)
        if (reg == 0x28)
        {
            int ch = val & 0x07;
            if (ch == 4) ch = 3;
            else if (ch == 5) ch = 4;
            else if (ch == 6) ch = 5;
            else if (ch > 2) return;
            
            _keyOn[ch] = (val & 0xF0) != 0;
        }
        // 频率 LSB (0xA0-0xA2)
        else if (reg >= 0xA0 && reg <= 0xA2)
        {
            int ch = (reg - 0xA0) + chOffset;
            if (ch < 6) _fnum[ch] = (_fnum[ch] & 0x700) | val;
        }
        // 频率 MSB + Block (0xA4-0xA6)
        else if (reg >= 0xA4 && reg <= 0xA6)
        {
            int ch = (reg - 0xA4) + chOffset;
            if (ch < 6)
            {
                _fnum[ch] = (_fnum[ch] & 0xFF) | ((val & 0x07) << 8);
                _block[ch] = (val >> 3) & 0x07;
            }
        }
        // 总电平 TL (Op1: 0x40-0x42, 0x44-0x46)
        else if (reg >= 0x40 && reg <= 0x4F)
        {
            int ch = ((reg - 0x40) % 4) + chOffset;
            if ((reg - 0x40) / 4 == 0 && ch < 6)
            {
                _tl[ch] = val & 0x7F;
            }
        }
        // DAC 使能 (0x2B)
        else if (reg == 0x2B)
        {
            _dacEnable = (val & 0x80) != 0;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_fnum);
        Array.Clear(_block);
        Array.Clear(_tl);
        Array.Clear(_keyOn);
        _dacEnable = false;
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 6; ch++)
        {
            if (ch >= state.Channels.Length) break;
            
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = Math.Max(0, 127 - _tl[ch]);
            
            if (_keyOn[ch] && _fnum[ch] > 0)
            {
                state.Channels[ch].Note = FnumToNote(_fnum[ch], _block[ch]);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
        
        // DAC 通道
        if (state.Channels.Length > 6)
        {
            state.Channels[6].KeyOn = _dacEnable;
            state.Channels[6].Note = -1;
            state.Channels[6].Volume = _dacEnable ? 127 : 0;
        }
    }
    
    // YM2612 F-Number 转音符
    private static int FnumToNote(int fnum, int block)
    {
        if (fnum == 0) return -1;
        // YM2612: freq = fnum * clock / (144 * 2^(21-block))
        // 假设 clock = 7670454 Hz
        double freq = fnum * 7670454.0 / (144.0 * Math.Pow(2, 21 - block));
        return VgmVisualizer.FrequencyToNote(freq);
    }
}

// SN76489 状态追踪器（PSG 芯片）
public class SN76489Tracker : VgmChipTracker
{
    private readonly int[] _tone = new int[3];   // 音调寄存器
    private readonly int[] _volume = new int[4]; // 音量（0-15，15=静音）
    private int _latchedChannel;
    private bool _latchedVolume;
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte val = evt.Value;
        
        if ((val & 0x80) != 0)
        {
            // Latch/Data 字节
            _latchedChannel = (val >> 5) & 0x03;
            _latchedVolume = (val & 0x10) != 0;
            
            if (_latchedVolume)
            {
                _volume[_latchedChannel] = val & 0x0F;
            }
            else if (_latchedChannel < 3)
            {
                _tone[_latchedChannel] = (_tone[_latchedChannel] & 0x3F0) | (val & 0x0F);
            }
        }
        else
        {
            // Data 字节
            if (!_latchedVolume && _latchedChannel < 3)
            {
                _tone[_latchedChannel] = (_tone[_latchedChannel] & 0x0F) | ((val & 0x3F) << 4);
            }
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_tone);
        for (int i = 0; i < 4; i++) _volume[i] = 15;
        _latchedChannel = 0;
        _latchedVolume = false;
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 3 && ch < state.Channels.Length; ch++)
        {
            bool active = _volume[ch] < 15 && _tone[ch] > 0;
            state.Channels[ch].KeyOn = active;
            state.Channels[ch].Volume = (15 - _volume[ch]) * 127 / 15;
            
            if (active)
            {
                state.Channels[ch].Note = PeriodToNote(_tone[ch]);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
        
        // 噪声通道
        if (state.Channels.Length > 3)
        {
            state.Channels[3].KeyOn = _volume[3] < 15;
            state.Channels[3].Volume = (15 - _volume[3]) * 127 / 15;
            state.Channels[3].Note = -1;
        }
    }
    
    // SN76489 周期转音符（假设 3579545 Hz 时钟）
    private static int PeriodToNote(int period)
    {
        if (period == 0) return -1;
        double freq = 3579545.0 / (32.0 * period);
        return VgmVisualizer.FrequencyToNote(freq);
    }
}

// AY-3-8910 状态追踪器
public class AY8910Tracker : VgmChipTracker
{
    private readonly int[] _tonePeriod = new int[3];
    private readonly int[] _volume = new int[3];
    private readonly bool[] _toneEnable = new bool[3];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        // 音调周期低字节
        if (reg <= 0x05)
        {
            int ch = reg / 2;
            if ((reg & 1) == 0)
                _tonePeriod[ch] = (_tonePeriod[ch] & 0xF00) | val;
            else
                _tonePeriod[ch] = (_tonePeriod[ch] & 0x0FF) | ((val & 0x0F) << 8);
        }
        // 混音器控制
        else if (reg == 0x07)
        {
            _toneEnable[0] = (val & 0x01) == 0;
            _toneEnable[1] = (val & 0x02) == 0;
            _toneEnable[2] = (val & 0x04) == 0;
        }
        // 音量
        else if (reg >= 0x08 && reg <= 0x0A)
        {
            _volume[reg - 0x08] = val & 0x0F;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_tonePeriod);
        Array.Clear(_volume);
        Array.Clear(_toneEnable);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 3 && ch < state.Channels.Length; ch++)
        {
            bool active = _toneEnable[ch] && _volume[ch] > 0 && _tonePeriod[ch] > 0;
            state.Channels[ch].KeyOn = active;
            state.Channels[ch].Volume = _volume[ch] * 127 / 15;
            
            if (active)
            {
                // 假设 1.78 MHz 时钟
                double freq = 1789773.0 / (16.0 * _tonePeriod[ch]);
                state.Channels[ch].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
    }
}

// YM2151 状态追踪器（OPM 芯片）
public class YM2151Tracker : VgmChipTracker
{
    private readonly int[] _kc = new int[8];     // Key Code
    private readonly int[] _kf = new int[8];     // Key Fraction
    private readonly int[] _tl = new int[8];     // 总电平
    private readonly bool[] _keyOn = new bool[8];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        // Key On/Off (0x08)
        if (reg == 0x08)
        {
            int ch = val & 0x07;
            _keyOn[ch] = (val & 0x78) != 0;
        }
        // Key Code (0x28-0x2F)
        else if (reg >= 0x28 && reg <= 0x2F)
        {
            int ch = reg & 0x07;
            _kc[ch] = val;
        }
        // Key Fraction (0x30-0x37)
        else if (reg >= 0x30 && reg <= 0x37)
        {
            int ch = reg & 0x07;
            _kf[ch] = (val >> 2) & 0x3F;
        }
        // TL Operator 1 (0x60-0x67)
        else if (reg >= 0x60 && reg <= 0x67)
        {
            int ch = reg & 0x07;
            _tl[ch] = val & 0x7F;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_kc);
        Array.Clear(_kf);
        Array.Clear(_tl);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 8 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = Math.Max(0, 127 - _tl[ch]);
            
            if (_keyOn[ch])
            {
                state.Channels[ch].Note = KcToNote(_kc[ch]);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
    }
    
    // YM2151 Key Code 转 MIDI 音符
    private static int KcToNote(int kc)
    {
        int octave = (kc >> 4) & 0x07;
        int note = kc & 0x0F;
        // KC 音符映射：0,1,2,4,5,6,8,9,10,12,13,14
        int[] noteMap = { 0, 1, 2, 2, 3, 4, 5, 5, 6, 7, 8, 8, 9, 10, 11, 11 };
        return (octave + 1) * 12 + noteMap[note];
    }
}

// YM2413 (OPLL) 状态追踪器
public class YM2413Tracker : VgmChipTracker
{
    private readonly int[] _fnum = new int[9];
    private readonly int[] _block = new int[9];
    private readonly int[] _volume = new int[9];
    private readonly bool[] _keyOn = new bool[9];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        // F-Number 低位 (0x10-0x18)
        if (reg >= 0x10 && reg <= 0x18)
        {
            int ch = reg - 0x10;
            _fnum[ch] = (_fnum[ch] & 0x100) | val;
        }
        // F-Number 高位 + Block + KeyOn (0x20-0x28)
        else if (reg >= 0x20 && reg <= 0x28)
        {
            int ch = reg - 0x20;
            _fnum[ch] = (_fnum[ch] & 0xFF) | ((val & 0x01) << 8);
            _block[ch] = (val >> 1) & 0x07;
            _keyOn[ch] = (val & 0x10) != 0;
        }
        // 音量 (0x30-0x38)
        else if (reg >= 0x30 && reg <= 0x38)
        {
            int ch = reg - 0x30;
            _volume[ch] = val & 0x0F;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_fnum);
        Array.Clear(_block);
        Array.Clear(_volume);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 9 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = (15 - _volume[ch]) * 127 / 15;
            
            if (_keyOn[ch] && _fnum[ch] > 0)
            {
                // OPLL: freq = fnum * clock / (72 * 2^(19-block))
                double freq = _fnum[ch] * 3579545.0 / (72.0 * Math.Pow(2, 19 - _block[ch]));
                state.Channels[ch].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
    }
}

// YM2203 (OPN) 状态追踪器 - 3 FM + 3 SSG
public class YM2203Tracker : VgmChipTracker
{
    private readonly int[] _fmFnum = new int[3];
    private readonly int[] _fmBlock = new int[3];
    private readonly int[] _fmTl = new int[3];
    private readonly bool[] _fmKeyOn = new bool[3];
    private readonly int[] _ssgPeriod = new int[3];
    private readonly int[] _ssgVolume = new int[3];
    private readonly bool[] _ssgEnable = new bool[3];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        // SSG 部分 (0x00-0x0D)
        if (reg <= 0x05)
        {
            int ch = reg / 2;
            if ((reg & 1) == 0)
                _ssgPeriod[ch] = (_ssgPeriod[ch] & 0xF00) | val;
            else
                _ssgPeriod[ch] = (_ssgPeriod[ch] & 0x0FF) | ((val & 0x0F) << 8);
        }
        else if (reg == 0x07)
        {
            _ssgEnable[0] = (val & 0x01) == 0;
            _ssgEnable[1] = (val & 0x02) == 0;
            _ssgEnable[2] = (val & 0x04) == 0;
        }
        else if (reg >= 0x08 && reg <= 0x0A)
        {
            _ssgVolume[reg - 0x08] = val & 0x0F;
        }
        // FM 部分
        else if (reg == 0x28)
        {
            int ch = val & 0x03;
            if (ch < 3) _fmKeyOn[ch] = (val & 0xF0) != 0;
        }
        else if (reg >= 0xA0 && reg <= 0xA2)
        {
            int ch = reg - 0xA0;
            _fmFnum[ch] = (_fmFnum[ch] & 0x700) | val;
        }
        else if (reg >= 0xA4 && reg <= 0xA6)
        {
            int ch = reg - 0xA4;
            _fmFnum[ch] = (_fmFnum[ch] & 0xFF) | ((val & 0x07) << 8);
            _fmBlock[ch] = (val >> 3) & 0x07;
        }
        else if (reg >= 0x40 && reg <= 0x4F)
        {
            int ch = (reg - 0x40) % 4;
            if ((reg - 0x40) / 4 == 0 && ch < 3)
                _fmTl[ch] = val & 0x7F;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_fmFnum);
        Array.Clear(_fmBlock);
        Array.Clear(_fmTl);
        Array.Clear(_fmKeyOn);
        Array.Clear(_ssgPeriod);
        Array.Clear(_ssgVolume);
        Array.Clear(_ssgEnable);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        // FM 通道 (0-2)
        for (int ch = 0; ch < 3 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _fmKeyOn[ch];
            state.Channels[ch].Volume = Math.Max(0, 127 - _fmTl[ch]);
            
            if (_fmKeyOn[ch] && _fmFnum[ch] > 0)
            {
                double freq = _fmFnum[ch] * 3993600.0 / (72.0 * Math.Pow(2, 21 - _fmBlock[ch]));
                state.Channels[ch].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
        
        // SSG 通道 (3-5)
        for (int ch = 0; ch < 3 && ch + 3 < state.Channels.Length; ch++)
        {
            bool active = _ssgEnable[ch] && _ssgVolume[ch] > 0 && _ssgPeriod[ch] > 0;
            state.Channels[ch + 3].KeyOn = active;
            state.Channels[ch + 3].Volume = _ssgVolume[ch] * 127 / 15;
            
            if (active)
            {
                double freq = 3993600.0 / (32.0 * _ssgPeriod[ch]);
                state.Channels[ch + 3].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[ch + 3].Note = -1;
            }
        }
    }
}

// NES APU 状态追踪器
public class NesApuTracker : VgmChipTracker
{
    private readonly int[] _period = new int[4];      // Pulse1, Pulse2, Triangle, Noise
    private readonly int[] _volume = new int[4];
    private readonly bool[] _enable = new bool[5];
    private int _dmcFreq;
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        // Pulse 1 (0x00-0x03)
        if (reg == 0x00) _volume[0] = val & 0x0F;
        else if (reg == 0x02) _period[0] = (_period[0] & 0x700) | val;
        else if (reg == 0x03) _period[0] = (_period[0] & 0x0FF) | ((val & 0x07) << 8);
        
        // Pulse 2 (0x04-0x07)
        else if (reg == 0x04) _volume[1] = val & 0x0F;
        else if (reg == 0x06) _period[1] = (_period[1] & 0x700) | val;
        else if (reg == 0x07) _period[1] = (_period[1] & 0x0FF) | ((val & 0x07) << 8);
        
        // Triangle (0x08-0x0B)
        else if (reg == 0x0A) _period[2] = (_period[2] & 0x700) | val;
        else if (reg == 0x0B) _period[2] = (_period[2] & 0x0FF) | ((val & 0x07) << 8);
        
        // Noise (0x0C-0x0F)
        else if (reg == 0x0C) _volume[3] = val & 0x0F;
        else if (reg == 0x0E) _period[3] = val & 0x0F;
        
        // Status (0x15)
        else if (reg == 0x15)
        {
            _enable[0] = (val & 0x01) != 0;
            _enable[1] = (val & 0x02) != 0;
            _enable[2] = (val & 0x04) != 0;
            _enable[3] = (val & 0x08) != 0;
            _enable[4] = (val & 0x10) != 0;
        }
        
        // DMC (0x10)
        else if (reg == 0x10) _dmcFreq = val & 0x0F;
    }
    
    public override void Reset()
    {
        Array.Clear(_period);
        Array.Clear(_volume);
        Array.Clear(_enable);
        _dmcFreq = 0;
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        // Pulse 1
        if (state.Channels.Length > 0)
        {
            bool active = _enable[0] && _volume[0] > 0 && _period[0] > 0;
            state.Channels[0].KeyOn = active;
            state.Channels[0].Volume = _volume[0] * 127 / 15;
            state.Channels[0].Note = active ? PeriodToNote(_period[0], 1789773) : -1;
        }
        
        // Pulse 2
        if (state.Channels.Length > 1)
        {
            bool active = _enable[1] && _volume[1] > 0 && _period[1] > 0;
            state.Channels[1].KeyOn = active;
            state.Channels[1].Volume = _volume[1] * 127 / 15;
            state.Channels[1].Note = active ? PeriodToNote(_period[1], 1789773) : -1;
        }
        
        // Triangle
        if (state.Channels.Length > 2)
        {
            bool active = _enable[2] && _period[2] > 0;
            state.Channels[2].KeyOn = active;
            state.Channels[2].Volume = active ? 127 : 0;
            state.Channels[2].Note = active ? PeriodToNote(_period[2] * 2, 1789773) : -1;
        }
        
        // Noise
        if (state.Channels.Length > 3)
        {
            state.Channels[3].KeyOn = _enable[3] && _volume[3] > 0;
            state.Channels[3].Volume = _volume[3] * 127 / 15;
            state.Channels[3].Note = -1;
        }
        
        // DMC
        if (state.Channels.Length > 4)
        {
            state.Channels[4].KeyOn = _enable[4];
            state.Channels[4].Volume = _enable[4] ? 127 : 0;
            state.Channels[4].Note = -1;
        }
    }
    
    private static int PeriodToNote(int period, int clock)
    {
        if (period <= 0) return -1;
        double freq = clock / (16.0 * (period + 1));
        return VgmVisualizer.FrequencyToNote(freq);
    }
}

// Game Boy DMG 状态追踪器
public class GbDmgTracker : VgmChipTracker
{
    private readonly int[] _freq = new int[3];    // CH1, CH2, Wave
    private readonly int[] _volume = new int[4];
    private readonly bool[] _enable = new bool[4];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        // CH1 (0x10-0x14)
        if (reg == 0x12) _volume[0] = (val >> 4) & 0x0F;
        else if (reg == 0x13) _freq[0] = (_freq[0] & 0x700) | val;
        else if (reg == 0x14) { _freq[0] = (_freq[0] & 0x0FF) | ((val & 0x07) << 8); if ((val & 0x80) != 0) _enable[0] = true; }
        
        // CH2 (0x16-0x19)
        else if (reg == 0x17) _volume[1] = (val >> 4) & 0x0F;
        else if (reg == 0x18) _freq[1] = (_freq[1] & 0x700) | val;
        else if (reg == 0x19) { _freq[1] = (_freq[1] & 0x0FF) | ((val & 0x07) << 8); if ((val & 0x80) != 0) _enable[1] = true; }
        
        // CH3 Wave (0x1A-0x1E)
        else if (reg == 0x1A) _enable[2] = (val & 0x80) != 0;
        else if (reg == 0x1C) _volume[2] = (val >> 5) & 0x03;
        else if (reg == 0x1D) _freq[2] = (_freq[2] & 0x700) | val;
        else if (reg == 0x1E) _freq[2] = (_freq[2] & 0x0FF) | ((val & 0x07) << 8);
        
        // CH4 Noise (0x20-0x23)
        else if (reg == 0x21) _volume[3] = (val >> 4) & 0x0F;
        else if (reg == 0x23) if ((val & 0x80) != 0) _enable[3] = true;
        
        // Master control (0x26)
        else if (reg == 0x26)
        {
            if ((val & 0x80) == 0)
            {
                Array.Clear(_enable);
            }
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_freq);
        Array.Clear(_volume);
        Array.Clear(_enable);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        // CH1, CH2
        for (int ch = 0; ch < 2 && ch < state.Channels.Length; ch++)
        {
            bool active = _enable[ch] && _volume[ch] > 0;
            state.Channels[ch].KeyOn = active;
            state.Channels[ch].Volume = _volume[ch] * 127 / 15;
            
            if (active && _freq[ch] > 0)
            {
                double freq = 131072.0 / (2048 - _freq[ch]);
                state.Channels[ch].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
        
        // CH3 Wave
        if (state.Channels.Length > 2)
        {
            bool active = _enable[2] && _volume[2] > 0;
            state.Channels[2].KeyOn = active;
            state.Channels[2].Volume = _volume[2] * 42;
            
            if (active && _freq[2] > 0)
            {
                double freq = 65536.0 / (2048 - _freq[2]);
                state.Channels[2].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[2].Note = -1;
            }
        }
        
        // CH4 Noise
        if (state.Channels.Length > 3)
        {
            state.Channels[3].KeyOn = _enable[3] && _volume[3] > 0;
            state.Channels[3].Volume = _volume[3] * 127 / 15;
            state.Channels[3].Note = -1;
        }
    }
}

// HuC6280 (PC Engine) 状态追踪器
public class HuC6280Tracker : VgmChipTracker
{
    private readonly int[] _freq = new int[6];
    private readonly int[] _volume = new int[6];
    private readonly bool[] _enable = new bool[6];
    private int _currentChannel;
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        if (reg == 0x00) _currentChannel = val & 0x07;
        else if (reg == 0x02) _freq[_currentChannel] = (_freq[_currentChannel] & 0xF00) | val;
        else if (reg == 0x03) _freq[_currentChannel] = (_freq[_currentChannel] & 0x0FF) | ((val & 0x0F) << 8);
        else if (reg == 0x04)
        {
            _enable[_currentChannel] = (val & 0x80) != 0;
            _volume[_currentChannel] = val & 0x1F;
        }
        else if (reg == 0x05) _volume[_currentChannel] = val & 0x1F;
    }
    
    public override void Reset()
    {
        Array.Clear(_freq);
        Array.Clear(_volume);
        Array.Clear(_enable);
        _currentChannel = 0;
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 6 && ch < state.Channels.Length; ch++)
        {
            bool active = _enable[ch] && _volume[ch] > 0 && _freq[ch] > 0;
            state.Channels[ch].KeyOn = active;
            state.Channels[ch].Volume = _volume[ch] * 127 / 31;
            
            if (active)
            {
                double freq = 3579545.0 / (32.0 * _freq[ch]);
                state.Channels[ch].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
    }
}

// OPL 系列 (YM3812/YM3526/Y8950) 状态追踪器
public class OplTracker : VgmChipTracker
{
    private readonly int[] _fnum = new int[9];
    private readonly int[] _block = new int[9];
    private readonly int[] _tl = new int[9];
    private readonly bool[] _keyOn = new bool[9];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        // F-Number 低位 (0xA0-0xA8)
        if (reg >= 0xA0 && reg <= 0xA8)
        {
            int ch = reg - 0xA0;
            _fnum[ch] = (_fnum[ch] & 0x300) | val;
        }
        // KeyOn + Block + F-Number 高位 (0xB0-0xB8)
        else if (reg >= 0xB0 && reg <= 0xB8)
        {
            int ch = reg - 0xB0;
            _fnum[ch] = (_fnum[ch] & 0x0FF) | ((val & 0x03) << 8);
            _block[ch] = (val >> 2) & 0x07;
            _keyOn[ch] = (val & 0x20) != 0;
        }
        // TL (总电平，每个通道2个算子)
        else if (reg >= 0x40 && reg <= 0x55)
        {
            int idx = reg - 0x40;
            int ch = idx % 3 + (idx / 6) * 3;
            if (ch < 9 && (idx % 6) < 3)
                _tl[ch] = val & 0x3F;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_fnum);
        Array.Clear(_block);
        Array.Clear(_tl);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 9 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = Math.Max(0, 127 - _tl[ch] * 2);
            
            if (_keyOn[ch] && _fnum[ch] > 0)
            {
                // OPL: freq = fnum * clock / (72 * 2^(20-block))
                double freq = _fnum[ch] * 3579545.0 / (72.0 * Math.Pow(2, 20 - _block[ch]));
                state.Channels[ch].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
    }
}

// QSound 状态追踪器
public class QSoundTracker : VgmChipTracker
{
    private readonly int[] _pitch = new int[16];
    private readonly int[] _volume = new int[16];
    private readonly bool[] _keyOn = new bool[16];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        // QSound 使用特殊的寄存器格式
        byte reg = evt.Register;
        byte val = evt.Value;
        int ch = reg & 0x0F;
        int type = (reg >> 4) & 0x0F;
        
        if (ch < 16)
        {
            if (type == 0) _pitch[ch] = (_pitch[ch] & 0xFF00) | val;
            else if (type == 1) _pitch[ch] = (_pitch[ch] & 0x00FF) | (val << 8);
            else if (type == 2) _volume[ch] = val;
            else if (type == 3) _keyOn[ch] = val != 0;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_pitch);
        Array.Clear(_volume);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 16 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = _volume[ch];
            
            if (_keyOn[ch] && _pitch[ch] > 0)
            {
                // QSound 使用简单的线性音高
                double freq = _pitch[ch] * 60000.0 / 65536.0;
                state.Channels[ch].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
    }
}

// K051649 (SCC) 状态追踪器
public class K051649Tracker : VgmChipTracker
{
    private readonly int[] _freq = new int[5];
    private readonly int[] _volume = new int[5];
    private readonly bool[] _enable = new bool[5];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        // 频率 (0x00-0x09: 每通道2字节)
        if (reg < 0x0A)
        {
            int ch = reg / 2;
            if ((reg & 1) == 0)
                _freq[ch] = (_freq[ch] & 0xF00) | val;
            else
                _freq[ch] = (_freq[ch] & 0x0FF) | ((val & 0x0F) << 8);
        }
        // 音量 (0x0A-0x0E)
        else if (reg >= 0x0A && reg <= 0x0E)
        {
            _volume[reg - 0x0A] = val & 0x0F;
        }
        // 使能 (0x0F)
        else if (reg == 0x0F)
        {
            for (int i = 0; i < 5; i++)
                _enable[i] = (val & (1 << i)) != 0;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_freq);
        Array.Clear(_volume);
        Array.Clear(_enable);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 5 && ch < state.Channels.Length; ch++)
        {
            bool active = _enable[ch] && _volume[ch] > 0 && _freq[ch] > 0;
            state.Channels[ch].KeyOn = active;
            state.Channels[ch].Volume = _volume[ch] * 127 / 15;
            
            if (active)
            {
                double freq = 3579545.0 / (32.0 * _freq[ch]);
                state.Channels[ch].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
    }
}

// POKEY 状态追踪器
public class PokeyTracker : VgmChipTracker
{
    private readonly int[] _freq = new int[4];
    private readonly int[] _volume = new int[4];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        // 频率和控制寄存器
        if (reg < 8)
        {
            int ch = reg / 2;
            if ((reg & 1) == 0)
                _freq[ch] = val;
            else
                _volume[ch] = val & 0x0F;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_freq);
        Array.Clear(_volume);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 4 && ch < state.Channels.Length; ch++)
        {
            bool active = _volume[ch] > 0 && _freq[ch] > 0;
            state.Channels[ch].KeyOn = active;
            state.Channels[ch].Volume = _volume[ch] * 127 / 15;
            
            if (active)
            {
                double freq = 1789773.0 / (2.0 * (_freq[ch] + 1));
                state.Channels[ch].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
    }
}

// SAA1099 状态追踪器
public class SAA1099Tracker : VgmChipTracker
{
    private readonly int[] _freq = new int[6];
    private readonly int[] _octave = new int[6];
    private readonly int[] _volume = new int[6];
    private readonly bool[] _enable = new bool[6];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        // 频率 (0x00-0x05)
        if (reg <= 0x05) _freq[reg] = val;
        // 八度 (0x10-0x12: 每寄存器2通道)
        else if (reg >= 0x10 && reg <= 0x12)
        {
            int ch = (reg - 0x10) * 2;
            _octave[ch] = val & 0x07;
            _octave[ch + 1] = (val >> 4) & 0x07;
        }
        // 音量 (0x00-0x05 的另一组)
        else if (reg >= 0x08 && reg <= 0x0D)
        {
            _volume[reg - 0x08] = (val & 0x0F);
        }
        // 使能 (0x14/0x15)
        else if (reg == 0x14)
        {
            for (int i = 0; i < 6; i++)
                _enable[i] = (val & (1 << i)) != 0;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_freq);
        Array.Clear(_octave);
        Array.Clear(_volume);
        Array.Clear(_enable);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 6 && ch < state.Channels.Length; ch++)
        {
            bool active = _enable[ch] && _volume[ch] > 0;
            state.Channels[ch].KeyOn = active;
            state.Channels[ch].Volume = _volume[ch] * 127 / 15;
            
            if (active && _freq[ch] > 0)
            {
                double freq = 7159090.0 / (512.0 * _freq[ch] * Math.Pow(2, 8 - _octave[ch]));
                state.Channels[ch].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
    }
}

// RF5C68/RF5C164 状态追踪器（Sega CD PCM）
public class RF5CTracker : VgmChipTracker
{
    private readonly int[] _env = new int[8];       // 音量包络
    private readonly int[] _pan = new int[8];       // 声像
    private readonly int[] _fdLow = new int[8];     // 频率增量低位
    private readonly int[] _fdHigh = new int[8];    // 频率增量高位
    private readonly bool[] _keyOn = new bool[8];
    private int _currentChannel;
    private bool _chipEnable;
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        // 控制寄存器 0x07
        if (reg == 0x07)
        {
            _chipEnable = (val & 0x80) != 0;
            // 当 MOD=1 (bit6=1) 时，低3位是通道选择
            if ((val & 0x40) != 0)
            {
                _currentChannel = val & 0x07;
            }
        }
        // 通道开/关控制 0x08（bit=1 表示通道激活）
        else if (reg == 0x08)
        {
            for (int i = 0; i < 8; i++)
                _keyOn[i] = (val & (1 << i)) != 0;
        }
        // 通道寄存器（需要先选择通道）
        else if (reg <= 0x06)
        {
            int ch = _currentChannel;
            switch (reg)
            {
                case 0x00: _env[ch] = val; break;                    // 音量包络
                case 0x01: _pan[ch] = val; break;                    // 声像
                case 0x02: _fdLow[ch] = val; break;                  // 频率增量低位
                case 0x03: _fdHigh[ch] = val; break;                 // 频率增量高位
            }
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_env);
        Array.Clear(_pan);
        Array.Clear(_fdLow);
        Array.Clear(_fdHigh);
        Array.Clear(_keyOn);
        _currentChannel = 0;
        _chipEnable = false;
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 8 && ch < state.Channels.Length; ch++)
        {
            // 只有芯片使能且通道激活时才显示
            bool active = _chipEnable && _keyOn[ch] && _env[ch] > 0;
            state.Channels[ch].KeyOn = active;
            state.Channels[ch].Volume = _env[ch] / 2;
            
            // 从频率增量计算音高
            if (active)
            {
                int fd = _fdLow[ch] | (_fdHigh[ch] << 8);
                if (fd > 0)
                {
                    // RF5C164: freq = fd * clock / (256 * 384)
                    // clock 通常是 12.5 MHz
                    double freq = fd * 12500000.0 / (256.0 * 384.0);
                    state.Channels[ch].Note = VgmVisualizer.FrequencyToNote(freq);
                }
                else
                {
                    state.Channels[ch].Note = -1;
                }
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
    }
}

// C140 状态追踪器（Namco PCM）
public class C140Tracker : VgmChipTracker
{
    private readonly int[] _volume = new int[24];
    private readonly bool[] _keyOn = new bool[24];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        int reg = (evt.Port << 8) | evt.Register;
        byte val = evt.Value;
        int ch = (reg >> 4) & 0x1F;
        int type = reg & 0x0F;
        
        if (ch < 24)
        {
            if (type == 0) _volume[ch] = val;
            else if (type == 5) _keyOn[ch] = (val & 0x80) != 0;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_volume);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 24 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = _volume[ch] / 2;
            state.Channels[ch].Note = -1;
        }
    }
}

// C352 状态追踪器（Namco 32-voice PCM）
public class C352Tracker : VgmChipTracker
{
    private readonly int[] _volume = new int[32];
    private readonly int[] _pitch = new int[32];
    private readonly bool[] _keyOn = new bool[32];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        int reg = (evt.Register << 8) | evt.Port;
        byte val = evt.Value;
        int ch = (reg >> 4) & 0x1F;
        int type = reg & 0x0F;
        
        if (ch < 32)
        {
            if (type == 0) _volume[ch] = val;
            else if (type == 2) _pitch[ch] = (_pitch[ch] & 0xFF00) | val;
            else if (type == 3) _pitch[ch] = (_pitch[ch] & 0x00FF) | (val << 8);
            else if (type == 6) _keyOn[ch] = (val & 0x40) != 0;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_volume);
        Array.Clear(_pitch);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 32 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = _volume[ch] / 2;
            state.Channels[ch].Note = -1;
        }
    }
}

// K053260 状态追踪器（Konami PCM）
public class K053260Tracker : VgmChipTracker
{
    private readonly int[] _volume = new int[4];
    private readonly bool[] _keyOn = new bool[4];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        if (reg >= 0x00 && reg <= 0x07)
        {
            int ch = reg / 2;
            if ((reg & 1) == 1) _volume[ch] = val & 0x7F;
        }
        else if (reg == 0x28)
        {
            for (int i = 0; i < 4; i++)
                _keyOn[i] = (val & (1 << i)) != 0;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_volume);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 4 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = _volume[ch];
            state.Channels[ch].Note = -1;
        }
    }
}

// K054539 状态追踪器（Konami PCM）
public class K054539Tracker : VgmChipTracker
{
    private readonly int[] _volume = new int[8];
    private readonly bool[] _keyOn = new bool[8];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        int reg = (evt.Port << 8) | evt.Register;
        byte val = evt.Value;
        
        int ch = (reg >> 5) & 0x07;
        int type = reg & 0x1F;
        
        if (type == 0x03) _volume[ch] = val;
        else if (reg == 0x214)
        {
            for (int i = 0; i < 8; i++)
                _keyOn[i] = (val & (1 << i)) != 0;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_volume);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 8 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = _volume[ch] / 2;
            state.Channels[ch].Note = -1;
        }
    }
}

// MultiPCM 状态追踪器（Sega/Yamaha）
public class MultiPCMTracker : VgmChipTracker
{
    private readonly int[] _volume = new int[28];
    private readonly bool[] _keyOn = new bool[28];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        int ch = reg >> 3;
        int type = reg & 0x07;
        
        if (ch < 28)
        {
            if (type == 0) _keyOn[ch] = (val & 0x80) != 0;
            else if (type == 4) _volume[ch] = val & 0x7F;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_volume);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 28 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = _volume[ch];
            state.Channels[ch].Note = -1;
        }
    }
}

// SCSP 状态追踪器（Sega Saturn）
public class ScspTracker : VgmChipTracker
{
    private readonly int[] _volume = new int[32];
    private readonly bool[] _keyOn = new bool[32];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        int addr = (evt.Register << 8) | evt.Port;
        byte val = evt.Value;
        
        int ch = (addr >> 5) & 0x1F;
        int reg = addr & 0x1F;
        
        if (reg == 0x00) _keyOn[ch] = (val & 0x10) != 0;
        else if (reg == 0x0A) _volume[ch] = val & 0x0F;
    }
    
    public override void Reset()
    {
        Array.Clear(_volume);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 32 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = (15 - _volume[ch]) * 127 / 15;
            state.Channels[ch].Note = -1;
        }
    }
}

// WonderSwan 状态追踪器
public class WSwanTracker : VgmChipTracker
{
    private readonly int[] _freq = new int[4];
    private readonly int[] _volume = new int[4];
    private readonly bool[] _enable = new bool[4];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        // 频率低位
        if (reg >= 0x80 && reg <= 0x87)
        {
            int ch = (reg - 0x80) / 2;
            if ((reg & 1) == 0)
                _freq[ch] = (_freq[ch] & 0x700) | val;
            else
                _freq[ch] = (_freq[ch] & 0x0FF) | ((val & 0x07) << 8);
        }
        // 音量
        else if (reg >= 0x88 && reg <= 0x8B)
        {
            _volume[reg - 0x88] = val & 0x0F;
        }
        // 使能
        else if (reg == 0x90)
        {
            for (int i = 0; i < 4; i++)
                _enable[i] = (val & (1 << i)) != 0;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_freq);
        Array.Clear(_volume);
        Array.Clear(_enable);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 4 && ch < state.Channels.Length; ch++)
        {
            bool active = _enable[ch] && _volume[ch] > 0;
            state.Channels[ch].KeyOn = active;
            state.Channels[ch].Volume = _volume[ch] * 127 / 15;
            
            if (active && _freq[ch] > 0)
            {
                double freq = 3072000.0 / (32.0 * (2048 - _freq[ch]));
                state.Channels[ch].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
    }
}

// X1-010 状态追踪器（Seta/Allumer）
public class X1010Tracker : VgmChipTracker
{
    private readonly int[] _volume = new int[16];
    private readonly bool[] _keyOn = new bool[16];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        int addr = (evt.Register << 8) | evt.Port;
        byte val = evt.Value;
        
        int ch = (addr >> 3) & 0x0F;
        int reg = addr & 0x07;
        
        if (reg == 0) _keyOn[ch] = (val & 0x01) != 0;
        else if (reg == 1) _volume[ch] = val & 0x0F;
    }
    
    public override void Reset()
    {
        Array.Clear(_volume);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 16 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = _volume[ch] * 127 / 15;
            state.Channels[ch].Note = -1;
        }
    }
}

// OKIM6295 状态追踪器
public class OKIM6295Tracker : VgmChipTracker
{
    private readonly bool[] _keyOn = new bool[4];
    private readonly int[] _volume = new int[4];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        if (reg == 0x00)
        {
            // 触发命令
            if ((val & 0x80) != 0)
            {
                for (int i = 0; i < 4; i++)
                    if ((val & (1 << i)) != 0) _keyOn[i] = true;
            }
            else if ((val & 0x78) != 0)
            {
                for (int i = 0; i < 4; i++)
                    if ((val & (8 << i)) != 0) _keyOn[i] = false;
            }
        }
        else if (reg >= 0x08 && reg <= 0x0B)
        {
            _volume[reg - 0x08] = (val >> 4) & 0x0F;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_keyOn);
        Array.Clear(_volume);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 4 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = (15 - _volume[ch]) * 127 / 15;
            state.Channels[ch].Note = -1;
        }
    }
}

// SegaPCM 状态追踪器
public class SegaPCMTracker : VgmChipTracker
{
    private readonly int[] _volume = new int[16];
    private readonly bool[] _keyOn = new bool[16];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        int addr = evt.Register;
        byte val = evt.Value;
        
        int ch = (addr >> 3) & 0x0F;
        int reg = addr & 0x07;
        
        if (reg == 0x02) _volume[ch] = val;
        else if (reg == 0x06) _keyOn[ch] = (val & 0x01) == 0;
    }
    
    public override void Reset()
    {
        Array.Clear(_volume);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 16 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = _volume[ch] / 2;
            state.Channels[ch].Note = -1;
        }
    }
}

// YMZ280B 状态追踪器
public class YMZ280BTracker : VgmChipTracker
{
    private readonly int[] _volume = new int[8];
    private readonly bool[] _keyOn = new bool[8];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        int ch = (reg >> 2) & 0x07;
        int type = reg & 0x03;
        
        if (type == 0) _keyOn[ch] = (val & 0x80) != 0;
        else if (type == 2) _volume[ch] = val;
    }
    
    public override void Reset()
    {
        Array.Clear(_volume);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 8 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = _volume[ch] / 2;
            state.Channels[ch].Note = -1;
        }
    }
}

// GA20 状态追踪器（Irem）
public class GA20Tracker : VgmChipTracker
{
    private readonly int[] _volume = new int[4];
    private readonly bool[] _keyOn = new bool[4];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        int ch = reg >> 3;
        int type = reg & 0x07;
        
        if (ch < 4)
        {
            if (type == 5) _volume[ch] = val;
            else if (type == 6) _keyOn[ch] = (val & 0x01) != 0;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_volume);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 4 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = _volume[ch] / 2;
            state.Channels[ch].Note = -1;
        }
    }
}
