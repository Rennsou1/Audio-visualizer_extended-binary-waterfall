# Extended Binary Waterfall

This program **generates video and audio** based on an **arbitrary computer file** (resulting in what's sometimes known as a **binary waterfall**), but in the process, it also includes a **detailed walktrough** of the **fragments, chunks or subfiles** that the target file may have.

> [!WARNING]
> This program is still in development.
> Some code is still untested, and errors are expected to happen when running this software.

## Usage

### Dependencies

- Required
	- .NET 9 SDK
		- It hasn't been tested with older versions but it is **probably compatible** with them. You can try lower the version manually in the `csproj` file.
- Optional
	- FFmpeg libraries (for the FFmpeg exporter)
		- In Windows 10/11, use the following command to install FFmpeg:
			```powershell
			winget install "FFmpeg (Shared)"
			```
			Once installed, **restart the command line** and make sure the `PATH` environment variable is updated with the FFmpeg libraries path.
		- Alternatively, you can manually download the libraries at [CODEX FFMPEG](https://www.gyan.dev/ffmpeg/builds/) (make sure to download the “shared” variant).
		Once downloaded, move the DLLs to a known path (e.g. `C:\ffmpeg`).
	- [Unifont](https://unifoundry.com/unifont/index.html)
		- Some Linux distros have the option to install this font via their respective package manager, but in Windows a manual download is required.
		- Make sure to install both the default font and the “upper” variant for emoticons.
		- Make sure to install the OTF format instead of TTF. There's a known issue with this format that crashes the text rendering library.
	- [`wimlib`](https://wimlib.net/) for WIM file listings.
	- `minidump` Python module for Windows Minidump memory region parsing.

### Build and Run

Use `run.sh` to quickly (build if necessary, then) run the program.

Alternatively, standard `dotnet build`/`dotnet run` commands apply:

Use `dotnet build` from the repository's path, then execute `dotnet run --project Unai.ExtendedBinaryWaterfall.Cli` to run the program.

### Quick Start

The following commands will assume your command line working directory is located at the resulting binaries from the build process.

Execute this command to get information about the arguments that can be used:

```sh
Unai.ExtendedBinaryWaterfall.Cli --help
```

When using `run.sh`, the command can be simplified to:

```sh
./run.sh --help
```

### Examples

#### Example 1

Read an ISO file and output the result to the standard output as an MKV video (requires FFmpeg):

```sh
Unai.ExtendedBinaryWaterfall.Cli /path/to/file.iso --exporter=ffmpeg
```

You can append `> output.mkv` to redirect the standard output to a file instead.

#### Example 2

Read a GameMaker archive file and preview the result in an SDL window:

```sh
Unai.ExtendedBinaryWaterfall.Cli /path/to/data.win
```

SDL is the default exporter if none is specified.
