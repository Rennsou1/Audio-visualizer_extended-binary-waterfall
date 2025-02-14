# Extended Binary Waterfall

This program **generates video and audio** based on an **arbitrary computer file** (resulting in what's sometimes known as a **binary waterfall**), but in the process, it also includes a **detailed walktrough** of the **fragments, chunks or subfiles** that the target file may have.

> [!WARNING]
> This program is still in early development. The code is still being cleaned up, and errors are expected to happen when running this software.

## Usage

### Dependencies

- .NET 9 SDK
- [Unifont](https://unifoundry.com/unifont/index.html)
	- Some Linux distros have the option to install this font via their respective package manager, but in Windows a manual download is required.
	Make sure to download both the default font and the “upper” variant for emoticons.
- FFmpeg libraries
	- The above text also applies to this one.

Additional software will be required for certain format parsers:

- `wimlib` for WIM file listing.

### Examples

#### Example 1

Read an ISO file and output the result to the standard output as an MKV video (requires FFmpeg):

```
Unai.ExtendedBinaryWaterfall /path/to/file.iso --exporter=ffmpeg
```

You can append `> output.mkv` to redirect the standard output to a file instead.

#### Example 2

Read a GameMaker archive file and preview the result in a SDL window:

```
Unai.ExtendedBinaryWaterfall /path/to/data.win
```

SDL is the default exporter if none is specified.
