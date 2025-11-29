using System;
using System.Buffers.Binary;
using System.Linq;

namespace Unai.ExtendedBinaryWaterfall;

public class AudioBuffer
{
	float[][] Samples { get; set; }
	public int ChannelCount => Samples.Length;
	public int SampleCount => Samples[0].Length;
	public int TotalSampleCount => SampleCount * ChannelCount;

	public AudioBuffer(int sampleCount, int channelCount)
	{
		Clear(sampleCount, channelCount);
	}

	public AudioBuffer(AudioBuffer source)
	{
		Clear(source.SampleCount, source.ChannelCount);
		for (int ch = 0; ch < ChannelCount; ch++)
		{
			Array.Copy(source.Samples[ch], Samples[ch], SampleCount);
		}
	}

	public AudioBuffer Clear(int? sampleCount = null, int? channelCount = null)
	{
		sampleCount ??= SampleCount;
		channelCount ??= ChannelCount;

		Samples = new float[channelCount.Value][];
		for (int ch = 0; ch < channelCount; ch++)
		{
			Samples[ch] = new float[sampleCount.Value];
		}

		return this;
	}

	public AudioBuffer LoadFromByteArray(byte[] buffer, AudioSampleFormat sampleFormat = AudioSampleFormat.Unsigned8)
	{
		float[] floatPcmBuffer = new float[buffer.Length / sampleFormat.GetByteSize()];

		switch (sampleFormat)
		{
			case AudioSampleFormat.Unsigned8:
				floatPcmBuffer = buffer.Select(x => (x / 128f) - 1f).ToArray();
				break;

			case AudioSampleFormat.Signed8:
				floatPcmBuffer = buffer.Select(x => x / 128f).ToArray();
				break;

			case AudioSampleFormat.Unsigned16LE:
				for (int i = 0; i < floatPcmBuffer.Length; i++)
				{
					var srcIdx = i * 2;
					var srcSample = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(srcIdx, 2));
					floatPcmBuffer[i] = (srcSample / 32768f) - 1f;
				}
				break;

			case AudioSampleFormat.Signed16LE:
				for (int i = 0; i < floatPcmBuffer.Length; i++)
				{
					var srcIdx = i * 2;
					var srcSample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(srcIdx, 2));
					floatPcmBuffer[i] = srcSample / 32768f;
				}
				break;

			default:
				throw new InvalidOperationException("Audio sample format not implemented yet.");
		}

		for (int i = 0; i < floatPcmBuffer.Length; i++)
		{
			if (i / ChannelCount >= Samples[0].Length)
			{
				Logger.Warning($"PCM sample at index {i} does not fit inside sample buffer at index {i / ChannelCount}.");
				return this;
			}
			Samples[i % ChannelCount][i / ChannelCount] = floatPcmBuffer[i];
		}

		return this;
	}

	/// <summary>
	/// 从交织的 float PCM 数组加载数据（samples 形如 L0,R0,L1,R1,...），用于对接外部解码器。
	/// 数组长度不足时会保留尾部为 0；数组长度超出内部缓冲区时会截断并记录日志。
	/// </summary>
	/// <param name="buffer">交织格式的 PCM 浮点数组，范围通常在 [-1,1]</param>
	/// <param name="channelCount">交织通道数，必须与当前缓冲区通道数一致</param>
	public AudioBuffer LoadFromInterleavedFloats(float[] buffer, int channelCount)
	{
		// 通道数不匹配时直接抛异常，便于在接入阶段快速发现问题
		if (channelCount != ChannelCount)
		{
			throw new InvalidOperationException($"Channel mismatch: buffer={channelCount}ch, AudioBuffer={ChannelCount}ch");
		}

		int samplesPerChannel = buffer.Length / channelCount;
		int maxSamples = SampleCount;
		int totalSamples = Math.Min(samplesPerChannel, maxSamples);

		// 将交织数据写入内部按通道分离的数组
		for (int i = 0; i < totalSamples * channelCount; i++)
		{
			int ch = i % channelCount;
			int s = i / channelCount;
			Samples[ch][s] = buffer[i];
		}

		// 如果源数据长度不足一整帧，剩余部分清零，避免上帧残留
		if (totalSamples < maxSamples)
		{
			for (int ch = 0; ch < ChannelCount; ch++)
			{
				Array.Clear(Samples[ch], totalSamples, maxSamples - totalSamples);
			}
		}

		// 如果源数据太长，只简单截断并给出提示
		if (samplesPerChannel > maxSamples)
		{
			Logger.Warning($"AudioBuffer: source has {samplesPerChannel} samples/ch, truncated to {maxSamples}.");
		}

		return this;
	}

	public AudioBuffer Resample(int newSampleCount)
	{
		if (newSampleCount == SampleCount) return this;

		for (int ch = 0; ch < ChannelCount; ch++)
		{
			var newSampleBuffer = Samples[ch].LinearResample(newSampleCount);
			Samples[ch] = newSampleBuffer.ToArray();
		}

		return this;
	}

	public AudioBuffer RemixChannels(int newChannelCount)
	{
		if (newChannelCount == ChannelCount) return this;

		var newSamples = new float[newChannelCount][];

		for (int dch = 0; dch < newChannelCount; dch++)
		{
			var sch = (int)((dch / (float)newChannelCount) * ChannelCount);
			newSamples[dch] = new float[SampleCount];
			Array.Copy(newSamples[dch], Samples[sch], SampleCount);
		}

		Samples = newSamples;

		return this;
	}

	public float[] ToArray(bool planar = false)
	{
		float[] ret = new float[SampleCount * ChannelCount];

		if (planar)
		{
			throw new NotImplementedException();
		}

		for (int i = 0; i < ret.Length; i++)
		{
			ret[i] = Samples[i % ChannelCount][i / ChannelCount];
		}

		return ret;
	}
}