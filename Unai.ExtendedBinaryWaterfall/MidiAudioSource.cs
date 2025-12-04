using System;
using System.IO;
using System.Runtime.InteropServices;
using CSCore;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace Unai.ExtendedBinaryWaterfall;

// MIDI 音频源：使用 FluidSynth 将 MIDI 文件渲染为 PCM 音频
// 实现 ISampleSource 接口，可与现有的 MultiFileAudioSource 无缝集成
public class MidiAudioSource : ISampleSource
{
    // FluidSynth P/Invoke 绑定
    private static class FluidSynth
    {
        private const string LibName = "libfluidsynth-3";

        // Settings API
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr new_fluid_settings();

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void delete_fluid_settings(IntPtr settings);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int fluid_settings_setnum(IntPtr settings, 
            [MarshalAs(UnmanagedType.LPStr)] string name, double val);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int fluid_settings_setint(IntPtr settings, 
            [MarshalAs(UnmanagedType.LPStr)] string name, int val);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int fluid_settings_setstr(IntPtr settings, 
            [MarshalAs(UnmanagedType.LPStr)] string name, 
            [MarshalAs(UnmanagedType.LPStr)] string val);

        // Synth API
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr new_fluid_synth(IntPtr settings);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void delete_fluid_synth(IntPtr synth);

        // 使用 LPUTF8Str 支持非 ASCII 路径（中文、日文等）
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int fluid_synth_sfload(IntPtr synth, 
            [MarshalAs(UnmanagedType.LPUTF8Str)] string filename, int reset_presets);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int fluid_synth_write_float(IntPtr synth, int len,
            [Out] float[] lout, int loff, int lincr,
            [Out] float[] rout, int roff, int rincr);

        // Player API
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr new_fluid_player(IntPtr synth);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void delete_fluid_player(IntPtr player);

        // 使用 LPUTF8Str 支持非 ASCII 路径（中文、日文等）
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int fluid_player_add(IntPtr player, 
            [MarshalAs(UnmanagedType.LPUTF8Str)] string midifile);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int fluid_player_play(IntPtr player);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int fluid_player_stop(IntPtr player);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int fluid_player_join(IntPtr player);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int fluid_player_get_status(IntPtr player);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int fluid_player_get_total_ticks(IntPtr player);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int fluid_player_get_current_tick(IntPtr player);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int fluid_player_seek(IntPtr player, int ticks);

        // 合成器诊断 API
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int fluid_synth_get_active_voice_count(IntPtr synth);

        // 播放器状态常量
        public const int FLUID_PLAYER_READY = 0;
        public const int FLUID_PLAYER_PLAYING = 1;
        public const int FLUID_PLAYER_STOPPING = 2;
        public const int FLUID_PLAYER_DONE = 3;

        public const int FLUID_OK = 0;
        public const int FLUID_FAILED = -1;
    }

    private WaveFormat _waveFormat;
    private int _sampleRate;
    private int _channels;
    private long _position;
    private bool _isDisposed;

    // 预渲染的音频缓冲区
    private float[] _audioBuffer;
    private long _length;

    // MIDI 文件路径（用于日志）
    private string _midiFilePath;
    
    // 效果设置
    private FluidSynthEffects _effects;

    public MidiAudioSource(string midiFilePath, string soundFontPath, 
        int targetSampleRate = 48000, int targetChannels = 2, FluidSynthEffects effects = null)
    {
        _midiFilePath = midiFilePath;
        _sampleRate = targetSampleRate;
        _channels = targetChannels;
        _effects = effects ?? FluidSynthEffects.Default;
        _waveFormat = new WaveFormat(_sampleRate, 32, _channels, AudioEncoding.IeeeFloat);

        // 使用 DryWetMidi 获取准确的 MIDI 时长
        double durationSeconds = 60.0;
        try
        {
            var midiFile = MidiFile.Read(midiFilePath);
            var duration = midiFile.GetDuration<MetricTimeSpan>();
            durationSeconds = duration.TotalSeconds;
            if (durationSeconds <= 0) durationSeconds = 60.0;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[MidiAudioSource] 无法读取 MIDI 时长: {ex.Message}");
        }

        // 预渲染整个 MIDI 到内存缓冲区
        _audioBuffer = PreRenderMidi(midiFilePath, soundFontPath, durationSeconds);
        _length = _audioBuffer.Length;
        
        Logger.Info($"[MidiAudioSource] 已加载 MIDI: {Path.GetFileName(midiFilePath)}, " +
                   $"时长: {TimeSpan.FromSeconds(durationSeconds):mm\\:ss}");
    }

    // 预渲染整个 MIDI 文件到内存
    private float[] PreRenderMidi(string midiFilePath, string soundFontPath, double durationSeconds)
    {
        IntPtr settings = IntPtr.Zero;
        IntPtr synth = IntPtr.Zero;
        IntPtr player = IntPtr.Zero;

        try
        {
            // 创建 FluidSynth 设置
            settings = FluidSynth.new_fluid_settings();
            if (settings == IntPtr.Zero)
                throw new InvalidOperationException("无法创建 FluidSynth 设置");

            // 配置合成器参数
            FluidSynth.fluid_settings_setnum(settings, "synth.sample-rate", _sampleRate);
            FluidSynth.fluid_settings_setnum(settings, "synth.gain", _effects.Gain);
            
            // 混响效果设置
            FluidSynth.fluid_settings_setint(settings, "synth.reverb.active", _effects.ReverbEnabled ? 1 : 0);
            FluidSynth.fluid_settings_setnum(settings, "synth.reverb.room-size", _effects.ReverbRoomSize);
            FluidSynth.fluid_settings_setnum(settings, "synth.reverb.damp", _effects.ReverbDamp);
            FluidSynth.fluid_settings_setnum(settings, "synth.reverb.level", _effects.ReverbLevel);
            FluidSynth.fluid_settings_setnum(settings, "synth.reverb.width", _effects.ReverbWidth);
            
            // 合唱效果设置
            FluidSynth.fluid_settings_setint(settings, "synth.chorus.active", _effects.ChorusEnabled ? 1 : 0);
            FluidSynth.fluid_settings_setint(settings, "synth.chorus.nr", _effects.ChorusNr);
            FluidSynth.fluid_settings_setnum(settings, "synth.chorus.level", _effects.ChorusLevel);
            FluidSynth.fluid_settings_setnum(settings, "synth.chorus.speed", _effects.ChorusSpeed);
            FluidSynth.fluid_settings_setnum(settings, "synth.chorus.depth", _effects.ChorusDepth);
            
            // 使用采样时钟，确保离线渲染同步
            FluidSynth.fluid_settings_setstr(settings, "player.timing-source", "sample");

            // 创建合成器
            synth = FluidSynth.new_fluid_synth(settings);
            if (synth == IntPtr.Zero)
                throw new InvalidOperationException("无法创建 FluidSynth 合成器");

            // 加载音色库
            if (!string.IsNullOrEmpty(soundFontPath) && File.Exists(soundFontPath))
            {
                int sfId = FluidSynth.fluid_synth_sfload(synth, soundFontPath, 1);
                if (sfId >= 0)
                {
                    Logger.Info($"[MidiAudioSource] 已加载音色库: {Path.GetFileName(soundFontPath)}");
                }
                else
                {
                    Logger.Warning($"[MidiAudioSource] 音色库加载失败: {soundFontPath}");
                }
            }
            else
            {
                Logger.Warning($"[MidiAudioSource] 未找到音色库: {soundFontPath}");
            }

            // 创建 MIDI 播放器
            player = FluidSynth.new_fluid_player(synth);
            if (player == IntPtr.Zero)
                throw new InvalidOperationException("无法创建 FluidSynth 播放器");

            // 添加 MIDI 文件
            int addResult = FluidSynth.fluid_player_add(player, midiFilePath);
            if (addResult != FluidSynth.FLUID_OK)
            {
                Logger.Warning($"[MidiAudioSource] fluid_player_add 失败: {addResult}, 文件: {midiFilePath}");
                throw new InvalidOperationException($"无法加载 MIDI 文件: {midiFilePath}");
            }

            // 检查 MIDI 文件是否正确加载
            int totalTicks = FluidSynth.fluid_player_get_total_ticks(player);
            Logger.Debug($"[MidiAudioSource] MIDI 总 ticks: {totalTicks}");
            if (totalTicks <= 0)
            {
                Logger.Warning($"[MidiAudioSource] MIDI 文件似乎为空或加载失败 (totalTicks={totalTicks})");
            }

            // 开始播放
            int playResult = FluidSynth.fluid_player_play(player);
            if (playResult != FluidSynth.FLUID_OK)
            {
                Logger.Warning($"[MidiAudioSource] fluid_player_play 失败: {playResult}");
            }
            
            // 检查播放器状态
            int initialStatus = FluidSynth.fluid_player_get_status(player);
            Logger.Debug($"[MidiAudioSource] 播放器初始状态: {initialStatus} (1=PLAYING, 3=DONE)");

            // 计算所需的缓冲区大小（采样数 * 声道数）
            int totalSamples = (int)(durationSeconds * _sampleRate);
            int bufferSize = totalSamples * _channels;
            float[] result = new float[bufferSize];

            // 临时渲染缓冲区
            const int blockSize = 8192;
            float[] leftBuf = new float[blockSize];
            float[] rightBuf = new float[blockSize];
            int position = 0;
            int iterationCount = 0;

            // 渲染整个 MIDI
            // 注意：使用 "sample" 时钟源时，必须先调用 fluid_synth_write_float 来驱动播放器
            // 然后再检查状态，否则播放器可能立即返回 DONE
            while (position < bufferSize)
            {
                int samplesToRender = Math.Min(blockSize, (bufferSize - position) / _channels);
                if (samplesToRender <= 0) break;

                // 先渲染采样，这会驱动播放器前进
                int writeResult = FluidSynth.fluid_synth_write_float(synth, samplesToRender, leftBuf, 0, 1, rightBuf, 0, 1);
                if (writeResult != FluidSynth.FLUID_OK)
                {
                    Logger.Warning($"[MidiAudioSource] fluid_synth_write_float 失败，返回值: {writeResult}");
                    break;
                }

                // 交错存储到结果缓冲区
                if (_channels >= 2)
                {
                    for (int i = 0; i < samplesToRender; i++)
                    {
                        result[position++] = leftBuf[i];
                        result[position++] = rightBuf[i];
                    }
                }
                else
                {
                    Array.Copy(leftBuf, 0, result, position, samplesToRender);
                    position += samplesToRender;
                }
                
                iterationCount++;

                // 渲染后检查播放器状态
                int status = FluidSynth.fluid_player_get_status(player);
                if (status == FluidSynth.FLUID_PLAYER_DONE)
                {
                    Logger.Debug($"[MidiAudioSource] 播放器完成: 迭代={iterationCount}, 位置={position}/{bufferSize}");
                    break;
                }
            }
            
            Logger.Debug($"[MidiAudioSource] 渲染完成: 迭代={iterationCount}, 采样={position}");

            return result;
        }
        finally
        {
            // 清理资源
            if (player != IntPtr.Zero)
            {
                FluidSynth.fluid_player_stop(player);
                FluidSynth.fluid_player_join(player);
                FluidSynth.delete_fluid_player(player);
            }
            if (synth != IntPtr.Zero)
                FluidSynth.delete_fluid_synth(synth);
            if (settings != IntPtr.Zero)
                FluidSynth.delete_fluid_settings(settings);
        }
    }

    public bool CanSeek => true;

    public WaveFormat WaveFormat => _waveFormat;

    public long Position
    {
        get => _position;
        set
        {
            if (value < 0) value = 0;
            if (value > _length) value = _length;
            _position = value;
        }
    }

    public long Length => _length;

    // 是否已到达文件末尾
    public bool IsEof => _position >= _length;

    // 从预渲染缓冲区读取音频
    public int Read(float[] buffer, int offset, int count)
    {
        if (_isDisposed || _audioBuffer == null) return 0;
        if (_position >= _length) return 0;

        // 计算可读取的数量
        long remaining = _length - _position;
        int toRead = (int)Math.Min(count, remaining);

        // 从预渲染缓冲区复制数据
        Array.Copy(_audioBuffer, _position, buffer, offset, toRead);
        _position += toRead;

        return toRead;
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _audioBuffer = null;
        }
    }
}

// MIDI 音频 管理音色库路径和创建 MidiAudioSource
public static class MidiAudioSourceFactory
{
    // 默认音色库路径（由用户设置）
    public static string DefaultSoundFontPath { get; set; } = null;
    
    // 默认效果设置（由 GUI 设置）
    public static FluidSynthEffects DefaultEffects { get; set; } = new FluidSynthEffects();

    // 查找可用的音色库
    public static string FindSoundFont()
    {
        // 优先使用用户设置的路径
        if (!string.IsNullOrEmpty(DefaultSoundFontPath) && File.Exists(DefaultSoundFontPath))
            return DefaultSoundFontPath;

        // 在程序目录下查找
        string[] searchPaths = new[]
        {
            "soundfonts",
            ".",
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "soundfonts"),
            AppDomain.CurrentDomain.BaseDirectory
        };

        string[] sfExtensions = { "*.sf2", "*.sf3" };

        foreach (var searchPath in searchPaths)
        {
            if (!Directory.Exists(searchPath)) continue;

            foreach (var ext in sfExtensions)
            {
                var files = Directory.GetFiles(searchPath, ext);
                if (files.Length > 0)
                {
                    Logger.Info($"[MidiAudioSourceFactory] 找到音色库: {files[0]}");
                    return files[0];
                }
            }
        }

        return null;
    }

    // 检测文件是否为 MIDI 格式
    public static bool IsMidiFile(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext == ".mid" || ext == ".midi";
    }

    // 创建 MIDI 音频源（使用默认效果设置）
    public static MidiAudioSource Create(string midiPath, int sampleRate = 48000, int channels = 2)
    {
        return Create(midiPath, sampleRate, channels, DefaultEffects);
    }
    
    // 创建 MIDI 音频源（使用指定效果设置）
    public static MidiAudioSource Create(string midiPath, int sampleRate, int channels, FluidSynthEffects effects)
    {
        string sf2Path = FindSoundFont();
        if (string.IsNullOrEmpty(sf2Path))
        {
            throw new FileNotFoundException(
                "未找到音色库文件。");
        }

        return new MidiAudioSource(midiPath, sf2Path, sampleRate, channels, effects);
    }
}
