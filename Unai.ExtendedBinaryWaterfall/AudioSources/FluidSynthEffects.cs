namespace Unai.ExtendedBinaryWaterfall;

// FluidSynth 效果设置
// 包含 Reverb（混响）和 Chorus（合唱）效果的所有可配置参数
public class FluidSynthEffects
{
    // ===== Reverb（混响）效果 =====
    
    // 混响开关
    public bool ReverbEnabled { get; set; } = true;
    
    // 房间大小 (0.0-1.0)，值越大混响尾越长
    public double ReverbRoomSize { get; set; } = 0.2;
    
    // 阻尼 (0.0-1.0)，值越大高频衰减越快
    public double ReverbDamp { get; set; } = 0.0;
    
    // 输出振幅 (0.0-1.0)，混响的输出音量
    public double ReverbLevel { get; set; } = 0.9;
    
    // 立体声宽度 (0.0-100.0)，0=单声道，1=正常立体声
    public double ReverbWidth { get; set; } = 0.5;
    
    // ===== Chorus（合唱）效果 =====
    
    // 合唱开关
    public bool ChorusEnabled { get; set; } = false;
    
    // 声部数量 (0-99)，更多声部=更丰富的合唱效果
    public int ChorusNr { get; set; } = 3;
    
    // 输出振幅 (0.0-10.0)，合唱的输出音量
    public double ChorusLevel { get; set; } = 2.0;
    
    // 调制速度 Hz (0.1-5.0)，LFO 频率
    public double ChorusSpeed { get; set; } = 0.3;
    
    // 调制深度 (0.0-256.0)，调制幅度
    public double ChorusDepth { get; set; } = 8.0;
    
    // ===== 主增益 =====
    
    // 主输出增益 (0.0-2.0)
    public double Gain { get; set; } = 0.6;
    
    // 创建默认设置
    public static FluidSynthEffects Default => new();
    
    // 复制当前设置
    public FluidSynthEffects Clone()
    {
        return new FluidSynthEffects
        {
            ReverbEnabled = this.ReverbEnabled,
            ReverbRoomSize = this.ReverbRoomSize,
            ReverbDamp = this.ReverbDamp,
            ReverbLevel = this.ReverbLevel,
            ReverbWidth = this.ReverbWidth,
            ChorusEnabled = this.ChorusEnabled,
            ChorusNr = this.ChorusNr,
            ChorusLevel = this.ChorusLevel,
            ChorusSpeed = this.ChorusSpeed,
            ChorusDepth = this.ChorusDepth,
            Gain = this.Gain
        };
    }
}
