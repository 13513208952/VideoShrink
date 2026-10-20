plugins {
    id("com.android.application")
    id("dev.flutter.flutter-gradle-plugin")
}

android {
    namespace = "com.videoshrink.android"
    compileSdk = 36
    ndkVersion = flutter.ndkVersion

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    defaultConfig {
        applicationId = "com.videoshrink.android"
        minSdk = 29
        // This is a private sideload build intended for multi-hour unattended jobs.
        // Android 15 applies the six-hour mediaProcessing quota to target 35+ apps.
        targetSdk = 34
        versionCode = flutter.versionCode
        versionName = flutter.versionName
    }

    buildTypes {
        release {
            signingConfig = signingConfigs.getByName("debug")
        }
    }

    packaging {
        jniLibs {
            // Extract the FFmpeg PIE into nativeLibraryDir, where Android permits execution.
            useLegacyPackaging = true
            keepDebugSymbols += "**/libvideoshrink_ffmpeg.so"
        }
    }
}

kotlin {
    compilerOptions {
        jvmTarget = org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17
    }
}

flutter {
    source = "../.."
}
