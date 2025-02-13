using System;

namespace Unai.ExtendedBinaryWaterfall;

public class AudioFrameResizer<T>
{
	private T[] _outputBuffer = null;
	private int _bufOfs = 0;
	
	public int BufferLength
	{
		get => _outputBuffer.Length;
		set => _outputBuffer = new T[value];
	}
	public Action<T[]> OutputCallback { get; set; } = null;

	public void Push(T[] input)
	{
		var newBufOfs = _bufOfs + input.Length;
		if (newBufOfs >= BufferLength)
		{
			var firstHalfSize = BufferLength - _bufOfs;
			var secondHalfSize = Math.Abs(BufferLength - newBufOfs);
			Array.Copy(input, 0, _outputBuffer, _bufOfs, firstHalfSize);
			
			OutputCallback?.Invoke(_outputBuffer);
			
			Array.Copy(input, firstHalfSize, _outputBuffer, 0, secondHalfSize);
			_bufOfs = secondHalfSize;

			if (_bufOfs > BufferLength)
			{
				Logger.Error($"buffer overrun {_bufOfs} > {BufferLength}");
				_bufOfs %= BufferLength;
			}
		}
		else
		{
			Array.Copy(input, 0, _outputBuffer, _bufOfs, input.Length);
			_bufOfs += input.Length;
		}
		Logger.Trace($"audio buf status: filled {_bufOfs,4}/{BufferLength,4} {BufferLength - _bufOfs} bytes left");
	}
}
