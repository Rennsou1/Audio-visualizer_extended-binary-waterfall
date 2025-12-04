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

	// 将输入数据推入缓冲区，当缓冲区满时调用 OutputCallback
	public void Push(T[] input)
	{
		Push(input.AsSpan());
	}
	
	// 重载：接受 Span 避免数组分配
	public void Push(ReadOnlySpan<T> input)
	{
		int inputOfs = 0;
		
		while (inputOfs < input.Length)
		{
			// 计算本次可以复制多少数据
			int spaceLeft = BufferLength - _bufOfs;
			int toCopy = Math.Min(spaceLeft, input.Length - inputOfs);
			
			// 复制数据到缓冲区（使用 Span.CopyTo 比 Array.Copy 更高效）
			input.Slice(inputOfs, toCopy).CopyTo(_outputBuffer.AsSpan(_bufOfs, toCopy));
			inputOfs += toCopy;
			_bufOfs += toCopy;
			
			// 缓冲区满时输出并重置
			if (_bufOfs >= BufferLength)
			{
				OutputCallback?.Invoke(_outputBuffer);
				_bufOfs = 0;
			}
		}
	}
}
