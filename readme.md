# Extended Binary Waterfall

This program **generates video and audio** based on an **arbitrary computer file** (resulting in what's sometimes known as a **binary waterfall**), but in the process, it also includes a **detailed walktrough** of the **fragments, chunks or subfiles** that the target file may have.

> [!WARNING]
> This program is still in early development. The code is still being cleaned up, and errors are expected to happen when running this software.

## Usage

### Examples

Read an ISO file and output the result to the standard output as an MKV video (requires FFmpeg):

```
Unai.ExtendedBinaryWaterfall /path/to/iso/file --format=iso
```

You can append `> output.mkv` to redirect the standard output to a file instead.
