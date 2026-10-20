# VideoShrink

**Batch-shrink videos to a target size — on Windows, Linux and Android.**

VideoShrink scans a folder, encodes each video towards a requested output size and verifies the
result before anything replaces the source. H.264 (GPU-assisted), H.265 and SVT-AV1 are supported,
and the same options are exposed consistently across the three platforms.

This repository is the unified home of all three applications. Each platform lives in its own
self-contained directory with its original documentation, licence and third-party notices, and the
full development history of the original repositories has been preserved.

## Download

Grab the package for your platform from the **[latest release](../../releases/latest)**:

| Platform | Requirements | Package | App version |
|---|---|---|---|
| Windows | Windows 10/11, x64 | `VideoShrink-v1.0.1-win-x64.zip` | 1.0.1 |
| Linux | x64, glibc 2.28+ | `VideoShrink-linux-x64-v1.0.1.tar.gz` | 1.0.1 |
| Android | Android 10+, arm64 | `VideoShrink-Android-1.2.0-arm64-release-debug-signed.apk` | 1.2.0 |

The unified release bundles the current build of every platform together with the Android native
artefacts and their corresponding FFmpeg / SVT-AV1 source archives. Packages are published as they
were built by their platform projects — nothing is rebuilt during a release.

## Repository layout

```text
.
├── windows/   Windows desktop app — C# / .NET WPF, ffmpeg.exe sidecar
├── linux/     Linux desktop app   — C# / Avalonia, bundled static FFmpeg
└── android/   Android app         — Flutter UI + native FFmpeg/SVT-AV1 (arm64)
```

Each directory is a complete, buildable project:

| Directory | Documentation | Build entry point |
|---|---|---|
| [`windows/`](windows/README.md) | [windows/README.md](windows/README.md) | `cd windows && dotnet publish -c Release` |
| [`linux/`](linux/README.md) | [linux/README.md](linux/README.md) | `cd linux && dotnet publish -c Release -r linux-x64 --self-contained` |
| [`android/`](android/README.md) | [android/README.md](android/README.md) | `cd android && flutter build apk --release --target-platform android-arm64` |

The Android native backend (FFmpeg + SVT-AV1) is built separately; see
[`android/docs/native-build.md`](android/docs/native-build.md).

## Releases

Releases are versioned for the suite as a whole (`v1.3.0`, …). Because the three apps have
independent development histories, each release notes the per-platform application version it
contains. Previous per-platform releases remain available in this repository's release history.

## Licence

VideoShrink uses two licences:

- **Windows and Linux** desktop applications: **GPL-3.0** — see
  [`windows/LICENSE`](windows/LICENSE) and [`linux/LICENSE`](linux/LICENSE).
- **Android** application: **MIT** — see [`android/LICENSE`](android/LICENSE).

Bundled third-party components and their licences are documented next to each platform in
`THIRD-PARTY-NOTICES.md` / `THIRD_PARTY_NOTICES.md`. See [`LICENSE`](LICENSE) for the summary.

---

## 中文简介

VideoShrink 是一个把视频批量压缩到指定体积的工具，编码完成并校验通过后才会替换源文件。
本仓库统一存放三个平台的完整项目：

- **Windows**：C# / .NET WPF 桌面版，随包附带 `ffmpeg.exe`
- **Linux**：C# / Avalonia 桌面版，随包附带静态 FFmpeg
- **Android**：Flutter 界面 + 原生 FFmpeg / SVT-AV1，当前发布包为 arm64

请从 **[最新 Release](../../releases/latest)** 下载对应平台的安装包。三个平台的应用版本独立
演进，统一 Release 会同时附上各平台当前版本、Android 原生库及其对应的 FFmpeg / SVT-AV1
源码包；发布过程不做重新编译。

各平台的使用说明、构建方法和第三方许可分别见 [`windows/`](windows/README.md)、
[`linux/`](linux/README.md) 与 [`android/`](android/README.md)。
