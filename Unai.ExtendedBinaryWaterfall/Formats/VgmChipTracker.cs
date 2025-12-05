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
    private readonly int[,] _tl = new int[6, 4];  // 每通道 4 个算子的 TL
    private readonly int[] _algo = new int[6];    // 算法
    private readonly int[] _lr = new int[6];      // Left/Right 输出选择
    private readonly bool[] _keyOn = new bool[6];
    private bool _dacEnable;
    
    // 算法对应的载波算子掩码 (S1=bit0, S2=bit1, S3=bit2, S4=bit3)
    // 算法 0-3: 只有 S4 是载波
    // 算法 4: S2 和 S4 是载波
    // 算法 5-6: S2, S3, S4 是载波
    // 算法 7: 全部是载波
    private static readonly int[] CarrierMask = { 0x08, 0x08, 0x08, 0x08, 0x0A, 0x0E, 0x0E, 0x0F };
    
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
        // 算法和反馈 (0xB0-0xB2)
        else if (reg >= 0xB0 && reg <= 0xB2)
        {
            int ch = (reg - 0xB0) + chOffset;
            if (ch < 6) _algo[ch] = val & 0x07;
        }
        // 总电平 TL (0x40-0x4F: 4个算子 x 3通道 x 2端口)
        else if (reg >= 0x40 && reg <= 0x4F)
        {
            int op = (reg - 0x40) / 4;   // 算子: 0=S1, 1=S3, 2=S2, 3=S4
            int ch = ((reg - 0x40) % 4) + chOffset;
            if (ch < 6 && ch % 4 < 3)
            {
                // 寄存器布局转换: 0->S1, 1->S3, 2->S2, 3->S4
                int slot = op == 0 ? 0 : op == 1 ? 2 : op == 2 ? 1 : 3;
                _tl[ch, slot] = val & 0x7F;
            }
        }
        // DAC 使能 (0x2B)
        else if (reg == 0x2B)
        {
            _dacEnable = (val & 0x80) != 0;
        }
        // L/R 输出选择 (0xB4-0xB6)
        else if (reg >= 0xB4 && reg <= 0xB6)
        {
            int ch = (reg - 0xB4) + chOffset;
            if (ch < 6) _lr[ch] = (val >> 6) & 0x03;  // bit7=L, bit6=R
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_fnum);
        Array.Clear(_block);
        Array.Clear(_tl);
        Array.Clear(_algo);
        Array.Clear(_lr);
        Array.Clear(_keyOn);
        _dacEnable = false;
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 6; ch++)
        {
            if (ch >= state.Channels.Length) break;
            
            state.Channels[ch].KeyOn = _keyOn[ch];
            
            // 根据算法计算载波算子的最小 TL（最大音量）
            int mask = CarrierMask[_algo[ch]];
            int minTl = 127;
            for (int op = 0; op < 4; op++)
            {
                if ((mask & (1 << op)) != 0)
                    minTl = Math.Min(minTl, _tl[ch, op]);
            }
            int vol = Math.Max(0, 127 - minTl);
            state.Channels[ch].Volume = vol;
            
            // 设置左右声道 (bit1=L, bit0=R)
            int lr = _lr[ch];
            state.Channels[ch].PanLeft = (lr & 0x02) != 0 ? vol : 0;
            state.Channels[ch].PanRight = (lr & 0x01) != 0 ? vol : 0;
            
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
            int dacVol = _dacEnable ? 127 : 0;
            state.Channels[6].KeyOn = _dacEnable;
            state.Channels[6].Note = -1;
            state.Channels[6].Volume = dacVol;
            state.Channels[6].PanLeft = dacVol;
            state.Channels[6].PanRight = dacVol;
        }
    }
    
    // YM2612 F-Number 转音符
    private static int FnumToNote(int fnum, int block)
    {
        if (fnum == 0) return -1;
        // YM2612: freq = fnum * clock / (144 * 2^(21-block))
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
            int vol = (15 - _volume[ch]) * 127 / 15;
            state.Channels[ch].KeyOn = active;
            state.Channels[ch].Volume = vol;
            // SN76489 是单声道芯片，左右相同
            state.Channels[ch].PanLeft = vol;
            state.Channels[ch].PanRight = vol;
            
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
            int noiseVol = (15 - _volume[3]) * 127 / 15;
            state.Channels[3].KeyOn = _volume[3] < 15;
            state.Channels[3].Volume = noiseVol;
            state.Channels[3].PanLeft = noiseVol;
            state.Channels[3].PanRight = noiseVol;
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
            int vol = _volume[ch] * 127 / 15;
            state.Channels[ch].KeyOn = active;
            state.Channels[ch].Volume = vol;
            // AY8910 是单声道芯片，左右相同
            state.Channels[ch].PanLeft = vol;
            state.Channels[ch].PanRight = vol;
            
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
    private readonly int[] _kc = new int[8];       // Key Code
    private readonly int[] _kf = new int[8];       // Key Fraction
    private readonly int[,] _tl = new int[8, 4];   // 每通道 4 个算子的 TL
    private readonly int[] _algo = new int[8];     // 算法
    private readonly int[] _rl = new int[8];       // RL (Right/Left) 声道选择
    private readonly bool[] _keyOn = new bool[8];
    
    // YM2151 与 YM2612 使用相同的 8 种算法
    private static readonly int[] CarrierMask = { 0x08, 0x08, 0x08, 0x08, 0x0A, 0x0E, 0x0E, 0x0F };
    
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
        // RL/FB/CON (0x20-0x27): bit7=R, bit6=L, bit5-3=FB, bit2-0=CON
        else if (reg >= 0x20 && reg <= 0x27)
        {
            int ch = reg & 0x07;
            _algo[ch] = val & 0x07;
            _rl[ch] = val & 0xC0;  // 保存 RL 位
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
        // TL (0x60-0x7F: 4 个算子 x 8 通道)
        else if (reg >= 0x60 && reg <= 0x7F)
        {
            int op = (reg - 0x60) / 8;  // 0-3
            int ch = reg & 0x07;
            _tl[ch, op] = val & 0x7F;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_kc);
        Array.Clear(_kf);
        Array.Clear(_tl);
        Array.Clear(_algo);
        Array.Clear(_rl);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 8 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            
            // 根据算法计算载波算子的最小 TL
            int mask = CarrierMask[_algo[ch]];
            int minTl = 127;
            for (int op = 0; op < 4; op++)
            {
                if ((mask & (1 << op)) != 0)
                    minTl = Math.Min(minTl, _tl[ch, op]);
            }
            int vol = Math.Max(0, 127 - minTl);
            state.Channels[ch].Volume = vol;
            
            // 设置声像显示（根据 RL 寄存器）
            int rl = _rl[ch];
            bool left = (rl & 0x80) != 0;
            bool right = (rl & 0x40) != 0;
            if (left && right)
            {
                state.Channels[ch].PanLeft = vol;
                state.Channels[ch].PanRight = vol;
            }
            else if (left)
            {
                state.Channels[ch].PanLeft = vol;
                state.Channels[ch].PanRight = 0;
            }
            else if (right)
            {
                state.Channels[ch].PanLeft = 0;
                state.Channels[ch].PanRight = vol;
            }
            else
            {
                state.Channels[ch].PanLeft = vol / 2;
                state.Channels[ch].PanRight = vol / 2;
            }
            
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
            int vol = (15 - _volume[ch]) * 127 / 15;
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = vol;
            // YM2413 是单声道芯片
            state.Channels[ch].PanLeft = vol;
            state.Channels[ch].PanRight = vol;
            
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
    private readonly int[,] _fmTl = new int[3, 4];  // 每通道 4 个算子的 TL
    private readonly int[] _fmAlgo = new int[3];     // 算法
    private readonly bool[] _fmKeyOn = new bool[3];
    private readonly int[] _ssgPeriod = new int[3];
    private readonly int[] _ssgVolume = new int[3];
    private readonly bool[] _ssgEnable = new bool[3];
    
    // OPN 系列与 YM2612 使用相同的 8 种算法
    private static readonly int[] CarrierMask = { 0x08, 0x08, 0x08, 0x08, 0x0A, 0x0E, 0x0E, 0x0F };
    
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
        // 算法和反馈 (0xB0-0xB2)
        else if (reg >= 0xB0 && reg <= 0xB2)
        {
            int ch = reg - 0xB0;
            _fmAlgo[ch] = val & 0x07;
        }
        // TL (0x40-0x4F: 4 个算子 x 3 通道)
        else if (reg >= 0x40 && reg <= 0x4F)
        {
            int op = (reg - 0x40) / 4;   // 算子: 0=S1, 1=S3, 2=S2, 3=S4
            int ch = (reg - 0x40) % 4;
            if (ch < 3)
            {
                int slot = op == 0 ? 0 : op == 1 ? 2 : op == 2 ? 1 : 3;
                _fmTl[ch, slot] = val & 0x7F;
            }
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_fmFnum);
        Array.Clear(_fmBlock);
        Array.Clear(_fmTl);
        Array.Clear(_fmAlgo);
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
            
            // 根据算法计算载波算子的最小 TL
            int mask = CarrierMask[_fmAlgo[ch]];
            int minTl = 127;
            for (int op = 0; op < 4; op++)
            {
                if ((mask & (1 << op)) != 0)
                    minTl = Math.Min(minTl, _fmTl[ch, op]);
            }
            int vol = Math.Max(0, 127 - minTl);
            state.Channels[ch].Volume = vol;
            // YM2203 FM 是单声道
            state.Channels[ch].PanLeft = vol;
            state.Channels[ch].PanRight = vol;
            
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
            int vol = _ssgVolume[ch] * 127 / 15;
            state.Channels[ch + 3].KeyOn = active;
            state.Channels[ch + 3].Volume = vol;
            state.Channels[ch + 3].PanLeft = vol;
            state.Channels[ch + 3].PanRight = vol;
            
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
    private readonly int[] _volume = new int[3];      // Pulse1, Pulse2, Noise (Triangle无音量控制)
    private readonly bool[] _enable = new bool[5];    // Pulse1, Pulse2, Triangle, Noise, DMC
    private readonly bool[] _lengthHalt = new bool[4];// 长度计数器停止标志
    private int _linearCounter;                       // Triangle 线性计数器
    private int _dmcFreq;
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        // Pulse 1 (0x00-0x03)
        if (reg == 0x00)
        {
            _lengthHalt[0] = (val & 0x20) != 0;
            _volume[0] = val & 0x0F;
        }
        else if (reg == 0x02) _period[0] = (_period[0] & 0x700) | val;
        else if (reg == 0x03) _period[0] = (_period[0] & 0x0FF) | ((val & 0x07) << 8);
        
        // Pulse 2 (0x04-0x07)
        else if (reg == 0x04)
        {
            _lengthHalt[1] = (val & 0x20) != 0;
            _volume[1] = val & 0x0F;
        }
        else if (reg == 0x06) _period[1] = (_period[1] & 0x700) | val;
        else if (reg == 0x07) _period[1] = (_period[1] & 0x0FF) | ((val & 0x07) << 8);
        
        // Triangle (0x08-0x0B) - 无音量控制
        else if (reg == 0x08)
        {
            _lengthHalt[2] = (val & 0x80) != 0;
            _linearCounter = val & 0x7F;
        }
        else if (reg == 0x0A) _period[2] = (_period[2] & 0x700) | val;
        else if (reg == 0x0B) _period[2] = (_period[2] & 0x0FF) | ((val & 0x07) << 8);
        
        // Noise (0x0C-0x0F)
        else if (reg == 0x0C)
        {
            _lengthHalt[3] = (val & 0x20) != 0;
            _volume[2] = val & 0x0F;
        }
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
        Array.Clear(_lengthHalt);
        _linearCounter = 0;
        _dmcFreq = 0;
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        // Pulse 1
        if (state.Channels.Length > 0)
        {
            bool active = _enable[0] && _volume[0] > 0 && _period[0] >= 8;
            int vol = _volume[0] * 127 / 15;
            state.Channels[0].KeyOn = active;
            state.Channels[0].Volume = vol;
            state.Channels[0].PanLeft = vol;
            state.Channels[0].PanRight = vol;
            state.Channels[0].Note = active ? PeriodToNote(_period[0]) : -1;
        }
        
        // Pulse 2
        if (state.Channels.Length > 1)
        {
            bool active = _enable[1] && _volume[1] > 0 && _period[1] >= 8;
            int vol = _volume[1] * 127 / 15;
            state.Channels[1].KeyOn = active;
            state.Channels[1].Volume = vol;
            state.Channels[1].PanLeft = vol;
            state.Channels[1].PanRight = vol;
            state.Channels[1].Note = active ? PeriodToNote(_period[1]) : -1;
        }
        
        // Triangle (无音量控制，始终最大音量)
        if (state.Channels.Length > 2)
        {
            bool active = _enable[2] && _linearCounter > 0 && _period[2] >= 2;
            int vol = active ? 127 : 0;
            state.Channels[2].KeyOn = active;
            state.Channels[2].Volume = vol;
            state.Channels[2].PanLeft = vol;
            state.Channels[2].PanRight = vol;
            state.Channels[2].Note = active ? TrianglePeriodToNote(_period[2]) : -1;
        }
        
        // Noise
        if (state.Channels.Length > 3)
        {
            int vol = _volume[2] * 127 / 15;
            state.Channels[3].KeyOn = _enable[3] && _volume[2] > 0;
            state.Channels[3].Volume = vol;
            state.Channels[3].PanLeft = vol;
            state.Channels[3].PanRight = vol;
            state.Channels[3].Note = -1;
        }
        
        // DMC
        if (state.Channels.Length > 4)
        {
            int vol = _enable[4] ? 127 : 0;
            state.Channels[4].KeyOn = _enable[4];
            state.Channels[4].Volume = vol;
            state.Channels[4].PanLeft = vol;
            state.Channels[4].PanRight = vol;
            state.Channels[4].Note = -1;
        }
    }
    
    // Pulse 频率: f = CPU / (16 * (t + 1))
    private static int PeriodToNote(int period)
    {
        if (period < 8) return -1;
        double freq = 1789773.0 / (16.0 * (period + 1));
        return VgmVisualizer.FrequencyToNote(freq);
    }
    
    // Triangle 频率: f = CPU / (32 * (t + 1)) - 比 Pulse 低一个八度
    private static int TrianglePeriodToNote(int period)
    {
        if (period < 2) return -1;
        double freq = 1789773.0 / (32.0 * (period + 1));
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
            int vol = _volume[ch] * 127 / 15;
            state.Channels[ch].KeyOn = active;
            state.Channels[ch].Volume = vol;
            state.Channels[ch].PanLeft = vol;
            state.Channels[ch].PanRight = vol;
            
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
            int vol = _volume[2] * 42;
            state.Channels[2].KeyOn = active;
            state.Channels[2].Volume = vol;
            state.Channels[2].PanLeft = vol;
            state.Channels[2].PanRight = vol;
            
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
            int vol = _volume[3] * 127 / 15;
            state.Channels[3].KeyOn = _enable[3] && _volume[3] > 0;
            state.Channels[3].Volume = vol;
            state.Channels[3].PanLeft = vol;
            state.Channels[3].PanRight = vol;
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
            int vol = _volume[ch] * 127 / 31;
            state.Channels[ch].KeyOn = active;
            state.Channels[ch].Volume = vol;
            state.Channels[ch].PanLeft = vol;
            state.Channels[ch].PanRight = vol;
            
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
            int vol = Math.Max(0, 127 - _tl[ch] * 2);
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = vol;
            // OPL 是单声道芯片
            state.Channels[ch].PanLeft = vol;
            state.Channels[ch].PanRight = vol;
            
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
    private readonly int[] _pan = new int[16];
    private readonly bool[] _keyOn = new bool[16];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        // VGM格式: 0xC4 mmll rr, Register=rr, Port=mm, Value=ll
        byte reg = evt.Register;
        int data = (evt.Port << 8) | evt.Value;  // 16位数据 = mm:ll
        
        // QSound寄存器: 每通道8个寄存器, 16通道
        int ch = reg >> 3;
        int offset = reg & 0x07;
        
        if (ch < 16)
        {
            switch (offset)
            {
                case 0: break;  // bank
                case 1: break;  // start
                case 2:  // pitch
                    _pitch[ch] = data;
                    break;
                case 3: break;  // loop
                case 4:  // end (触发keyOn)
                    _keyOn[ch] = data != 0;
                    break;
                case 5:  // volume
                    _volume[ch] = data >> 4;
                    break;
                case 6:  // pan
                    _pan[ch] = data;
                    break;
            }
        }
        
        // 0x80-0x8F: 音量/声像寄存器
        if (reg >= 0x80 && reg <= 0x8F)
        {
            ch = reg & 0x0F;
            _volume[ch] = data >> 6;
        }
        // 0x90-0x9F: 控制寄存器
        else if (reg >= 0x90 && reg <= 0x9F)
        {
            ch = reg & 0x0F;
            _keyOn[ch] = (data & 0x8000) != 0;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_pitch);
        Array.Clear(_volume);
        Array.Clear(_pan);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 16 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = Math.Min(127, _volume[ch]);
            
            // Pan
            int pan = _pan[ch];
            state.Channels[ch].PanLeft = Math.Min(127, (pan >> 8) & 0xFF);
            state.Channels[ch].PanRight = Math.Min(127, pan & 0xFF);
            
            // QSound: pitch=0x1000 为原始音高
            if (_keyOn[ch] && _pitch[ch] > 0)
            {
                double ratio = _pitch[ch] / 4096.0;
                state.Channels[ch].Note = VgmVisualizer.PcmRatioToNote(ratio, 60);
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
            int vol = _volume[ch] * 127 / 15;
            state.Channels[ch].KeyOn = active;
            state.Channels[ch].Volume = vol;
            state.Channels[ch].PanLeft = vol;
            state.Channels[ch].PanRight = vol;
            
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
            int vol = _volume[ch] * 127 / 15;
            state.Channels[ch].KeyOn = active;
            state.Channels[ch].Volume = vol;
            state.Channels[ch].PanLeft = vol;
            state.Channels[ch].PanRight = vol;
            
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
            int vol = _volume[ch] * 127 / 15;
            state.Channels[ch].KeyOn = active;
            state.Channels[ch].Volume = vol;
            // SAA1099 有立体声，但简化为单声道
            state.Channels[ch].PanLeft = vol;
            state.Channels[ch].PanRight = vol;
            
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
    private readonly int[] _pan = new int[8];       // 声像 (L4:R4)
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
            // 当 CB=1 (bit6=1) 时，低3位是通道选择
            if ((val & 0x40) != 0)
            {
                _currentChannel = val & 0x07;
            }
        }
        // 通道开/关控制 0x08（bit=0 表示通道激活，bit=1 表示静音）
        else if (reg == 0x08)
        {
            for (int i = 0; i < 8; i++)
                _keyOn[i] = (val & (1 << i)) == 0;  // 0=播放, 1=静音
        }
        // 通道寄存器（需要先选择通道）
        else if (reg <= 0x06)
        {
            int ch = _currentChannel;
            switch (reg)
            {
                case 0x00: _env[ch] = val; break;        // 音量包络
                case 0x01: _pan[ch] = val; break;        // 声像 L[7:4] R[3:0]
                case 0x02: _fdLow[ch] = val; break;      // FD低8位
                case 0x03: _fdHigh[ch] = val & 0x07; break;  // FD高3位
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
            
            // 声像: 高4位=L, 低4位=R
            int panL = (_pan[ch] >> 4) & 0x0F;
            int panR = _pan[ch] & 0x0F;
            state.Channels[ch].PanLeft = panL * 8;   // 0-15 -> 0-120
            state.Channels[ch].PanRight = panR * 8;
            
            // 从频率增量计算音高: FD=0x800 为原始音高
            if (active)
            {
                int fd = _fdLow[ch] | (_fdHigh[ch] << 8);
                if (fd > 0)
                {
                    double ratio = fd / 2048.0;
                    state.Channels[ch].Note = VgmVisualizer.PcmRatioToNote(ratio, 60);
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
    private readonly int[] _volumeR = new int[24];
    private readonly int[] _volumeL = new int[24];
    private readonly int[] _freqH = new int[24];
    private readonly int[] _freqL = new int[24];
    private readonly bool[] _keyOn = new bool[24];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        int reg = (evt.Port << 8) | evt.Register;
        byte val = evt.Value;
        int ch = (reg >> 4) & 0x1F;
        int type = reg & 0x0F;
        
        if (ch < 24)
        {
            switch (type)
            {
                case 0x00: _volumeR[ch] = val; break;   // 右声道音量
                case 0x01: _volumeL[ch] = val; break;   // 左声道音量
                case 0x02: _freqH[ch] = val; break;     // 频率高位
                case 0x03: _freqL[ch] = val; break;     // 频率低位
                case 0x05: _keyOn[ch] = (val & 0x80) != 0; break;  // Key On
            }
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_volumeR);
        Array.Clear(_volumeL);
        Array.Clear(_freqH);
        Array.Clear(_freqL);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 24 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = Math.Max(_volumeL[ch], _volumeR[ch]) / 2;
            state.Channels[ch].PanLeft = _volumeL[ch];
            state.Channels[ch].PanRight = _volumeR[ch];
            
            // C140: freq16=0x1000 为原始音高
            if (_keyOn[ch])
            {
                int freq16 = (_freqH[ch] << 8) | _freqL[ch];
                if (freq16 > 0)
                {
                    double ratio = freq16 / 4096.0;
                    state.Channels[ch].Note = VgmVisualizer.PcmRatioToNote(ratio, 60);
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

// C352 状态追踪器（Namco 32-voice PCM）
// 参考: https://github.com/mamedev/mame/blob/master/src/devices/sound/c352.cpp
public class C352Tracker : VgmChipTracker
{
    private const int C352_FLG_BUSY = 0x8000;
    private const int C352_FLG_KEYON = 0x4000;
    private const int C352_FLG_KEYOFF = 0x2000;
    
    private readonly int[] _volFront = new int[32];  // 高8位=L, 低8位=R
    private readonly int[] _volRear = new int[32];
    private readonly int[] _freq = new int[32];
    private readonly int[] _flags = new int[32];
    private readonly bool[] _busy = new bool[32];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        // VGM: 0xE1 mm ll dd ee -> Register=mm, Port=ll, Value=dd, Value2=ee
        // 地址 = (mm << 8) | ll, 数据 = (dd << 8) | ee
        int addr = (evt.Register << 8) | evt.Port;
        int data = (evt.Value << 8) | evt.Value2;
        
        // C352: 每通道8个寄存器（每个16位）
        // Channel = addr / 8, Register = addr % 8
        if (addr < 0x100)
        {
            int ch = addr >> 3;
            int reg = addr & 0x07;
            
            if (ch < 32)
            {
                switch (reg)
                {
                    case 0: _volFront[ch] = data; break;  // vol_f: 高字节=L, 低字节=R
                    case 1: _volRear[ch] = data; break;   // vol_r: 高字节=L, 低字节=R
                    case 2: _freq[ch] = data; break;      // frequency
                    case 3:  // flags
                        _flags[ch] = data;
                        // KEYON 标志设置时，在 0x202 写入后会变成 BUSY
                        if ((data & C352_FLG_KEYON) != 0)
                            _busy[ch] = true;
                        if ((data & C352_FLG_KEYOFF) != 0)
                            _busy[ch] = false;
                        break;
                }
            }
        }
        else if (addr == 0x202)
        {
            // Key on/off 执行命令：将所有 KEYON 标志转换为 BUSY
            for (int i = 0; i < 32; i++)
            {
                if ((_flags[i] & C352_FLG_KEYON) != 0)
                {
                    _busy[i] = true;
                    _flags[i] = (_flags[i] & ~C352_FLG_KEYON) | C352_FLG_BUSY;
                }
                if ((_flags[i] & C352_FLG_KEYOFF) != 0)
                {
                    _busy[i] = false;
                    _flags[i] &= ~(C352_FLG_KEYOFF | C352_FLG_BUSY);
                }
            }
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_volFront);
        Array.Clear(_volRear);
        Array.Clear(_freq);
        Array.Clear(_flags);
        Array.Clear(_busy);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 32 && ch < state.Channels.Length; ch++)
        {
            // 前声道: vol_f 高字节=L, 低字节=R
            int frontL = (_volFront[ch] >> 8) & 0xFF;
            int frontR = _volFront[ch] & 0xFF;
            // 后声道: vol_r 高字节=L, 低字节=R
            int rearL = (_volRear[ch] >> 8) & 0xFF;
            int rearR = _volRear[ch] & 0xFF;
            
            state.Channels[ch].KeyOn = _busy[ch];
            state.Channels[ch].Volume = Math.Max(Math.Max(frontL, frontR), Math.Max(rearL, rearR));
            state.Channels[ch].PanLeft = frontL;
            state.Channels[ch].PanRight = frontR;
            state.Channels[ch].RearLeft = rearL;
            state.Channels[ch].RearRight = rearR;
            state.Channels[ch].HasQuadChannel = true;  // C352 是四声道芯片
            
            // Pitch -> Note: C352 freq=0x10000 为原始音高
            if (_busy[ch] && _freq[ch] > 0)
            {
                double ratio = _freq[ch] / 65536.0;
                state.Channels[ch].Note = VgmVisualizer.PcmRatioToNote(ratio, 60);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
    }
}

// K053260 状态追踪器（Konami PCM）
public class K053260Tracker : VgmChipTracker
{
    private readonly int[] _volume = new int[4];
    private readonly int[] _pitch = new int[4];
    private readonly int[] _pan = new int[4];
    private readonly bool[] _keyOn = new bool[4];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        // 每通道8个寄存器 (0x08-0x27)
        if (reg >= 0x08 && reg <= 0x27)
        {
            int ch = (reg - 0x08) / 8;
            int type = (reg - 0x08) % 8;
            if (ch < 4)
            {
                switch (type)
                {
                    case 0: _pitch[ch] = (_pitch[ch] & 0x0F00) | val; break;  // pitch低8位
                    case 1: _pitch[ch] = (_pitch[ch] & 0x00FF) | ((val & 0x0F) << 8); break;  // pitch高4位
                    case 7: _volume[ch] = val & 0x7F; break;  // 音量
                }
            }
        }
        else if (reg == 0x28)  // Key On/Off
        {
            for (int i = 0; i < 4; i++)
                _keyOn[i] = (val & (1 << i)) != 0;
        }
        else if (reg == 0x2C)  // Pan声道0,1
        {
            _pan[0] = val & 0x07;
            _pan[1] = (val >> 3) & 0x07;
        }
        else if (reg == 0x2D)  // Pan声道2,3
        {
            _pan[2] = val & 0x07;
            _pan[3] = (val >> 3) & 0x07;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_volume);
        Array.Clear(_pitch);
        Array.Clear(_pan);
        Array.Clear(_keyOn);
    }
    
    // K053260 Pan 查找表 (0=静音, 1-7=声像位置)
    // 参考 MAME: pan_mul[8][2] = {{0,0}, {65536,0}, {59870,26656}, {53684,37950}, {46341,46341}, {37950,53684}, {26656,59870}, {0,65536}}
    private static readonly int[,] K053260_PanTable = {
        { 0, 0 },       // 0: 静音
        { 127, 0 },     // 1: 全左
        { 117, 52 },    // 2: 左偏
        { 104, 74 },    // 3: 中左
        { 90, 90 },     // 4: 中心
        { 74, 104 },    // 5: 中右
        { 52, 117 },    // 6: 右偏
        { 0, 127 }      // 7: 全右
    };
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 4 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = _volume[ch];
            
            // 使用查找表设置声像
            int pan = _pan[ch] & 0x07;
            state.Channels[ch].PanLeft = K053260_PanTable[pan, 0] * _volume[ch] / 127;
            state.Channels[ch].PanRight = K053260_PanTable[pan, 1] * _volume[ch] / 127;
            
            // Pitch -> Note: K053260 pitch=0x800 为原始音高
            if (_keyOn[ch] && _pitch[ch] > 0)
            {
                double ratio = _pitch[ch] / 2048.0;
                state.Channels[ch].Note = VgmVisualizer.PcmRatioToNote(ratio, 60);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
    }
}

// K054539 状态追踪器（Konami PCM）
public class K054539Tracker : VgmChipTracker
{
    private readonly int[] _volume = new int[8];
    private readonly int[] _pitch = new int[8];  // 24位pitch
    private readonly int[] _panL = new int[8];
    private readonly int[] _panR = new int[8];
    private readonly bool[] _keyOn = new bool[8];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        int reg = (evt.Port << 8) | evt.Register;
        byte val = evt.Value;
        
        // 通道寄存器 0x00-0xFF (每通道32字节)
        if (reg < 0x100)
        {
            int ch = reg >> 5;
            int type = reg & 0x1F;
            if (ch < 8)
            {
                switch (type)
                {
                    case 0x00: _pitch[ch] = (_pitch[ch] & 0xFFFF00) | val; break;  // pitch低8位
                    case 0x01: _pitch[ch] = (_pitch[ch] & 0xFF00FF) | (val << 8); break;  // pitch中8位
                    case 0x02: _pitch[ch] = (_pitch[ch] & 0x00FFFF) | (val << 16); break;  // pitch高8位
                    case 0x03: _volume[ch] = val; break;  // 音量
                    case 0x04: _panL[ch] = val; break;  // 左声道
                    case 0x05: _panR[ch] = val; break;  // 右声道
                }
            }
        }
        else if (reg == 0x214)  // Key On/Off
        {
            for (int i = 0; i < 8; i++)
                _keyOn[i] = (val & (1 << i)) != 0;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_volume);
        Array.Clear(_pitch);
        Array.Clear(_panL);
        Array.Clear(_panR);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 8 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = _volume[ch] / 2;
            state.Channels[ch].PanLeft = _panL[ch];
            state.Channels[ch].PanRight = _panR[ch];
            
            // K054539: 24位pitch，pitch=0x10000 为原始音高
            if (_keyOn[ch] && _pitch[ch] > 0)
            {
                double ratio = _pitch[ch] / 65536.0;
                state.Channels[ch].Note = VgmVisualizer.PcmRatioToNote(ratio, 60);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
    }
}

// MultiPCM 状态追踪器（Sega/Yamaha）
public class MultiPCMTracker : VgmChipTracker
{
    private readonly int[] _volume = new int[28];
    private readonly int[] _panpot = new int[28];
    private readonly int[] _oct = new int[28];
    private readonly int[] _pitch = new int[28];
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
            else if (type == 1) _panpot[ch] = val & 0x0F;
            else if (type == 2) _pitch[ch] = (_pitch[ch] & 0xFF00) | val;  // Pitch Low
            else if (type == 3) _pitch[ch] = (_pitch[ch] & 0x00FF) | (val << 8);  // Pitch High
            else if (type == 4) _volume[ch] = val & 0x7F;
            else if (type == 5) _oct[ch] = (val >> 4) & 0x0F;  // Octave
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_volume);
        Array.Clear(_panpot);
        Array.Clear(_oct);
        Array.Clear(_pitch);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 28 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = _volume[ch];
            
            // Pan: 0=L, 7=Center, 15=R
            float pan = (_panpot[ch] - 7) / 8f;
            state.Channels[ch].PanLeft = (int)(127 * (1f - Math.Max(0, pan)));
            state.Channels[ch].PanRight = (int)(127 * (1f + Math.Min(0, pan)));
            
            // MultiPCM: Octave + Pitch -> Note
            // pitch=0x400 为基准，octave 调整八度
            if (_keyOn[ch] && _pitch[ch] > 0)
            {
                int octave = (_oct[ch] & 0x07) - 4;
                double ratio = _pitch[ch] / 1024.0;
                // octave 调整的基准音符
                int baseNote = 60 + octave * 12;
                state.Channels[ch].Note = VgmVisualizer.PcmRatioToNote(ratio, baseNote);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
    }
}

// SCSP 状态追踪器（Sega Saturn）
public class ScspTracker : VgmChipTracker
{
    private readonly int[] _volume = new int[32];
    private readonly int[] _oct = new int[32];
    private readonly int[] _fns = new int[32];
    private readonly int[] _panL = new int[32];
    private readonly int[] _panR = new int[32];
    private readonly bool[] _keyOn = new bool[32];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        int addr = (evt.Register << 8) | evt.Port;
        byte val = evt.Value;
        
        // SCSP每通違32字节
        int ch = (addr >> 5) & 0x1F;
        int reg = addr & 0x1F;
        
        switch (reg)
        {
            case 0x00: _keyOn[ch] = (val & 0x10) != 0; break;
            case 0x08: _oct[ch] = (val >> 3) & 0x0F; _fns[ch] = (_fns[ch] & 0x00FF) | ((val & 0x03) << 8); break;
            case 0x09: _fns[ch] = (_fns[ch] & 0x0300) | val; break;
            case 0x0A: _volume[ch] = val & 0x0F; break;
            case 0x12: _panL[ch] = (val >> 4) & 0x0F; _panR[ch] = val & 0x0F; break;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_volume);
        Array.Clear(_oct);
        Array.Clear(_fns);
        Array.Clear(_panL);
        Array.Clear(_panR);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 32 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = (15 - _volume[ch]) * 127 / 15;
            state.Channels[ch].PanLeft = _panL[ch] * 8;
            state.Channels[ch].PanRight = _panR[ch] * 8;
            
            // SCSP: OCT + FNS -> Note
            if (_keyOn[ch])
            {
                int oct = _oct[ch];
                if (oct > 7) oct -= 16;  // 符号扩展
                double freq = 261.63 * Math.Pow(2, oct) * (1.0 + _fns[ch] / 1024.0);
                state.Channels[ch].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
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
            int vol = _volume[ch] * 127 / 15;
            state.Channels[ch].KeyOn = active;
            state.Channels[ch].Volume = vol;
            // WonderSwan 立体声支持，但此处简化为单声道
            state.Channels[ch].PanLeft = vol;
            state.Channels[ch].PanRight = vol;
            
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
            int vol = _volume[ch] * 127 / 15;
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = vol;
            state.Channels[ch].PanLeft = vol;
            state.Channels[ch].PanRight = vol;
            state.Channels[ch].Note = -1;
        }
    }
}

// OKIM6295 状态追踪器
// 参考: https://vgmrips.net/wiki/OKIM6295
public class OKIM6295Tracker : VgmChipTracker
{
    private readonly bool[] _keyOn = new bool[4];
    private readonly int[] _attenuation = new int[4];  // 衰减值 (0-15, 0=最大, 15=静音)
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        if (reg == 0x00)
        {
            // 命令字节
            if ((val & 0x80) != 0)
            {
                // Key On: bit 0-3 选择通道
                for (int i = 0; i < 4; i++)
                    if ((val & (1 << i)) != 0) _keyOn[i] = true;
            }
            else if ((val & 0x78) != 0)
            {
                // Key Off: bit 3-6 选择通道
                for (int i = 0; i < 4; i++)
                    if ((val & (8 << i)) != 0) _keyOn[i] = false;
            }
        }
        else if (reg >= 0x08 && reg <= 0x0B)
        {
            // 音量寄存器：高4位是衰减值
            _attenuation[reg - 0x08] = (val >> 4) & 0x0F;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_keyOn);
        Array.Clear(_attenuation);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 4 && ch < state.Channels.Length; ch++)
        {
            // 衰减转音量 (0=最大音量127, 15=静音)
            int vol = (15 - _attenuation[ch]) * 127 / 15;
            
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = vol;
            // OKIM6295 是单声道，左右相同
            state.Channels[ch].PanLeft = vol;
            state.Channels[ch].PanRight = vol;
            state.Channels[ch].Note = -1;  // PCM 采样器无固定音高
        }
    }
}

// SegaPCM 状态追踪器
public class SegaPCMTracker : VgmChipTracker
{
    private readonly int[] _volumeL = new int[16];
    private readonly int[] _volumeR = new int[16];
    private readonly int[] _delta = new int[16];  // 采样增量（音高）
    private readonly bool[] _keyOn = new bool[16];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        // 16位地址: Port=高字节, Register=低字节
        int addr = evt.Register | (evt.Port << 8);
        byte val = evt.Value;
        
        // SegaPCM: 地址0x00-0x7F是通道寄存器区
        // 每通道8字节，共16通道
        if (addr < 0x80)
        {
            int ch = (addr >> 3) & 0x0F;
            int reg = addr & 0x07;
            
            switch (reg)
            {
                case 0x02: _volumeL[ch] = val; break;  // 左声道音量
                case 0x03: _volumeR[ch] = val; break;  // 右声道音量
                case 0x06: _keyOn[ch] = (val & 0x01) == 0; break;  // bit0=0表示播放
                case 0x07: _delta[ch] = val; break;  // 采样增量（音高）
            }
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_volumeL);
        Array.Clear(_volumeR);
        Array.Clear(_delta);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 16 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = Math.Max(_volumeL[ch], _volumeR[ch]) / 2;
            state.Channels[ch].PanLeft = _volumeL[ch];
            state.Channels[ch].PanRight = _volumeR[ch];
            
            // Delta -> Note: delta=0x80 为原始音高
            if (_keyOn[ch] && _delta[ch] > 0)
            {
                double ratio = _delta[ch] / 128.0;
                state.Channels[ch].Note = VgmVisualizer.PcmRatioToNote(ratio, 60);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
    }
}

// YMZ280B 状态追踪器
public class YMZ280BTracker : VgmChipTracker
{
    private readonly int[] _volume = new int[8];
    private readonly int[] _pitch = new int[8];
    private readonly int[] _panL = new int[8];
    private readonly int[] _panR = new int[8];
    private readonly bool[] _keyOn = new bool[8];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        // 每通道4个寄存器
        int ch = (reg >> 2) & 0x07;
        int type = reg & 0x03;
        
        switch (type)
        {
            case 0: _keyOn[ch] = (val & 0x80) != 0; _pitch[ch] = (_pitch[ch] & 0x00FF) | ((val & 0x01) << 8); break;
            case 1: _pitch[ch] = (_pitch[ch] & 0x0100) | val; break;  // pitch低8位
            case 2: _volume[ch] = val; break;
            case 3: _panL[ch] = val >> 4; _panR[ch] = val & 0x0F; break;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_volume);
        Array.Clear(_pitch);
        Array.Clear(_panL);
        Array.Clear(_panR);
        Array.Clear(_keyOn);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 8 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = _volume[ch] / 2;
            state.Channels[ch].PanLeft = _panL[ch] * 8;
            state.Channels[ch].PanRight = _panR[ch] * 8;
            
            // YMZ280B: pitch=0x100 为原始音高
            if (_keyOn[ch] && _pitch[ch] > 0)
            {
                double ratio = _pitch[ch] / 256.0;
                state.Channels[ch].Note = VgmVisualizer.PcmRatioToNote(ratio, 60);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
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
            int vol = _volume[ch] / 2;
            state.Channels[ch].KeyOn = _keyOn[ch];
            state.Channels[ch].Volume = vol;
            state.Channels[ch].PanLeft = vol;
            state.Channels[ch].PanRight = vol;
            state.Channels[ch].Note = -1;  // GA20 无音高信息
        }
    }
}

// YM2608 (OPNA) 状态追踪器 - 6 FM + 3 SSG + 6 Rhythm + 1 ADPCM
public class YM2608Tracker : VgmChipTracker
{
    // FM 部分 (6通道)
    private readonly int[] _fmFnum = new int[6];
    private readonly int[] _fmBlock = new int[6];
    private readonly int[,] _fmTl = new int[6, 4];
    private readonly int[] _fmAlgo = new int[6];
    private readonly int[] _fmLr = new int[6];
    private readonly bool[] _fmKeyOn = new bool[6];
    
    // SSG 部分 (3通道)
    private readonly int[] _ssgPeriod = new int[3];
    private readonly int[] _ssgVolume = new int[3];
    private readonly bool[] _ssgEnable = new bool[3];
    
    // Rhythm 部分 (6通道) - BD/SD/TOP/HH/TOM/RIM
    private readonly int[] _rhythmVol = new int[6];
    private bool _rhythmEnable;
    private int _rhythmKeyOn;
    
    // ADPCM-B (1通道)
    private int _adpcmDelta;
    private int _adpcmVolume;
    private bool _adpcmKeyOn;
    private int _adpcmPan;
    
    private static readonly int[] CarrierMask = { 0x08, 0x08, 0x08, 0x08, 0x0A, 0x0E, 0x0E, 0x0F };
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        int port = evt.Port;
        int chOffset = port * 3;
        
        // Port 0: SSG + FM 通道 0-2
        // Port 1: FM 通道 3-5 + ADPCM + Rhythm
        
        if (port == 0)
        {
            // SSG 部分 (0x00-0x0F)
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
        }
        
        // FM Key On (0x28)
        if (reg == 0x28 && port == 0)
        {
            int ch = val & 0x03;
            if ((val & 0x04) != 0) ch += 3;
            if (ch < 6) _fmKeyOn[ch] = (val & 0xF0) != 0;
        }
        // FM F-Number Low (0xA0-0xA2)
        else if (reg >= 0xA0 && reg <= 0xA2)
        {
            int ch = (reg - 0xA0) + chOffset;
            if (ch < 6) _fmFnum[ch] = (_fmFnum[ch] & 0x700) | val;
        }
        // FM F-Number High + Block (0xA4-0xA6)
        else if (reg >= 0xA4 && reg <= 0xA6)
        {
            int ch = (reg - 0xA4) + chOffset;
            if (ch < 6)
            {
                _fmFnum[ch] = (_fmFnum[ch] & 0xFF) | ((val & 0x07) << 8);
                _fmBlock[ch] = (val >> 3) & 0x07;
            }
        }
        // FM Algorithm (0xB0-0xB2)
        else if (reg >= 0xB0 && reg <= 0xB2)
        {
            int ch = (reg - 0xB0) + chOffset;
            if (ch < 6) _fmAlgo[ch] = val & 0x07;
        }
        // FM L/R (0xB4-0xB6)
        else if (reg >= 0xB4 && reg <= 0xB6)
        {
            int ch = (reg - 0xB4) + chOffset;
            if (ch < 6) _fmLr[ch] = (val >> 6) & 0x03;
        }
        // FM TL (0x40-0x4F)
        else if (reg >= 0x40 && reg <= 0x4F)
        {
            int op = (reg - 0x40) / 4;
            int ch = ((reg - 0x40) % 4) + chOffset;
            if (ch < 6 && ch % 4 < 3)
            {
                int slot = op == 0 ? 0 : op == 1 ? 2 : op == 2 ? 1 : 3;
                _fmTl[ch, slot] = val & 0x7F;
            }
        }
        // Rhythm (Port 0, 0x10-0x1D)
        else if (port == 0 && reg == 0x10)
        {
            _rhythmEnable = (val & 0x80) != 0;
            _rhythmKeyOn = val & 0x3F;
        }
        else if (port == 0 && reg >= 0x18 && reg <= 0x1D)
        {
            _rhythmVol[reg - 0x18] = val & 0x1F;
        }
        // ADPCM-B (Port 1)
        else if (port == 1)
        {
            if (reg == 0x00) _adpcmKeyOn = (val & 0x80) != 0;
            else if (reg == 0x01) _adpcmPan = (val >> 6) & 0x03;
            else if (reg == 0x09) _adpcmDelta = (_adpcmDelta & 0xFF00) | val;
            else if (reg == 0x0A) _adpcmDelta = (_adpcmDelta & 0x00FF) | (val << 8);
            else if (reg == 0x0B) _adpcmVolume = val;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_fmFnum);
        Array.Clear(_fmBlock);
        Array.Clear(_fmTl);
        Array.Clear(_fmAlgo);
        Array.Clear(_fmLr);
        Array.Clear(_fmKeyOn);
        Array.Clear(_ssgPeriod);
        Array.Clear(_ssgVolume);
        Array.Clear(_ssgEnable);
        Array.Clear(_rhythmVol);
        _rhythmEnable = false;
        _rhythmKeyOn = 0;
        _adpcmDelta = 0;
        _adpcmVolume = 0;
        _adpcmKeyOn = false;
        _adpcmPan = 0;
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        // FM 通道 (0-5)
        for (int ch = 0; ch < 6 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _fmKeyOn[ch];
            
            int mask = CarrierMask[_fmAlgo[ch]];
            int minTl = 127;
            for (int op = 0; op < 4; op++)
            {
                if ((mask & (1 << op)) != 0)
                    minTl = Math.Min(minTl, _fmTl[ch, op]);
            }
            int vol = Math.Max(0, 127 - minTl);
            state.Channels[ch].Volume = vol;
            
            int lr = _fmLr[ch];
            state.Channels[ch].PanLeft = (lr & 0x02) != 0 ? vol : 0;
            state.Channels[ch].PanRight = (lr & 0x01) != 0 ? vol : 0;
            
            if (_fmKeyOn[ch] && _fmFnum[ch] > 0)
            {
                double freq = _fmFnum[ch] * 7987200.0 / (72.0 * Math.Pow(2, 21 - _fmBlock[ch]));
                state.Channels[ch].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
        
        // SSG 通道 (6-8)
        for (int ch = 0; ch < 3 && ch + 6 < state.Channels.Length; ch++)
        {
            bool active = _ssgEnable[ch] && _ssgVolume[ch] > 0 && _ssgPeriod[ch] > 0;
            int vol = _ssgVolume[ch] * 127 / 15;
            state.Channels[ch + 6].KeyOn = active;
            state.Channels[ch + 6].Volume = vol;
            state.Channels[ch + 6].PanLeft = vol;
            state.Channels[ch + 6].PanRight = vol;
            
            if (active)
            {
                double freq = 7987200.0 / (64.0 * _ssgPeriod[ch]);
                state.Channels[ch + 6].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[ch + 6].Note = -1;
            }
        }
        
        // Rhythm 通道 (9-14): BD/SD/TOP/HH/TOM/RIM
        for (int ch = 0; ch < 6 && ch + 9 < state.Channels.Length; ch++)
        {
            bool active = _rhythmEnable && ((_rhythmKeyOn >> ch) & 1) != 0;
            int vol = _rhythmVol[ch] * 127 / 31;
            state.Channels[ch + 9].KeyOn = active;
            state.Channels[ch + 9].Volume = vol;
            state.Channels[ch + 9].PanLeft = vol;
            state.Channels[ch + 9].PanRight = vol;
            state.Channels[ch + 9].Note = -1;  // Rhythm 无音高
        }
        
        // ADPCM-B 通道 (15)
        if (state.Channels.Length > 15)
        {
            int vol = _adpcmVolume / 2;
            state.Channels[15].KeyOn = _adpcmKeyOn;
            state.Channels[15].Volume = vol;
            state.Channels[15].PanLeft = (_adpcmPan & 0x02) != 0 ? vol : 0;
            state.Channels[15].PanRight = (_adpcmPan & 0x01) != 0 ? vol : 0;
            
            // ADPCM-B: delta=0x49BA 约等于 8kHz 采样率
            if (_adpcmKeyOn && _adpcmDelta > 0)
            {
                double ratio = _adpcmDelta / 18874.0;  // 0x49BA = 18874
                state.Channels[15].Note = VgmVisualizer.PcmRatioToNote(ratio, 60);
            }
            else
            {
                state.Channels[15].Note = -1;
            }
        }
    }
}

// YM2610 (OPNB) 状态追踪器 - 4 FM + 3 SSG + 6 ADPCM-A + 1 ADPCM-B
public class YM2610Tracker : VgmChipTracker
{
    // FM 部分 (4通道: 1,2,4,5 通道 3 不可用)
    private readonly int[] _fmFnum = new int[4];
    private readonly int[] _fmBlock = new int[4];
    private readonly int[,] _fmTl = new int[4, 4];
    private readonly int[] _fmAlgo = new int[4];
    private readonly int[] _fmLr = new int[4];
    private readonly bool[] _fmKeyOn = new bool[4];
    
    // SSG 部分 (3通道)
    private readonly int[] _ssgPeriod = new int[3];
    private readonly int[] _ssgVolume = new int[3];
    private readonly bool[] _ssgEnable = new bool[3];
    
    // ADPCM-A (6通道)
    private readonly int[] _adpcmAVol = new int[6];
    private int _adpcmAKeyOn;
    private int _adpcmATotalVol;
    
    // ADPCM-B (1通道)
    private int _adpcmBDelta;
    private int _adpcmBVolume;
    private bool _adpcmBKeyOn;
    private int _adpcmBPan;
    
    private static readonly int[] CarrierMask = { 0x08, 0x08, 0x08, 0x08, 0x0A, 0x0E, 0x0E, 0x0F };
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        int port = evt.Port;
        
        if (port == 0)
        {
            // SSG (0x00-0x0F)
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
            // ADPCM-A
            else if (reg == 0x00) _adpcmAKeyOn = val & 0x3F;
            else if (reg == 0x01) _adpcmATotalVol = val & 0x3F;
            else if (reg >= 0x08 && reg <= 0x0D)
            {
                _adpcmAVol[reg - 0x08] = val & 0x1F;
            }
        }
        else if (port == 1)
        {
            // ADPCM-B
            if (reg == 0x10) _adpcmBKeyOn = (val & 0x80) != 0;
            else if (reg == 0x11) _adpcmBPan = (val >> 6) & 0x03;
            else if (reg == 0x19) _adpcmBDelta = (_adpcmBDelta & 0xFF00) | val;
            else if (reg == 0x1A) _adpcmBDelta = (_adpcmBDelta & 0x00FF) | (val << 8);
            else if (reg == 0x1B) _adpcmBVolume = val;
            // FM
            else if (reg == 0x28)
            {
                int ch = val & 0x03;
                if (ch < 4) _fmKeyOn[ch] = (val & 0xF0) != 0;
            }
            else if (reg >= 0xA0 && reg <= 0xA2)
            {
                int ch = reg - 0xA0;
                if (ch == 2) ch = 3;  // 通道映射
                if (ch < 4) _fmFnum[ch] = (_fmFnum[ch] & 0x700) | val;
            }
            else if (reg >= 0xA4 && reg <= 0xA6)
            {
                int ch = reg - 0xA4;
                if (ch == 2) ch = 3;
                if (ch < 4)
                {
                    _fmFnum[ch] = (_fmFnum[ch] & 0xFF) | ((val & 0x07) << 8);
                    _fmBlock[ch] = (val >> 3) & 0x07;
                }
            }
            else if (reg >= 0xB0 && reg <= 0xB2)
            {
                int ch = reg - 0xB0;
                if (ch == 2) ch = 3;
                if (ch < 4) _fmAlgo[ch] = val & 0x07;
            }
            else if (reg >= 0xB4 && reg <= 0xB6)
            {
                int ch = reg - 0xB4;
                if (ch == 2) ch = 3;
                if (ch < 4) _fmLr[ch] = (val >> 6) & 0x03;
            }
            else if (reg >= 0x40 && reg <= 0x4F)
            {
                int op = (reg - 0x40) / 4;
                int ch = (reg - 0x40) % 4;
                if (ch == 2) ch = 3;
                if (ch < 4)
                {
                    int slot = op == 0 ? 0 : op == 1 ? 2 : op == 2 ? 1 : 3;
                    _fmTl[ch, slot] = val & 0x7F;
                }
            }
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_fmFnum);
        Array.Clear(_fmBlock);
        Array.Clear(_fmTl);
        Array.Clear(_fmAlgo);
        Array.Clear(_fmLr);
        Array.Clear(_fmKeyOn);
        Array.Clear(_ssgPeriod);
        Array.Clear(_ssgVolume);
        Array.Clear(_ssgEnable);
        Array.Clear(_adpcmAVol);
        _adpcmAKeyOn = 0;
        _adpcmATotalVol = 0;
        _adpcmBDelta = 0;
        _adpcmBVolume = 0;
        _adpcmBKeyOn = false;
        _adpcmBPan = 0;
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        // FM 通道 (0-3)
        for (int ch = 0; ch < 4 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _fmKeyOn[ch];
            
            int mask = CarrierMask[_fmAlgo[ch]];
            int minTl = 127;
            for (int op = 0; op < 4; op++)
            {
                if ((mask & (1 << op)) != 0)
                    minTl = Math.Min(minTl, _fmTl[ch, op]);
            }
            int vol = Math.Max(0, 127 - minTl);
            state.Channels[ch].Volume = vol;
            
            int lr = _fmLr[ch];
            state.Channels[ch].PanLeft = (lr & 0x02) != 0 ? vol : 0;
            state.Channels[ch].PanRight = (lr & 0x01) != 0 ? vol : 0;
            
            if (_fmKeyOn[ch] && _fmFnum[ch] > 0)
            {
                double freq = _fmFnum[ch] * 8000000.0 / (72.0 * Math.Pow(2, 21 - _fmBlock[ch]));
                state.Channels[ch].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
        
        // SSG 通道 (4-6)
        for (int ch = 0; ch < 3 && ch + 4 < state.Channels.Length; ch++)
        {
            bool active = _ssgEnable[ch] && _ssgVolume[ch] > 0 && _ssgPeriod[ch] > 0;
            int vol = _ssgVolume[ch] * 127 / 15;
            state.Channels[ch + 4].KeyOn = active;
            state.Channels[ch + 4].Volume = vol;
            state.Channels[ch + 4].PanLeft = vol;
            state.Channels[ch + 4].PanRight = vol;
            
            if (active)
            {
                double freq = 8000000.0 / (64.0 * _ssgPeriod[ch]);
                state.Channels[ch + 4].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[ch + 4].Note = -1;
            }
        }
        
        // ADPCM-A 通道 (7-12)
        for (int ch = 0; ch < 6 && ch + 7 < state.Channels.Length; ch++)
        {
            bool active = ((_adpcmAKeyOn >> ch) & 1) != 0;
            int vol = (_adpcmATotalVol + _adpcmAVol[ch]) * 127 / 94;
            vol = Math.Min(127, vol);
            state.Channels[ch + 7].KeyOn = active;
            state.Channels[ch + 7].Volume = vol;
            state.Channels[ch + 7].PanLeft = vol;
            state.Channels[ch + 7].PanRight = vol;
            state.Channels[ch + 7].Note = -1;  // ADPCM-A 无音高
        }
        
        // ADPCM-B 通道 (13)
        if (state.Channels.Length > 13)
        {
            int vol = _adpcmBVolume / 2;
            state.Channels[13].KeyOn = _adpcmBKeyOn;
            state.Channels[13].Volume = vol;
            state.Channels[13].PanLeft = (_adpcmBPan & 0x02) != 0 ? vol : 0;
            state.Channels[13].PanRight = (_adpcmBPan & 0x01) != 0 ? vol : 0;
            
            if (_adpcmBKeyOn && _adpcmBDelta > 0)
            {
                double ratio = _adpcmBDelta / 18874.0;
                state.Channels[13].Note = VgmVisualizer.PcmRatioToNote(ratio, 60);
            }
            else
            {
                state.Channels[13].Note = -1;
            }
        }
    }
}
