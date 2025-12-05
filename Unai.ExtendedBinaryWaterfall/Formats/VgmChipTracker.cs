using System;

namespace Unai.ExtendedBinaryWaterfall;

// 芯片状态追踪器基类
public abstract class VgmChipTracker
{
    // 芯片时钟频率（从 VGM 头读取）
    public uint Clock { get; set; }
    
    public abstract void ProcessEvent(VgmEvent evt);
    public abstract void Reset();
    public abstract void UpdateVisualizerState(VgmVisualizer.ChipState state);
}

// YM2612 状态追踪器（Mega Drive/Genesis FM 芯片）
public class YM2612Tracker : VgmChipTracker
{
    // 6 个 FM 通道，通道 6 可切换为 DAC/PCM 模式
    private readonly int[] _fnum = new int[6];
    private readonly int[] _block = new int[6];
    private readonly int[,] _tl = new int[6, 4];  // 每通道 4 个算子的 TL
    private readonly int[] _algo = new int[6];    // 算法
    private readonly int[] _lr = new int[6];      // Left/Right 输出选择
    private readonly bool[] _keyOn = new bool[6];
    private bool _dacEnable;                      // DAC 使能 (0x2B bit 7)
    private int _dacData;                         // DAC 数据 (0x2A)
    private bool _dacActive;                      // DAC 是否有数据输出
    private bool _dacStreamActive;                // DAC 流是否活动 (从0x93/0x95命令)
    private uint _dacSampleRate;                  // DAC 采样率 (从 VGM DAC Stream 命令获取)
    
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
            int regCh = (reg - 0x40) % 4;  // 寄存器中的通道号 (0,1,2有效，3无效)
            if (regCh < 3)
            {
                int ch = regCh + chOffset;
                if (ch < 6)
                {
                    // 寄存器布局转换: 0->S1, 1->S3, 2->S2, 3->S4
                    int slot = op == 0 ? 0 : op == 1 ? 2 : op == 2 ? 1 : 3;
                    _tl[ch, slot] = val & 0x7F;
                }
            }
        }
        // DAC 数据 (0x2A)
        else if (reg == 0x2A)
        {
            _dacData = val;
            // 写入 DAC 数据时，标记 DAC 活动
            if (_dacEnable) _dacActive = true;
        }
        // DAC 使能 (0x2B)
        else if (reg == 0x2B)
        {
            _dacEnable = (val & 0x80) != 0;
            // DAC 关闭时，清除活动状态
            if (!_dacEnable) _dacActive = false;
        }
        // L/R 输出选择 (0xB4-0xB6)
        else if (reg >= 0xB4 && reg <= 0xB6)
        {
            int ch = (reg - 0xB4) + chOffset;
            if (ch < 6) _lr[ch] = (val >> 6) & 0x03;  // bit7=L, bit6=R
        }
        // 特殊: DAC 采样率 (从 VGM DAC Stream 命令 0x92 获取)
        // Register=0xFF, Port=freq[23:16], Value=freq[15:8], Value2=freq[7:0]
        else if (reg == 0xFF)
        {
            _dacSampleRate = (uint)((evt.Port << 16) | (val << 8) | evt.Value2);
        }
        // 特殊: DAC 流开始 (从 VGM DAC Stream 命令 0x93/0x95 获取)
        else if (reg == 0xFE)
        {
            _dacStreamActive = true;
            _dacEnable = true;  // DAC流命令隐含启用DAC
        }
        // 特殊: DAC 流停止 (从 VGM DAC Stream 命令 0x94 获取)
        else if (reg == 0xFD)
        {
            _dacStreamActive = false;
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
        _dacData = 0;
        _dacActive = false;
        _dacStreamActive = false;
        _dacSampleRate = 0;
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        // FM 通道 1-5 (索引 0-4)
        for (int ch = 0; ch < 5; ch++)
        {
            if (ch >= state.Channels.Length) break;
            UpdateFmChannel(state, ch);
        }
        
        // 通道 6 (索引 5): DAC 启用时显示 PCM，否则显示 FM
        if (state.Channels.Length > 5)
        {
            if (_dacEnable)
            {
                // DAC/PCM 模式: 通道 6 被 DAC 占用
                // DAC活动状态：直接写入0x2A或DAC流命令
                bool dacPlaying = _dacActive || _dacStreamActive;
                state.Channels[5].KeyOn = dacPlaying;
                state.Channels[5].Volume = dacPlaying ? 127 : 0;
                state.Channels[5].PanLeft = dacPlaying ? 127 : 0;
                state.Channels[5].PanRight = dacPlaying ? 127 : 0;
                
                if (dacPlaying)
                {
                    // Detune始终显示当前DAC数据值
                    state.Channels[5].Detune = _dacData;
                    
                    if (_dacSampleRate > 0)
                    {
                        // 采样率比例转音高: ratio = sampleRate / 22050
                        // 每翻倍采样率，音高上升 12 个半音 (以22050Hz为基准 = C4)
                        double ratio = _dacSampleRate / 22050.0;
                        state.Channels[5].Note = VgmVisualizer.PcmRatioToNote(ratio, 60);  // C4 = 60
                    }
                    else
                    {
                        // 没有采样率信息时，使用固定音符 C4
                        state.Channels[5].Note = 60;  // C4
                    }
                }
                else
                {
                    state.Channels[5].Note = -1;
                    state.Channels[5].Detune = 0;
                }
            }
            else
            {
                // FM 模式
                UpdateFmChannel(state, 5);
            }
        }
        
        // DAC 活动状态在下一帧重置（需要持续写入才保持活动）
        _dacActive = false;
    }
    
    // 更新单个 FM 通道状态
    private void UpdateFmChannel(VgmVisualizer.ChipState state, int ch)
    {
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
            // 计算音符和音分偏移
            var (note, cent) = FnumToNoteAndCent(_fnum[ch], _block[ch]);
            state.Channels[ch].Note = note;
            state.Channels[ch].Detune = cent;
        }
        else
        {
            state.Channels[ch].Note = -1;
            state.Channels[ch].Detune = 0;
        }
    }
    
    // YM2612 F-Number 转音符和音分偏移
    // 公式: freq = fnum * clock / (144 * 2^(21-block))
    private (int note, int cent) FnumToNoteAndCent(int fnum, int block)
    {
        if (fnum == 0) return (-1, 0);
        double clock = Clock > 0 ? Clock : 7670453.0;
        double freq = fnum * clock / (144.0 * Math.Pow(2, 21 - block));
        return VgmVisualizer.FrequencyToNoteAndCent(freq);
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
                var (note, cent) = PeriodToNoteAndCent(_tone[ch]);
                state.Channels[ch].Note = note;
                state.Channels[ch].Detune = cent;
            }
            else
            {
                state.Channels[ch].Note = -1;
                state.Channels[ch].Detune = 0;
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
            state.Channels[3].Detune = 0;
        }
    }
    
    // SN76489 周期转音符和音分偏移
    private (int note, int cent) PeriodToNoteAndCent(int period)
    {
        if (period <= 1) return (-1, 0);
        double clock = Clock > 0 ? Clock : 3579545.0;
        double freq = clock / (32.0 * period);
        return VgmVisualizer.FrequencyToNoteAndCent(freq);
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
                // AY-3-8910: freq = clock / (16 * period)
                double clock = Clock > 0 ? Clock : 1789773.0;
                double freq = clock / (16.0 * _tonePeriod[ch]);
                var (note, cent) = VgmVisualizer.FrequencyToNoteAndCent(freq);
                state.Channels[ch].Note = note;
                state.Channels[ch].Detune = cent;
            }
            else
            {
                state.Channels[ch].Note = -1;
                state.Channels[ch].Detune = 0;
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
    private readonly int[,] _dt1 = new int[8, 4];  // DT1 (Detune 1) 每算子
    private readonly int[] _algo = new int[8];     // 算法
    private readonly int[] _rl = new int[8];       // RL (Right/Left) 声道选择
    private readonly bool[] _keyOn = new bool[8];
    private readonly int[] _pms = new int[8];      // PMS (LFO Pitch Modulation Sensitivity)
    
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
            _rl[ch] = val & 0xC0;
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
        // PMS/AMS (0x38-0x3F): bit6-4=PMS, bit1-0=AMS
        else if (reg >= 0x38 && reg <= 0x3F)
        {
            int ch = reg & 0x07;
            _pms[ch] = (val >> 4) & 0x07;
        }
        // DT1/MUL (0x40-0x5F: 4 算子 x 8 通道)
        else if (reg >= 0x40 && reg <= 0x5F)
        {
            int op = (reg - 0x40) / 8;
            int ch = reg & 0x07;
            _dt1[ch, op] = (val >> 4) & 0x07;  // DT1: bit6-4
        }
        // TL (0x60-0x7F: 4 个算子 x 8 通道)
        else if (reg >= 0x60 && reg <= 0x7F)
        {
            int op = (reg - 0x60) / 8;
            int ch = reg & 0x07;
            _tl[ch, op] = val & 0x7F;
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_kc);
        Array.Clear(_kf);
        Array.Clear(_tl);
        Array.Clear(_dt1);
        Array.Clear(_algo);
        Array.Clear(_rl);
        Array.Clear(_keyOn);
        Array.Clear(_pms);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 8 && ch < state.Channels.Length; ch++)
        {
            state.Channels[ch].KeyOn = _keyOn[ch];
            
            // 根据算法计算载波算子的最小 TL
            int mask = CarrierMask[_algo[ch]];
            int minTl = 127;
            int sumDt1 = 0;
            int carrierCount = 0;
            for (int op = 0; op < 4; op++)
            {
                if ((mask & (1 << op)) != 0)
                {
                    minTl = Math.Min(minTl, _tl[ch, op]);
                    // DT1: 0-3=正向 detune, 4-7=负向 detune (4=0, 5=-1, 6=-2, 7=-3)
                    int dt = _dt1[ch, op];
                    sumDt1 += (dt < 4) ? dt : -(dt - 4);
                    carrierCount++;
                }
            }
            int vol = Math.Max(0, 127 - minTl);
            state.Channels[ch].Volume = vol;
            
            // Detune: 使用 Key Fraction (KF) 相对于中心值的偏移
            // KF: 0-63，32为中心值（无偏移），偏移量 = (kf - 32) * 100 / 64 cent
            // 但YM2151实际上KF=0表示无偏移，所以直接显示KF转换的cent值
            // 如果KF=0表示无偏移，则Detune=0
            state.Channels[ch].Detune = _kf[ch] > 0 ? (_kf[ch] * 100 / 64) : 0;
            
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
            // Detune: F-Number 低 8 位
            state.Channels[ch].Detune = _fnum[ch] & 0xFF;
            
            if (_keyOn[ch] && _fnum[ch] > 0)
            {
                // OPLL: freq = fnum * clock / (72 * 2^(19-block))
                double clock = Clock > 0 ? Clock : 3579545.0;
                double freq = _fnum[ch] * clock / (72.0 * Math.Pow(2, 19 - _block[ch]));
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
            // Detune: F-Number 低 8 位
            state.Channels[ch].Detune = _fmFnum[ch] & 0xFF;
            
            if (_fmKeyOn[ch] && _fmFnum[ch] > 0)
            {
                // YM2203 (OPN): freq = fnum * clock / (72 * 2^(21-block))
                double clock = Clock > 0 ? Clock : 4000000.0;
                double freq = _fmFnum[ch] * clock / (72.0 * Math.Pow(2, 21 - _fmBlock[ch]));
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
            // Detune: SSG 频率低 8 位
            state.Channels[ch + 3].Detune = _ssgPeriod[ch] & 0xFF;
            
            if (active)
            {
                // YM2203 SSG: freq = clock / (16 * period)
                // SSG 时钟为 FM 时钟的 1/2
                double clock = Clock > 0 ? Clock / 2.0 : 2000000.0;
                double freq = clock / (16.0 * _ssgPeriod[ch]);
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
    private int _linearCounterReload;                 // Triangle 线性计数器重载值
    private bool _triangleKeyOn;                      // Triangle 是否处于 Key On 状态
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
            _linearCounterReload = val & 0x7F;
            // 线性计数器重载值为 0 时，Key Off
            if (_linearCounterReload == 0) _triangleKeyOn = false;
        }
        else if (reg == 0x0A) _period[2] = (_period[2] & 0x700) | val;
        else if (reg == 0x0B)
        {
            _period[2] = (_period[2] & 0x0FF) | ((val & 0x07) << 8);
            // 写入 0x0B 会重新加载线性计数器，触发 Key On
            if (_linearCounterReload > 0) _triangleKeyOn = true;
        }
        
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
            // 清除 Triangle enable 时，Key Off
            if (!_enable[2]) _triangleKeyOn = false;
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
        _linearCounterReload = 0;
        _triangleKeyOn = false;
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
            state.Channels[0].Detune = _period[0] & 0xFF;
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
            state.Channels[1].Detune = _period[1] & 0xFF;
            state.Channels[1].Note = active ? PeriodToNote(_period[1]) : -1;
        }
        
        // Triangle (无音量控制)
        if (state.Channels.Length > 2)
        {
            // Triangle 活动条件: enable && keyOn && period 有效
            bool active = _enable[2] && _triangleKeyOn && _period[2] >= 2;
            int vol = active ? 127 : 0;
            state.Channels[2].KeyOn = active;
            state.Channels[2].Volume = vol;
            state.Channels[2].PanLeft = vol;
            state.Channels[2].PanRight = vol;
            state.Channels[2].Detune = _period[2] & 0xFF;
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
    
    // NES APU Pulse 频率: f = CPU_clock / (16 * (period + 1))
    private int PeriodToNote(int period)
    {
        if (period < 8) return -1;
        // 使用 VGM 头中的时钟，默认 NTSC 1789773 Hz
        double clock = Clock > 0 ? Clock : 1789773.0;
        double freq = clock / (16.0 * (period + 1));
        return VgmVisualizer.FrequencyToNote(freq);
    }
    
    // NES APU Triangle 频率: f = CPU_clock / (32 * (period + 1))
    private int TrianglePeriodToNote(int period)
    {
        if (period < 2) return -1;
        double clock = Clock > 0 ? Clock : 1789773.0;
        double freq = clock / (32.0 * (period + 1));
        return VgmVisualizer.FrequencyToNote(freq);
    }
}

// Game Boy DMG 状态追踪器
public class GbDmgTracker : VgmChipTracker
{
    private readonly int[] _freq = new int[3];      // CH1, CH2, Wave
    private readonly int[] _nrx2 = new int[4];      // NRx2 寄存器值 (DAC 控制)
    private readonly bool[] _keyOn = new bool[4];   // Key On 状态
    private bool _dacCh3;                           // CH3 DAC enable (NR30 bit 7)
    private bool _masterEnable;                     // 主开关 (NR52 bit 7)
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        
        // CH1 (0x10-0x14)
        if (reg == 0x12)
        {
            _nrx2[0] = val;
            // DAC off 时 Key Off
            if ((val & 0xF8) == 0) _keyOn[0] = false;
        }
        else if (reg == 0x13) _freq[0] = (_freq[0] & 0x700) | val;
        else if (reg == 0x14)
        {
            _freq[0] = (_freq[0] & 0x0FF) | ((val & 0x07) << 8);
            // bit 7 = trigger, DAC 开启时触发 Key On
            if ((val & 0x80) != 0 && (_nrx2[0] & 0xF8) != 0)
                _keyOn[0] = true;
        }
        
        // CH2 (0x16-0x19)
        else if (reg == 0x17)
        {
            _nrx2[1] = val;
            if ((val & 0xF8) == 0) _keyOn[1] = false;
        }
        else if (reg == 0x18) _freq[1] = (_freq[1] & 0x700) | val;
        else if (reg == 0x19)
        {
            _freq[1] = (_freq[1] & 0x0FF) | ((val & 0x07) << 8);
            if ((val & 0x80) != 0 && (_nrx2[1] & 0xF8) != 0)
                _keyOn[1] = true;
        }
        
        // CH3 Wave (0x1A-0x1E)
        else if (reg == 0x1A)
        {
            _dacCh3 = (val & 0x80) != 0;
            // DAC off 时 Key Off
            if (!_dacCh3) _keyOn[2] = false;
        }
        else if (reg == 0x1C) _nrx2[2] = val;  // 音量代码 (0-3)
        else if (reg == 0x1D) _freq[2] = (_freq[2] & 0x700) | val;
        else if (reg == 0x1E)
        {
            _freq[2] = (_freq[2] & 0x0FF) | ((val & 0x07) << 8);
            // DAC 开启时触发 Key On
            if ((val & 0x80) != 0 && _dacCh3)
                _keyOn[2] = true;
        }
        
        // CH4 Noise (0x20-0x23)
        else if (reg == 0x21)
        {
            _nrx2[3] = val;
            if ((val & 0xF8) == 0) _keyOn[3] = false;
        }
        else if (reg == 0x23)
        {
            if ((val & 0x80) != 0 && (_nrx2[3] & 0xF8) != 0)
                _keyOn[3] = true;
        }
        
        // NR52 Master control (0x26)
        else if (reg == 0x26)
        {
            _masterEnable = (val & 0x80) != 0;
            // 关闭主开关时，所有通道 Key Off
            if (!_masterEnable)
            {
                Array.Clear(_keyOn);
            }
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_freq);
        Array.Clear(_nrx2);
        Array.Clear(_keyOn);
        _dacCh3 = false;
        _masterEnable = false;
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        // CH1, CH2: 音量从 NRx2 高 4 位获取
        for (int ch = 0; ch < 2 && ch < state.Channels.Length; ch++)
        {
            int volume = (_nrx2[ch] >> 4) & 0x0F;
            bool active = _keyOn[ch] && volume > 0;
            int vol = volume * 127 / 15;
            state.Channels[ch].KeyOn = active;
            state.Channels[ch].Volume = vol;
            state.Channels[ch].PanLeft = vol;
            state.Channels[ch].PanRight = vol;
            state.Channels[ch].Detune = _freq[ch] & 0xFF;
            
            if (active && _freq[ch] > 0)
            {
                // GB DMG: freq = clock / (32 * (2048 - freq_reg))
                double clock = Clock > 0 ? Clock / 32.0 : 131072.0;
                double freq = clock / (2048 - _freq[ch]);
                state.Channels[ch].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
        
        // CH3 Wave: 音量从 NR32 (0x1C) 的 bit 5-6 获取 (0-3)
        if (state.Channels.Length > 2)
        {
            int volCode = (_nrx2[2] >> 5) & 0x03;
            // 音量代码: 0=静音, 1=100%, 2=50%, 3=25%
            int[] volTable = { 0, 127, 64, 32 };
            int vol = volTable[volCode];
            bool active = _keyOn[2] && volCode > 0;
            state.Channels[2].KeyOn = active;
            state.Channels[2].Volume = vol;
            state.Channels[2].PanLeft = vol;
            state.Channels[2].PanRight = vol;
            state.Channels[2].Detune = _freq[2] & 0xFF;
            
            if (active && _freq[2] > 0)
            {
                // CH3 频率是 CH1/CH2 的两倍 (clock/64 vs clock/32)
                double clock = Clock > 0 ? Clock / 64.0 : 65536.0;
                double freq = clock / (2048 - _freq[2]);
                state.Channels[2].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[2].Note = -1;
            }
        }
        
        // CH4 Noise: 音量从 NR42 (0x21) 高 4 位获取
        if (state.Channels.Length > 3)
        {
            int volume = (_nrx2[3] >> 4) & 0x0F;
            int vol = volume * 127 / 15;
            state.Channels[3].KeyOn = _keyOn[3] && volume > 0;
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
            state.Channels[ch].Detune = _freq[ch] & 0xFF;
            
            if (active)
            {
                // HuC6280: freq = clock / (32 * period)
                double clock = Clock > 0 ? Clock : 3579545.0;
                double freq = clock / (32.0 * _freq[ch]);
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
            state.Channels[ch].Detune = _fnum[ch] & 0xFF;
            
            if (_keyOn[ch] && _fnum[ch] > 0)
            {
                // OPL: freq = fnum * clock / (72 * 2^(20-block))
                double clock = Clock > 0 ? Clock : 3579545.0;
                double freq = _fnum[ch] * clock / (72.0 * Math.Pow(2, 20 - _block[ch]));
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
            // Detune: 相对于 0x1000 的偏移
            state.Channels[ch].Detune = _pitch[ch] - 0x1000;
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
            state.Channels[ch].Detune = _freq[ch] & 0xFF;
            
            if (active)
            {
                // K051649 (SCC): freq = clock / (32 * period)
                double clock = Clock > 0 ? Clock : 3579545.0;
                double freq = clock / (32.0 * _freq[ch]);
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
            
            state.Channels[ch].Detune = _freq[ch];
            if (active)
            {
                // POKEY: freq = clock / (2 * (period + 1))
                double clock = Clock > 0 ? Clock : 1789773.0;
                double freq = clock / (2.0 * (_freq[ch] + 1));
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
            state.Channels[ch].Detune = _freq[ch];
            
            if (active && _freq[ch] > 0)
            {
                // SAA1099: freq = clock / (512 * period * 2^(8-octave))
                double clock = Clock > 0 ? Clock : 7159090.0;
                double freq = clock / (512.0 * _freq[ch] * Math.Pow(2, 8 - _octave[ch]));
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
            int fd = _fdLow[ch] | (_fdHigh[ch] << 8);
            state.Channels[ch].Detune = fd - 0x800;
            if (active)
            {
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
            int freq16 = (_freqH[ch] << 8) | _freqL[ch];
            state.Channels[ch].Detune = freq16 - 0x1000;
            if (_keyOn[ch])
            {
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
            state.Channels[ch].Detune = _freq[ch] - 0x10000;
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
            state.Channels[ch].Detune = _pitch[ch] - 0x800;
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
            state.Channels[ch].Detune = (_pitch[ch] - 0x10000) >> 8;  // 取高 16 位
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
            state.Channels[ch].Detune = _pitch[ch] - 0x400;
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
            state.Channels[ch].Detune = _fns[ch];
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
            state.Channels[ch].Detune = _freq[ch] & 0xFF;
            
            if (active && _freq[ch] > 0)
            {
                // WonderSwan: freq = clock / (32 * (2048 - freq_reg))
                double clock = Clock > 0 ? Clock : 3072000.0;
                double freq = clock / (32.0 * (2048 - _freq[ch]));
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
// 参考: MAME okim6295.cpp
// 4通道 ADPCM 采样播放芯片
// 采样率 = clock / 132 (pin7=H) 或 clock / 165 (pin7=L)
// VGM 命令格式: 0xB8 aa dd
//   aa的bit7 = 芯片选择 (0=第一芯片, 1=第二芯片)
//   aa的bit0-6 = 偏移量 (offset)
//   当offset=0时，dd是写入芯片的命令字节
// OKIM6295命令是顺序的两字节序列:
//   1xxx xxxx = 采样播放命令开始，xxxxxxx是采样号(0-127)
//   abcd vvvv = 命令第二字节，abcd中只有一位为1表示通道，vvvv是衰减
//   0abc d000 = 停止命令，abcd是通道停止掩码
// 音量表 (从MAME): 每3dB衰减约50%
public class OKIM6295Tracker : VgmChipTracker
{
    private readonly bool[] _keyOn = new bool[4];
    private readonly int[] _attenuation = new int[4];  // 衰减值 (0-15)
    private readonly int[] _sampleNum = new int[4];    // 正在播放的采样号 (0-127)
    
    // 命令状态：-1表示等待新命令，>=0表示等待第二字节（值为采样号）
    private int _pendingCommand = -1;
    
    // MAME音量表 (okim6295.cpp): 精确的dB衰减值
    // 0x20/0x20=1.0(0dB), 0x16/0x20=0.6875(-3.2dB), 0x10/0x20=0.5(-6dB)...
    // 索引9-15为静音
    private static readonly int[] VolumeTable = { 127, 87, 64, 44, 32, 24, 16, 12, 8, 0, 0, 0, 0, 0, 0, 0 };
    
    // DAC流状态 (用于DAC Stream Control命令)
    private readonly bool[] _streamActive = new bool[16];
    private readonly int[] _streamChannel = new int[16];  // 流对应的通道
    private int _nextStreamChannel = 0;  // 下一个分配的通道
    
    public override void ProcessEvent(VgmEvent evt)
    {
        // VGM命令 0xB8 aa dd:
        // aa的bit7 = 芯片选择 (0=第一芯片, 1=第二芯片)
        // aa的bit0-6 = 偏移量 (offset)
        // 当offset=0时，dd是写入芯片的命令字节
        
        byte aa = evt.Register;
        byte data = evt.Value;
        int offset = aa & 0x7F;  // 去掉芯片选择位，得到偏移量
        
        // 处理DAC流控制命令 (特殊寄存器值)
        if (aa == 0xFE)
        {
            // DAC流开始 (来自0x93或0x95命令)
            int streamId = data;
            if (streamId < 16)
            {
                // 分配一个通道给这个流
                int ch = _nextStreamChannel % 4;
                _streamChannel[streamId] = ch;
                _streamActive[streamId] = true;
                _keyOn[ch] = true;
                _sampleNum[ch] = streamId;  // 用流ID作为采样号
                _attenuation[ch] = 0;  // 最大音量
                _nextStreamChannel++;
            }
            return;
        }
        else if (aa == 0xFD)
        {
            // DAC流停止 (来自0x94命令)
            int streamId = data;
            if (streamId < 16 && _streamActive[streamId])
            {
                int ch = _streamChannel[streamId];
                _keyOn[ch] = false;
                _streamActive[streamId] = false;
            }
            return;
        }
        else if (aa == 0xFF)
        {
            // DAC采样率设置 (来自0x92命令) - 忽略，用于音频播放
            return;
        }
        
        // 只处理offset=0的命令写入，其他offset是特殊控制（时钟、bank等）
        if (offset != 0)
            return;
        
        // 处理OKIM6295的顺序命令
        // 参考libvgm/MAME的okim6295_write_command实现
        
        if (_pendingCommand != -1)
        {
            // 有挂起命令，这是命令序列的第二字节
            // data = abcd vvvv，abcd是通道选择（只有一位为1），vvvv是衰减
            int voiceMask = (data >> 4) & 0x0F;
            int attenuation = data & 0x0F;
            
            // 遍历通道，找到被选中的通道
            for (int ch = 0; ch < 4; ch++)
            {
                if ((voiceMask & (1 << ch)) != 0)
                {
                    // 只在通道空闲时开始播放（参考MAME: fixes Got-cha and Steel Force）
                    if (!_keyOn[ch])
                    {
                        _keyOn[ch] = true;
                        _sampleNum[ch] = _pendingCommand;
                        _attenuation[ch] = attenuation;
                    }
                }
            }
            
            // 重置挂起命令
            _pendingCommand = -1;
        }
        else if ((data & 0x80) != 0)
        {
            // 命令序列开始：1xxx xxxx，保存采样号等待第二字节
            _pendingCommand = data & 0x7F;
        }
        else
        {
            // 停止命令：0abc d000，bit3-6是通道停止掩码
            int voiceMask = (data >> 3) & 0x0F;
            for (int ch = 0; ch < 4; ch++)
            {
                if ((voiceMask & (1 << ch)) != 0)
                {
                    _keyOn[ch] = false;
                    _sampleNum[ch] = -1;
                }
            }
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_keyOn);
        Array.Clear(_attenuation);
        Array.Clear(_streamActive);
        Array.Clear(_streamChannel);
        _nextStreamChannel = 0;
        _pendingCommand = -1;
        // 初始化采样号为-1（无效），区分于采样0
        for (int i = 0; i < 4; i++)
        {
            _sampleNum[i] = -1;
        }
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int ch = 0; ch < 4 && ch < state.Channels.Length; ch++)
        {
            // 使用查表获取音量
            int vol = VolumeTable[Math.Min(15, _attenuation[ch])];
            
            // 只有KeyOn且有有效采样号时才显示
            bool active = _keyOn[ch] && _sampleNum[ch] >= 0;
            
            state.Channels[ch].KeyOn = active;
            state.Channels[ch].Volume = active ? vol : 0;
            state.Channels[ch].PanLeft = active ? vol : 0;
            state.Channels[ch].PanRight = active ? vol : 0;
            
            if (active)
            {
                // OKIM6295是采样播放芯片，不同采样代表不同音效/乐器（非实际音高）
                // 为了在钢琴窗中可视化区分不同采样，将采样号映射到不同音高位置
                // 采样号0-127映射到C2(36)-G9，使不同采样在钢琴卷帘上显示在不同位置
                int note = 36 + _sampleNum[ch];
                state.Channels[ch].Note = Math.Clamp(note, 0, 127);
                state.Channels[ch].Detune = _sampleNum[ch];
            }
            else
            {
                state.Channels[ch].Note = -1;
                state.Channels[ch].Detune = 0;
            }
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
        
        // SegaPCM 寄存器布局 (参考 MAME segapcm.cpp):
        // 地址 0x00-0x7F: 通道 0-15 的寄存器 0-7 (每通道8字节)
        //   reg 0x02: volume left
        //   reg 0x03: volume right
        //   reg 0x07: address delta (采样增量/音高)
        // 地址 0x80-0xFF: 通道 0-15 的控制寄存器
        //   reg 0x86: bit 0 = channel disable (1=停止, 0=播放)
        
        int ch = (addr >> 3) & 0x0F;
        int reg = addr & 0x87;  // 保留 bit 7 和 bit 0-2
        
        switch (reg)
        {
            case 0x02: _volumeL[ch] = val & 0x7F; break;  // 左声道音量 (7-bit)
            case 0x03: _volumeR[ch] = val & 0x7F; break;  // 右声道音量 (7-bit)
            case 0x07: _delta[ch] = val; break;          // 采样增量（音高）
            case 0x86:                                    // 通道控制寄存器
                _keyOn[ch] = (val & 0x01) == 0;          // bit 0: 0=播放, 1=停止
                break;
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
            int vol = Math.Max(_volumeL[ch], _volumeR[ch]);
            
            // 如果有音量，即使没有检测到 KeyOn 也认为在播放
            // (有些 VGM 可能先设置音量再触发播放，或者 KeyOn 事件丢失)
            bool active = _keyOn[ch] || vol > 0;
            
            state.Channels[ch].KeyOn = active;
            state.Channels[ch].Volume = vol;
            state.Channels[ch].PanLeft = _volumeL[ch];
            state.Channels[ch].PanRight = _volumeR[ch];
            
            // Detune: 相对于原始速率 (0x80) 的采样率偏移
            // <0x80 = 慢速(低音), >0x80 = 快速(高音)
            state.Channels[ch].Detune = _delta[ch] - 0x80;
            
            // Delta -> Note: delta=0x80 为原始音高 (31250 * delta/256 Hz)
            if (active && _delta[ch] > 0)
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
            state.Channels[ch].Detune = _pitch[ch] - 0x100;
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
            int regCh = (reg - 0x40) % 4;  // 寄存器中的通道号 (0,1,2有效，3无效)
            if (regCh < 3)
            {
                int ch = regCh + chOffset;
                if (ch < 6)
                {
                    int slot = op == 0 ? 0 : op == 1 ? 2 : op == 2 ? 1 : 3;
                    _fmTl[ch, slot] = val & 0x7F;
                }
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
            state.Channels[ch].Detune = _fmFnum[ch] & 0xFF;
            
            if (_fmKeyOn[ch] && _fmFnum[ch] > 0)
            {
                // YM2608 (OPNA): freq = fnum * clock / (72 * 2^(21-block))
                double clock = Clock > 0 ? Clock : 7987200.0;
                double freq = _fmFnum[ch] * clock / (72.0 * Math.Pow(2, 21 - _fmBlock[ch]));
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
            state.Channels[ch + 6].Detune = _ssgPeriod[ch] & 0xFF;
            
            if (active)
            {
                // YM2608 SSG: freq = clock / (16 * period)
                // SSG 时钟为 FM 时钟的 1/4
                double clock = Clock > 0 ? Clock / 4.0 : 1996800.0;
                double freq = clock / (16.0 * _ssgPeriod[ch]);
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
            state.Channels[15].Detune = _adpcmDelta - 0x49BA;
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

// YM2610/YM2610B (OPNB) 状态追踪器
// YM2610: 4 FM + 3 SSG + 6 ADPCM-A + 1 ADPCM-B = 14通道
// YM2610B: 6 FM + 3 SSG + 6 ADPCM-A + 1 ADPCM-B = 16通道
// 参考: MAME ymfm_adpcm.h, Neo Geo Dev Wiki, VGM Spec
// Port 0: SSG (0x00-0x0F) + ADPCM-B (0x10-0x1F) + FM ch1-3 (0x30-0xBF)
// Port 1: ADPCM-A (0x00-0x2D) + FM ch4-6 (0x30-0xBF)
// Clock bit31: 0=YM2610(4FM), 1=YM2610B(6FM)
public class YM2610Tracker : VgmChipTracker
{
    // FM 部分 (最多6通道，YM2610使用4个，YM2610B使用6个)
    private readonly int[] _fmFnum = new int[6];
    private readonly int[] _fmBlock = new int[6];
    private readonly int[,] _fmTl = new int[6, 4];
    private readonly int[] _fmAlgo = new int[6];
    private readonly int[] _fmLr = new int[6];
    private readonly int[] _fmKeyOnMask = new int[6];  // FM KeyOn operator mask (bit4-7)
    private bool _isYm2610B;  // 是否为YM2610B模式 (6 FM通道)
    
    // SSG 部分 (3通道)
    private readonly int[] _ssgPeriod = new int[3];
    private readonly int[] _ssgVolume = new int[3];
    private readonly bool[] _ssgEnable = new bool[3];
    
    // ADPCM-A (6通道) - 固定采样率 18.5kHz (8MHz/12/6/6)
    // 寄存器在 Port 1: 0x00-0x2D
    // 音量处理: MAME使用XOR反转，0=最大衰减，0x1F/0x3F=最小衰减
    private readonly int[] _adpcmAIL = new int[6];         // 通道音量IL (反转后: 0=静音, 0x1F=最大)
    private readonly int[] _adpcmAPan = new int[6];        // 声道选择 (0x08-0x0D bit7-6)
    private readonly int[] _adpcmAStartAddr = new int[6];  // 采样起始地址 (0x10-0x15, 0x18-0x1D)
    private readonly int[] _adpcmAEndAddr = new int[6];    // 采样结束地址 (0x20-0x25, 0x28-0x2D)
    private readonly bool[] _adpcmAKeyOn = new bool[6];    // 通道Key On状态
    private int _adpcmATL;                                 // 总音量TL (反转后: 0=静音, 0x3F=最大)
    
    // ADPCM-B (1通道) - 可变采样率 1.85kHz~55.5kHz
    // 寄存器在 Port 0: 0x10-0x1F (注意：不是Port 1!)
    private int _adpcmBDelta;          // Delta-N 采样率 (0x19-0x1A)
    private int _adpcmBVolume;         // 音量 (0x1B)
    private bool _adpcmBKeyOn;         // 播放状态
    private bool _adpcmBRepeat;        // 循环播放
    private int _adpcmBPan;            // 声道选择 (0x11 bit7-6)
    private int _adpcmBStartAddr;      // 起始地址 (0x12-0x13)
    private int _adpcmBEndAddr;        // 结束地址 (0x14-0x15)
    
    private static readonly int[] CarrierMask = { 0x08, 0x08, 0x08, 0x08, 0x0A, 0x0E, 0x0E, 0x0F };
    
    // 重写Clock属性来检测YM2610B模式
    public new uint Clock
    {
        get => base.Clock;
        set
        {
            // bit31=1表示YM2610B模式 (6 FM通道)
            _isYm2610B = (value & 0x80000000) != 0;
            base.Clock = value & 0x7FFFFFFF;  // 去掉标志位
        }
    }
    
    // FM通道数: YM2610=4, YM2610B=6
    private int FmChannelCount => _isYm2610B ? 6 : 4;
    
    public override void ProcessEvent(VgmEvent evt)
    {
        byte reg = evt.Register;
        byte val = evt.Value;
        int port = evt.Port;
        
        // YM2610/YM2610B 寄存器布局:
        // Port 0 (0x58): SSG (0x00-0x0F), ADPCM-B (0x10-0x1F), FM共享 (0x20-0x2F), FM ch1-3 (0x30-0xBF)
        // Port 1 (0x59): ADPCM-A (0x00-0x2D), FM ch4-6 (0x30-0xBF)
        // YM2610: 只使用 ch1,2 + ch4,5 (共4个FM通道)
        // YM2610B: 使用全部6个FM通道
        
        if (port == 0)
        {
            // SSG (0x00-0x0F) - AY-3-8910 兼容
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
            // ADPCM-B (0x10-0x1F) - 在 Port 0!
            else if (reg == 0x10)
            {
                // bit 7: start, bit 4: repeat, bit 0: reset
                _adpcmBRepeat = (val & 0x10) != 0;
                if ((val & 0x01) != 0)
                    _adpcmBKeyOn = false;  // Reset: 停止
                else if ((val & 0x80) != 0)
                    _adpcmBKeyOn = true;   // Start: 开始播放
            }
            else if (reg == 0x11)
            {
                // bit 7: L, bit 6: R
                _adpcmBPan = (val >> 6) & 0x03;
            }
            else if (reg == 0x12)
            {
                _adpcmBStartAddr = (_adpcmBStartAddr & 0xFF00) | val;
            }
            else if (reg == 0x13)
            {
                _adpcmBStartAddr = (_adpcmBStartAddr & 0x00FF) | (val << 8);
            }
            else if (reg == 0x14)
            {
                _adpcmBEndAddr = (_adpcmBEndAddr & 0xFF00) | val;
            }
            else if (reg == 0x15)
            {
                _adpcmBEndAddr = (_adpcmBEndAddr & 0x00FF) | (val << 8);
            }
            else if (reg == 0x19)
            {
                _adpcmBDelta = (_adpcmBDelta & 0xFF00) | val;
            }
            else if (reg == 0x1A)
            {
                _adpcmBDelta = (_adpcmBDelta & 0x00FF) | (val << 8);
            }
            else if (reg == 0x1B)
            {
                _adpcmBVolume = val;
            }
            // FM Key On (0x28) - 在 Port 0
            else if (reg == 0x28)
            {
                // bit 0-1: channel (0-2), bit 2: port (0=ch1-3, 1=ch4-6)
                // bit 4-7: operator key on mask (OP1,OP3,OP2,OP4)
                // YM2610: 只用 ch0,1 + ch4,5 (跳过ch2,3)
                // YM2610B: 使用全部 ch0-5
                int rawCh = val & 0x03;
                int portBit = (val >> 2) & 0x01;
                int opMask = val & 0xF0;
                
                if (_isYm2610B)
                {
                    // YM2610B: 全部6个通道
                    int ch = rawCh + portBit * 3;
                    if (ch < 6) _fmKeyOnMask[ch] = opMask;
                }
                else
                {
                    // YM2610: 只有4个通道 (ch0,1 映射到内部0,1; ch4,5 映射到内部2,3)
                    if (rawCh < 2)
                    {
                        int mappedCh = rawCh + portBit * 2;
                        if (mappedCh < 4) _fmKeyOnMask[mappedCh] = opMask;
                    }
                }
            }
            // FM ch1-3 频率寄存器 (Port 0)
            else if (reg >= 0xA0 && reg <= 0xA2)
            {
                int ch = reg - 0xA0;
                // 动态检测YM2610B：如果访问了ch2寄存器，说明是6 FM通道模式
                if (ch == 2 && !_isYm2610B) _isYm2610B = true;
                if (ch < 3) _fmFnum[ch] = (_fmFnum[ch] & 0x700) | val;
            }
            else if (reg >= 0xA4 && reg <= 0xA6)
            {
                int ch = reg - 0xA4;
                if (ch == 2 && !_isYm2610B) _isYm2610B = true;
                if (ch < 3)
                {
                    _fmFnum[ch] = (_fmFnum[ch] & 0xFF) | ((val & 0x07) << 8);
                    _fmBlock[ch] = (val >> 3) & 0x07;
                }
            }
            else if (reg >= 0xB0 && reg <= 0xB2)
            {
                int ch = reg - 0xB0;
                if (ch == 2 && !_isYm2610B) _isYm2610B = true;
                if (ch < 3) _fmAlgo[ch] = val & 0x07;
            }
            else if (reg >= 0xB4 && reg <= 0xB6)
            {
                int ch = reg - 0xB4;
                if (ch == 2 && !_isYm2610B) _isYm2610B = true;
                if (ch < 3) _fmLr[ch] = (val >> 6) & 0x03;
            }
            else if (reg >= 0x40 && reg <= 0x4F)
            {
                int op = (reg - 0x40) / 4;
                int ch = (reg - 0x40) % 4;
                if (ch == 2 && !_isYm2610B) _isYm2610B = true;
                if (ch < 3)
                {
                    int slot = op == 0 ? 0 : op == 1 ? 2 : op == 2 ? 1 : 3;
                    _fmTl[ch, slot] = val & 0x7F;
                }
            }
        }
        else if (port == 1)
        {
            // ADPCM-A (0x00-0x2D) - 在 Port 1
            if (reg == 0x00)
            {
                // bit 7: DM (dump/mute), bit 5-0: 通道选择
                // 参考MAME: DM=0时KeyOn, DM=1时KeyOff
                bool isKeyOff = (val & 0x80) != 0;
                for (int ch = 0; ch < 6; ch++)
                {
                    if (((val >> ch) & 1) != 0)
                    {
                        _adpcmAKeyOn[ch] = !isKeyOff;
                    }
                }
            }
            else if (reg == 0x01)
            {
                // Total Level: MAME使用XOR 0x3F反转
                // 寄存器值0=最大衰减(静音), 0x3F=最小衰减(最大音量)
                // 反转后: 0=静音, 0x3F=最大音量
                _adpcmATL = (val & 0x3F) ^ 0x3F;
            }
            else if (reg >= 0x08 && reg <= 0x0D)
            {
                int ch = reg - 0x08;
                // Individual Level: MAME使用XOR 0x1F反转
                // 寄存器值0=最大衰减(静音), 0x1F=最小衰减(最大音量)
                // 反转后: 0=静音, 0x1F=最大音量
                _adpcmAIL[ch] = (val & 0x1F) ^ 0x1F;
                // Pan: bit 7=L, bit 6=R
                _adpcmAPan[ch] = (val >> 6) & 0x03;
            }
            // ADPCM-A Start Address
            else if (reg >= 0x10 && reg <= 0x15)
            {
                int ch = reg - 0x10;
                _adpcmAStartAddr[ch] = (_adpcmAStartAddr[ch] & 0xFF00) | val;
            }
            else if (reg >= 0x18 && reg <= 0x1D)
            {
                int ch = reg - 0x18;
                _adpcmAStartAddr[ch] = (_adpcmAStartAddr[ch] & 0x00FF) | (val << 8);
            }
            // ADPCM-A End Address
            else if (reg >= 0x20 && reg <= 0x25)
            {
                int ch = reg - 0x20;
                _adpcmAEndAddr[ch] = (_adpcmAEndAddr[ch] & 0xFF00) | val;
            }
            else if (reg >= 0x28 && reg <= 0x2D)
            {
                int ch = reg - 0x28;
                _adpcmAEndAddr[ch] = (_adpcmAEndAddr[ch] & 0x00FF) | (val << 8);
            }
            // FM ch4-6 频率寄存器 (Port 1)
            // YM2610: ch0,1 映射到内部索引 2,3 (FM4,FM5)
            // YM2610B: ch0,1,2 映射到内部索引 3,4,5 (FM4,FM5,FM6)
            else if (reg >= 0xA0 && reg <= 0xA2)
            {
                int ch = reg - 0xA0;
                // 动态检测YM2610B：如果访问了ch2寄存器（FM6），说明是6 FM通道模式
                if (ch == 2 && !_isYm2610B) _isYm2610B = true;
                if (ch < 3)
                {
                    int fmCh = _isYm2610B ? ch + 3 : ch + 2;
                    _fmFnum[fmCh] = (_fmFnum[fmCh] & 0x700) | val;
                }
            }
            else if (reg >= 0xA4 && reg <= 0xA6)
            {
                int ch = reg - 0xA4;
                if (ch == 2 && !_isYm2610B) _isYm2610B = true;
                if (ch < 3)
                {
                    int fmCh = _isYm2610B ? ch + 3 : ch + 2;
                    _fmFnum[fmCh] = (_fmFnum[fmCh] & 0xFF) | ((val & 0x07) << 8);
                    _fmBlock[fmCh] = (val >> 3) & 0x07;
                }
            }
            else if (reg >= 0xB0 && reg <= 0xB2)
            {
                int ch = reg - 0xB0;
                if (ch == 2 && !_isYm2610B) _isYm2610B = true;
                if (ch < 3)
                {
                    int fmCh = _isYm2610B ? ch + 3 : ch + 2;
                    _fmAlgo[fmCh] = val & 0x07;
                }
            }
            else if (reg >= 0xB4 && reg <= 0xB6)
            {
                int ch = reg - 0xB4;
                if (ch == 2 && !_isYm2610B) _isYm2610B = true;
                if (ch < 3)
                {
                    int fmCh = _isYm2610B ? ch + 3 : ch + 2;
                    _fmLr[fmCh] = (val >> 6) & 0x03;
                }
            }
            else if (reg >= 0x40 && reg <= 0x4F)
            {
                int op = (reg - 0x40) / 4;
                int ch = (reg - 0x40) % 4;
                if (ch == 2 && !_isYm2610B) _isYm2610B = true;
                if (ch < 3)
                {
                    int fmCh = _isYm2610B ? ch + 3 : ch + 2;
                    int slot = op == 0 ? 0 : op == 1 ? 2 : op == 2 ? 1 : 3;
                    _fmTl[fmCh, slot] = val & 0x7F;
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
        Array.Clear(_fmKeyOnMask);
        Array.Clear(_ssgPeriod);
        Array.Clear(_ssgVolume);
        Array.Clear(_ssgEnable);
        Array.Clear(_adpcmAIL);
        Array.Clear(_adpcmAPan);
        Array.Clear(_adpcmAStartAddr);
        Array.Clear(_adpcmAEndAddr);
        Array.Clear(_adpcmAKeyOn);
        _adpcmATL = 0;
        _adpcmBDelta = 0;
        _adpcmBVolume = 0;
        _adpcmBKeyOn = false;
        _adpcmBRepeat = false;
        _adpcmBPan = 0;
        _adpcmBStartAddr = 0;
        _adpcmBEndAddr = 0;
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        // 通道布局:
        // YM2610: 4 FM (0-3) + 3 SSG (4-6) + 6 ADPCM-A (7-12) + 1 ADPCM-B (13) = 14通道
        // YM2610B: 6 FM (0-5) + 3 SSG (6-8) + 6 ADPCM-A (9-14) + 1 ADPCM-B (15) = 16通道
        int fmCount = FmChannelCount;
        int ssgBase = fmCount;            // SSG起始索引: YM2610=4, YM2610B=6
        int adpcmABase = ssgBase + 3;     // ADPCM-A起始索引: YM2610=7, YM2610B=9
        int adpcmBIdx = adpcmABase + 6;   // ADPCM-B索引: YM2610=13, YM2610B=15
        
        // FM 通道
        for (int ch = 0; ch < fmCount && ch < state.Channels.Length; ch++)
        {
            // 检查是否有任何operator开启
            bool keyOn = _fmKeyOnMask[ch] != 0;
            state.Channels[ch].KeyOn = keyOn;
            
            // 根据algorithm计算carrier的TL（最小值 = 最大音量）
            int algoMask = CarrierMask[_fmAlgo[ch]];
            int minTl = 127;
            for (int op = 0; op < 4; op++)
            {
                // 只计算carrier的TL
                if ((algoMask & (1 << op)) != 0)
                    minTl = Math.Min(minTl, _fmTl[ch, op]);
            }
            int vol = Math.Max(0, 127 - minTl);
            state.Channels[ch].Volume = keyOn ? vol : 0;
            
            // Pan处理：如果LR=0则默认输出到两边
            int lr = _fmLr[ch];
            if (lr == 0) lr = 3;  // 默认立体声
            state.Channels[ch].PanLeft = (lr & 0x02) != 0 ? vol : 0;
            state.Channels[ch].PanRight = (lr & 0x01) != 0 ? vol : 0;
            state.Channels[ch].Detune = _fmFnum[ch] & 0xFF;
            
            if (keyOn && _fmFnum[ch] > 0)
            {
                // YM2610/B: freq = fnum * clock / (72 * 2^(21-block))
                double clock = Clock > 0 ? Clock : 8000000.0;
                double freq = _fmFnum[ch] * clock / (72.0 * Math.Pow(2, 21 - _fmBlock[ch]));
                state.Channels[ch].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[ch].Note = -1;
            }
        }
        
        // SSG 通道
        for (int ch = 0; ch < 3 && ssgBase + ch < state.Channels.Length; ch++)
        {
            int idx = ssgBase + ch;
            bool active = _ssgEnable[ch] && _ssgVolume[ch] > 0 && _ssgPeriod[ch] > 0;
            int vol = _ssgVolume[ch] * 127 / 15;
            state.Channels[idx].KeyOn = active;
            state.Channels[idx].Volume = vol;
            state.Channels[idx].PanLeft = vol;
            state.Channels[idx].PanRight = vol;
            state.Channels[idx].Detune = _ssgPeriod[ch] & 0xFF;
            
            if (active)
            {
                // SSG: freq = clock / (16 * period), SSG时钟为FM时钟的1/4
                double clock = Clock > 0 ? Clock / 4.0 : 2000000.0;
                double freq = clock / (16.0 * _ssgPeriod[ch]);
                state.Channels[idx].Note = VgmVisualizer.FrequencyToNote(freq);
            }
            else
            {
                state.Channels[idx].Note = -1;
            }
        }
        
        // ADPCM-A 通道 - 固定采样率 18.5kHz
        for (int ch = 0; ch < 6 && adpcmABase + ch < state.Channels.Length; ch++)
        {
            int idx = adpcmABase + ch;
            bool active = _adpcmAKeyOn[ch];
            
            // 音量计算: 使用反转后的TL和IL值
            // TL和IL都已经反转: 0=静音, 最大值=最大音量
            // 总音量 = TL + IL, 限制在0-63范围内
            int totalVol = _adpcmATL + _adpcmAIL[ch];
            // 映射到0-127范围
            int vol = Math.Min(127, totalVol * 127 / 63);
            
            state.Channels[idx].KeyOn = active;
            state.Channels[idx].Volume = active ? vol : 0;
            
            // Pan: bit 1=L, bit 0=R (如果pan=0则默认输出到两边)
            int pan = _adpcmAPan[ch];
            if (pan == 0) pan = 3;  // 默认立体声输出
            state.Channels[idx].PanLeft = (pan & 0x02) != 0 ? vol : 0;
            state.Channels[idx].PanRight = (pan & 0x01) != 0 ? vol : 0;
            
            // 采样地址映射到音高显示
            // ADPCM-A使用起始地址区分不同采样
            // 地址格式: (高字节 << 8) | 低字节，以256字节为单位
            if (active && _adpcmAStartAddr[ch] > 0)
            {
                // 使用完整16位地址作为采样标识
                int sampleAddr = _adpcmAStartAddr[ch] & 0xFFFF;
                // 使用简单的哈希确保不同地址映射到不同音符
                // 高字节决定主要音高区域，低字节微调
                int highByte = (sampleAddr >> 8) & 0xFF;
                int lowByte = sampleAddr & 0xFF;
                // 使用高字节和低字节的组合，确保唯一性
                int noteOffset = (highByte * 7 + lowByte) % 96;  // 使用质数7减少冲突
                state.Channels[idx].Note = 24 + noteOffset;  // C1-B8范围
                state.Channels[idx].Detune = sampleAddr;  // 显示完整地址
            }
            else
            {
                state.Channels[idx].Note = -1;
                state.Channels[idx].Detune = 0;
            }
        }
        
        // ADPCM-B 通道 - 可变采样率
        if (adpcmBIdx < state.Channels.Length)
        {
            int vol = _adpcmBVolume / 2;
            state.Channels[adpcmBIdx].KeyOn = _adpcmBKeyOn;
            state.Channels[adpcmBIdx].Volume = vol;
            state.Channels[adpcmBIdx].PanLeft = (_adpcmBPan & 0x02) != 0 ? vol : 0;
            state.Channels[adpcmBIdx].PanRight = (_adpcmBPan & 0x01) != 0 ? vol : 0;
            state.Channels[adpcmBIdx].Detune = _adpcmBDelta - 0x49BA;
            
            if (_adpcmBKeyOn && _adpcmBDelta > 0)
            {
                // ADPCM-B: delta=0x49BA (18874) 约等于 8kHz
                double ratio = _adpcmBDelta / 18874.0;
                state.Channels[adpcmBIdx].Note = VgmVisualizer.PcmRatioToNote(ratio, 60);
            }
            else
            {
                state.Channels[adpcmBIdx].Note = -1;
            }
        }
    }
}

// 通用 PCM 追踪器（用于固定采样率 PCM 芯片如 PWM, uPD7759, VSU, ES5503, ES5506）
// 这些芯片播放预录采样，采样率固定，无音高控制
public class GenericPcmTracker : VgmChipTracker
{
    private readonly bool[] _keyOn = new bool[32];
    private readonly int[] _volume = new int[32];
    private readonly int[] _sampleNum = new int[32];  // 采样号
    
    public override void ProcessEvent(VgmEvent evt)
    {
        // 通用处理：根据寄存器写入推断通道状态
        int ch = evt.Register % 32;
        if (ch < _keyOn.Length)
        {
            // 非零值表示播放，值本身可能是采样号或音量
            if (evt.Value > 0)
            {
                _keyOn[ch] = true;
                _volume[ch] = Math.Min(127, (int)evt.Value);
                _sampleNum[ch] = evt.Value;  // 使用值作为采样号
            }
            else
            {
                _keyOn[ch] = false;
            }
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_keyOn);
        Array.Clear(_volume);
        Array.Clear(_sampleNum);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int i = 0; i < state.Channels.Length && i < _keyOn.Length; i++)
        {
            state.Channels[i].KeyOn = _keyOn[i];
            state.Channels[i].Volume = _volume[i];
            state.Channels[i].PanLeft = _volume[i] / 2;
            state.Channels[i].PanRight = _volume[i] / 2;
            // 固定采样率 PCM：采样号映射到音高显示
            // 采样号映射到 MIDI note 36-96 范围
            if (_keyOn[i])
            {
                state.Channels[i].Note = 36 + (_sampleNum[i] % 61);
            }
            else
            {
                state.Channels[i].Note = -1;
            }
        }
    }
}

// 通用 FM 追踪器（用于 YMF271 等）
public class GenericFmTracker : VgmChipTracker
{
    private readonly bool[] _keyOn = new bool[12];
    private readonly int[] _volume = new int[12];
    private readonly int[] _fnum = new int[12];
    
    public override void ProcessEvent(VgmEvent evt)
    {
        int ch = evt.Register % 12;
        if (ch < _keyOn.Length)
        {
            // 简单处理：检测 Key On/Off
            if ((evt.Register & 0xF0) == 0x20)
            {
                _keyOn[ch] = (evt.Value & 0x80) != 0;
            }
            else if ((evt.Register & 0xF0) == 0x40)
            {
                _volume[ch] = 127 - (evt.Value & 0x7F);
            }
        }
    }
    
    public override void Reset()
    {
        Array.Clear(_keyOn);
        Array.Clear(_volume);
        Array.Clear(_fnum);
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        for (int i = 0; i < state.Channels.Length && i < _keyOn.Length; i++)
        {
            state.Channels[i].KeyOn = _keyOn[i];
            state.Channels[i].Volume = _volume[i];
            state.Channels[i].PanLeft = _volume[i] / 2;
            state.Channels[i].PanRight = _volume[i] / 2;
            state.Channels[i].Note = -1;
        }
    }
}

// OKIM6258 ADPCM 追踪器
// 单通道 4-bit ADPCM 编解码器，无可变频率，采样率由芯片时钟决定
// 采样率 = clock / divider (divider = 1024, 768, 或 512)
public class OKIM6258Tracker : VgmChipTracker
{
    private bool _playing;
    private int _volume = 127;
    private int _dataCount;  // 数据写入计数，用于判断活动状态
    
    public override void ProcessEvent(VgmEvent evt)
    {
        // OKIM6258 命令格式
        switch (evt.Register)
        {
            case 0x00: // 控制寄存器
                // bit 0: 录音/播放选择
                // bit 1: 播放启动
                // bit 2: 录音启动
                if ((evt.Value & 0x02) != 0)
                {
                    _playing = true;
                    _dataCount = 0;
                }
                else if ((evt.Value & 0x01) == 0)
                {
                    _playing = false;
                }
                break;
            case 0x01: // 数据写入
                if (_playing && evt.Value != 0)
                {
                    _dataCount++;
                }
                break;
        }
    }
    
    public override void Reset()
    {
        _playing = false;
        _volume = 127;
        _dataCount = 0;
    }
    
    public override void UpdateVisualizerState(VgmVisualizer.ChipState state)
    {
        if (state.Channels.Length > 0)
        {
            state.Channels[0].KeyOn = _playing;
            state.Channels[0].Volume = _playing ? _volume : 0;
            state.Channels[0].PanLeft = _volume / 2;
            state.Channels[0].PanRight = _volume / 2;
            // OKIM6258 是固定采样率的 ADPCM 编解码器，显示固定音高 C4
            state.Channels[0].Note = _playing ? 60 : -1;
        }
    }
}
