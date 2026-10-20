# Android ARM64 原生后端构建

发布版本固定使用：

- FFmpeg `n8.1.2`，提交 `38b88335f99e76ed89ff3c93f877fdefce736c13`
- SVT-AV1 `v4.2.0`，提交 `9292ec8e32bce26f781f277ec8739b53426c4300`
- Android NDK `28.2.13676358`
- ABI `arm64-v8a`
- 最低原生 API `android-29`

以下示例中的路径仅是占位符，请替换为本机绝对路径。

## 1. 构建 SVT-AV1 静态库

```bash
cmake -S svt-av1-4.2.0 -B build/svt-av1-arm64 -G Ninja \
  -DCMAKE_TOOLCHAIN_FILE="$ANDROID_NDK/build/cmake/android.toolchain.cmake" \
  -DANDROID_ABI=arm64-v8a \
  -DANDROID_PLATFORM=android-29 \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_INSTALL_PREFIX="$PREFIX" \
  -DBUILD_SHARED_LIBS=OFF \
  -DBUILD_APPS=OFF \
  -DSVT_AV1_LTO=ON
cmake --build build/svt-av1-arm64
cmake --install build/svt-av1-arm64
```

## 2. 构建 FFmpeg PIE

在独立 FFmpeg build 目录中执行：

```bash
ffmpeg-8.1.2/configure \
  --prefix="$FFMPEG_PREFIX" \
  --target-os=android \
  --arch=aarch64 \
  --cpu=armv8-a \
  --enable-cross-compile \
  --cc="$ANDROID_NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/aarch64-linux-android29-clang" \
  --cxx="$ANDROID_NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/aarch64-linux-android29-clang++" \
  --ar="$ANDROID_NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/llvm-ar" \
  --ranlib="$ANDROID_NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/llvm-ranlib" \
  --strip="$ANDROID_NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/llvm-strip" \
  --sysroot="$ANDROID_NDK/toolchains/llvm/prebuilt/linux-x86_64/sysroot" \
  --enable-pic \
  --enable-small \
  --disable-shared \
  --enable-static \
  --disable-doc \
  --disable-debug \
  --disable-ffplay \
  --disable-ffprobe \
  --disable-avdevice \
  --disable-network \
  --enable-jni \
  --enable-mediacodec \
  --enable-libsvtav1 \
  --pkg-config-flags=--static \
  --extra-cflags="-I$PREFIX/include" \
  --extra-ldflags="-L$PREFIX/lib"
make -j"$(nproc)"
make install
```

将 `$FFMPEG_PREFIX/bin/ffmpeg` 复制到：

```text
android/app/src/main/jniLibs/arm64-v8a/libvideoshrink_ffmpeg.so
```

Gradle 使用 legacy JNI packaging 将该 PIE 提取到应用的 `nativeLibraryDir`，Android 才允许应用执行它。
