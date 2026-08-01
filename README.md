# VideoShrink for Android

VideoShrink 的 Android 移植版。应用使用 Flutter 构建界面，在 Android 前台服务中调用原生 FFmpeg，支持 MediaCodec 硬件编码与 SVT-AV1 软件编码。

## 当前功能

- 递归扫描用户通过 SAF 授权的目录
- H.264 / HEVC MediaCodec 硬件编码
- SVT-AV1 10-bit 软件编码，开放 preset 0–13
- 自动、强制硬件、强制软件三种解码路径
- 原始、480p、720p、1080p、1440p、2160p、2880p、4320p 输出
- 目标体积 1%–100%，不根据设备能力限制选项
- 新建 `VideoShrink` 输出目录或安全覆盖源文件
- 前台媒体处理服务与 Partial WakeLock，锁屏后继续运行
- Edge-to-edge 全面屏、挖孔/手势 Insets 与横屏侧边导航适配
- 不需要 Root 或无障碍权限
- Android 10+，当前发布包为 ARM64

## 安装

从仓库的 [Releases](../../releases) 下载 ARM64 APK。当前 Release 使用 Android Debug 证书签名，适合个人侧载与测试，不用于应用商店发布。

## 构建 Flutter 应用

要求：Flutter 3.44 或兼容版本、JDK 17、Android SDK 36、Android NDK r28c，以及 ARM64 Android 29+ 的 VideoShrink FFmpeg PIE。

将原生可执行文件放置为：

```text
android/app/src/main/jniLibs/arm64-v8a/libvideoshrink_ffmpeg.so
```

然后构建：

```bash
flutter pub get
flutter build apk --release --target-platform android-arm64
```

原生文件不提交到 Git；对应版本的预编译文件和完整第三方源码随 GitHub Release 提供。具体构建参数见 [原生构建说明](docs/native-build.md)。

## 数据安全

覆盖模式会先完成编码、校验临时输出、通过 SAF 写入并核对长度，最后才删除源文件。尽管如此，处理重要素材前仍建议保留备份。

## 第三方组件

应用发行包包含 FFmpeg 与 SVT-AV1。许可证、版本、源码获取方式见 [第三方说明](THIRD_PARTY_NOTICES.md)。项目自身以 [MIT License](LICENSE) 开源。
