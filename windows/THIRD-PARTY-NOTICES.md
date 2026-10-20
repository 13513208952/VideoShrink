# Third-Party Notices

VideoShrink itself is licensed under **GPL-3.0** (see [LICENSE](LICENSE)).
It bundles and/or depends on the following third-party components.

---

## FFmpeg (`ffmpeg.exe`, `ffprobe.exe`)

VideoShrink ships prebuilt Windows binaries of FFmpeg and invokes them as separate processes.

- **Upstream:** <https://ffmpeg.org/> — source: <https://git.ffmpeg.org/ffmpeg.git>
- **Prebuilt by:** [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)
  (`ffmpeg-master-latest-win64-gpl.zip`)
- **Version shipped:** `N-125505-gc57660fb18-20260708`
- **Licence:** **GPL v3** — this build is configured with `--enable-gpl --enable-version3`.

> The build is **not** `--enable-nonfree`. Binaries produced with `--enable-nonfree` may not be
> redistributed. VideoShrink deliberately uses the GPL build; it explicitly disables
> `libfdk-aac` (`--disable-libfdk-aac`), which VideoShrink never needed.

**Obtaining the source:** the exact sources for this binary are the FFmpeg git tree at commit
`c57660fb18`, together with the build scripts published at
<https://github.com/BtbN/FFmpeg-Builds>. Both are publicly available at no charge.

<details>
<summary>Full <code>ffmpeg -buildconf</code> of the shipped binary</summary>

```
--prefix=/ffbuild/prefix --pkg-config-flags=--static --pkg-config=pkg-config
--cross-prefix=x86_64-w64-mingw32- --arch=x86_64 --target-os=mingw32
--enable-gpl --enable-version3 --disable-debug --disable-w32threads --enable-pthreads
--enable-iconv --enable-zlib --enable-libxml2 --enable-libvmaf --enable-fontconfig
--enable-libharfbuzz --enable-libfreetype --enable-libfribidi --enable-vulkan
--enable-libshaderc --enable-libvorbis --disable-libxcb --disable-xlib --disable-libpulse
--enable-gmp --enable-lzma --enable-liblcevc-dec --enable-opencl --enable-amf --enable-libaom
--enable-libaribb24 --enable-avisynth --enable-chromaprint --enable-libdav1d --enable-libdavs2
--enable-libdvdread --enable-libdvdnav --disable-libfdk-aac --enable-ffnvcodec
--enable-cuda-llvm --enable-frei0r --enable-libgme --enable-libkvazaar --enable-libaribcaption
--enable-libass --enable-libbluray --enable-libjxl --enable-libmp3lame --enable-libopus
--enable-libplacebo --enable-librist --enable-libssh --enable-libtheora --enable-libvpx
--enable-libwebp --enable-libzmq --enable-lv2 --enable-libvpl --enable-openal --enable-liboapv
--enable-libopencore-amrnb --enable-libopencore-amrwb --enable-libopenh264 --enable-libopenjpeg
--enable-libopenmpt --enable-librav1e --enable-librubberband --enable-schannel --enable-sdl2
--enable-libsnappy --enable-libsoxr --enable-libsrt --enable-libsvtav1 --enable-libtwolame
--enable-libuavs3d --disable-libdrm --enable-vaapi --enable-libvidstab --enable-libvvenc
--disable-whisper --enable-libx264 --enable-libx265 --enable-libxavs2 --enable-libxvid
--enable-libzimg --enable-libzvbi --extra-cflags=-DLIBTWOLAME_STATIC --extra-libs=-lgomp
--extra-ldflags=-pthread --cc=x86_64-w64-mingw32-gcc --cxx=x86_64-w64-mingw32-g++
--extra-version=20260708
```
</details>

### Notable libraries inside that FFmpeg build, used by VideoShrink

| Library | Purpose here | Licence |
|---|---|---|
| **x265** | HEVC encoder (`libx265`) | GPL v2 or later |
| **SVT-AV1** | AV1 encoder (`libsvtav1`) | BSD-3-Clause + Alliance for Open Media Patent License 1.0 |
| **libvmaf** | Quality measurement (evaluation only) | BSD-2-Clause-Patent |
| **libopus** | Audio fallback encoder for MKV/WebM | BSD-3-Clause |

Because the FFmpeg binary is GPL v3, redistributing VideoShrink together with it means the
combined distribution is GPL v3. That is why this project is GPL-3.0.

---

## .NET / NuGet dependencies

| Package | Licence |
|---|---|
| [MaterialDesignThemes](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit) | MIT |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | MIT |
| [Serilog](https://github.com/serilog/serilog) (+ `Sinks.File`, `Sinks.Debug`) | Apache-2.0 |
| [SQLite](https://sqlite.org/) (native, via Microsoft.Data.Sqlite) | Public Domain |
| .NET Runtime (bundled by self-contained publish) | MIT |

---

## Trademarks

FFmpeg is a trademark of Fabrice Bellard. VideoShrink is not affiliated with, nor endorsed by,
the FFmpeg project.
