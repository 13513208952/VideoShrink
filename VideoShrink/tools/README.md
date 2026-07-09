# tools/

`ffmpeg.exe` and `ffprobe.exe` live here at build time but are **not tracked in git**
(they are ~140 MB each).

## Where to get them

Download the **GPL** build (not `nonfree`) from
[BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds/releases):

```
ffmpeg-master-latest-win64-gpl.zip
```

Extract `bin/ffmpeg.exe` and `bin/ffprobe.exe` into this directory.

## Why the GPL build and not the nonfree one

Binaries produced with `--enable-nonfree` **may not be redistributed**. The nonfree build
only adds `libfdk-aac` and `cuda-llvm`, neither of which VideoShrink uses — audio is
stream-copied whenever the container allows it, and we never invoke the hardware encoders.
The GPL build contains everything we need:

- `libx265` (HEVC encoder)
- `libsvtav1` (AV1 encoder)
- `libvmaf` (unused at runtime, handy for evaluation)

Verify a candidate build before shipping it:

```sh
ffmpeg -hide_banner -buildconf | grep nonfree      # must print nothing
ffmpeg -hide_banner -encoders  | grep -E 'libx265|libsvtav1'
```
