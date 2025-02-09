using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SDL_Sharp;
using SDL_Sharp.Loader;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Unai.ExtendedBinaryWaterfall;

public class SdlExportHandler : ExportHandler
{
	private bool _init = false;
	private bool _quit = false;

	private Window _win;
	private Renderer _ren;
	private PSurface _surface;
	// private AudioSpec _audioSpec;
	private uint _audioDeviceId;

	private Stopwatch _sw = new();
	private int _frameCount = 0;
	private double _ts = 0;

	public void InitializeSdl()
	{
		SdlLoader.LoadDefault();
		unsafe
		{
			_ = SDL.Init(SdlInitFlags.Video | SdlInitFlags.Audio);
			_win = SDL.CreateWindow("Extended Binary Waterfall", SDL.WINDOWPOS_UNDEFINED, SDL.WINDOWPOS_UNDEFINED, 1920, 1080, WindowFlags.Shown | WindowFlags.Resizable);
			if (_win.IsNull) throw new Exception("SDL cannot create a window.");
			_ren = SDL.CreateRenderer(_win, -1, RendererFlags.Accelerated);
			if (_ren.IsNull) throw new Exception("SDL cannot create a renderer.");
			SDL.CreateRGBSurface(0, 1920, 1080, 32, 0, 0, 0, 0, out _surface);

			AudioSpec audioSpec;
			audioSpec.Frequency = 48000;
			audioSpec.Format = 8;
			audioSpec.Channels = 2;
			audioSpec.Samples = (ushort)(2 * audioSpec.Frequency / Program.OutputFps);
			// Console.Error.WriteLine($"{audioSpec.Frequency} {audioSpec.Samples}");
			_audioDeviceId = SDL.OpenAudioDevice(null, 0, &audioSpec, null, 0);
			Console.Error.WriteLine($"OpenAudioDevice() = {_audioDeviceId}");
			_ = SDL.PauseAudioDevice(_audioDeviceId, false);

			// _audioSpec = audioSpec;
		}
		_sw.Start();
		_init = true;
	}
	
	byte[] _framebuffer = new byte[1920 * 1080 * 4];

	public override void PushNewFrame(Image videoFrame, byte[] audioFrame, double delta)
	{
		PushNewFrame((Image<Rgba32>)videoFrame, audioFrame, delta);
	}

	public void PushNewFrame(Image<Rgba32> videoFrame, byte[] audioFrame, double delta)
	{
		if (!_init)
		{
			InitializeSdl();
		}

		if (_quit)
		{
			return;
		}

		while (SDL.PollEvent(out Event e) == 1)
		{
			switch (e.Type)
			{
				case EventType.Quit:
					Finish();
					return;
			}
		}

		unsafe
		{
			SDL.RenderClear(_ren);

			videoFrame.CopyPixelDataTo(_framebuffer);
			SDL.SetRenderDrawColor(_ren, 0, 32, 0, 255);
			Marshal.Copy(_framebuffer, 0, (nint)((Surface*)_surface)->Pixels, _framebuffer.Length);
				
			var tex = SDL.CreateTextureFromSurface(_ren, _surface);
			if (tex.IsNull) Console.Error.WriteLine("Texture is null!");
			SDL.RenderCopy(_ren, tex, 0, 0);

			SDL.RenderPresent(_ren);

			SDL.DestroyTexture(tex);
		}

		_frameCount++;
		_ts = _sw.Elapsed.TotalSeconds;
		var wcFrameCount = (int)(_ts * Program.OutputFps);
		var framediff = _frameCount - wcFrameCount; // positive = too fast

		if (framediff > 1) SDL.Delay((uint)(((1 / (float)Program.OutputFps) - delta) * 1000));

		var audioQueue = SDL.GetQueuedAudioSize(_audioDeviceId);

		if (audioQueue < 24000)
		{
			unsafe
			{
				fixed (byte* audioBufPtr = audioFrame)
				{
					var ret = SDL.QueueAudio(_audioDeviceId, audioBufPtr, audioFrame.Length);
					// if (ret != 0) Console.Error.WriteLine($"cannot queue audio (errno {ret}): {SDL.GetError()}");
				}
			}
		}

		Console.Error.Write($"frame={_frameCount,6} wcframe={wcFrameCount,6} diff={framediff,6} — {(int)(1 / delta)} fps aqueue={audioQueue}\x1b[K\x1b[G");
	}

	public override void Finish()
	{
		_ = SDL.CloseAudioDevice(_audioDeviceId);
		SDL.FreeSurface(_surface);
		SDL.DestroyRenderer(_ren);
		SDL.DestroyWindow(_win);
		SDL.Quit();
	}
}
