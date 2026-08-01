package com.videoshrink.android

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.media.MediaCodecList
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Environment
import android.provider.DocumentsContract
import android.provider.Settings
import android.view.WindowManager
import android.view.View
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodCall
import io.flutter.plugin.common.MethodChannel

class MainActivity : FlutterActivity() {
    private var pendingDirectoryResult: MethodChannel.Result? = null
    private var pendingDirectoryKind: String? = null
    private var pendingServiceResult: MethodChannel.Result? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        configureEdgeToEdge()
        super.onCreate(savedInstanceState)
        applyKeepScreenOn(AppSettingsStore.load(this).keepScreenOn)
    }

    @Suppress("DEPRECATION")
    private fun configureEdgeToEdge() {
        window.statusBarColor = Color.TRANSPARENT
        window.navigationBarColor = Color.TRANSPARENT
        window.navigationBarDividerColor = Color.TRANSPARENT
        window.isStatusBarContrastEnforced = false
        window.isNavigationBarContrastEnforced = false
        window.attributes = window.attributes.apply {
            layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            window.setDecorFitsSystemWindows(false)
        } else {
            window.decorView.systemUiVisibility =
                View.SYSTEM_UI_FLAG_LAYOUT_STABLE or
                    View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or
                    View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
        }
    }

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, CHANNEL).setMethodCallHandler { call, result ->
            when (call.method) {
                "getState" -> result.success(platformState())
                "requestAllFilesAccess" -> openAllFilesAccessSettings(result)
                "pickDirectory" -> pickDirectory(call, result)
                "startService" -> startTranscodeService(call, result)
                "pauseTask" -> {
                    startService(Intent(this, TranscodeService::class.java).setAction(TranscodeService.ACTION_PAUSE))
                    result.success(null)
                }
                "resumeTask" -> {
                    startForegroundService(Intent(this, TranscodeService::class.java).setAction(TranscodeService.ACTION_RESUME))
                    result.success(null)
                }
                "cancelTask" -> {
                    startService(Intent(this, TranscodeService::class.java).setAction(TranscodeService.ACTION_CANCEL))
                    result.success(null)
                }
                "deleteTask" -> deleteTask(result)
                "updateSettings" -> updateSettings(call, result)
                "clearProcessedHistory" -> {
                    result.success(ProcessingHistory.clear(this))
                }
                else -> result.notImplemented()
            }
        }
    }

    private fun platformState(): Map<String, Any?> {
        val preferences = getSharedPreferences(PREFERENCES, MODE_PRIVATE)
        val settings = AppSettingsStore.load(this)
        return mapOf(
            "hasAllFilesAccess" to hasAllFilesAccess(),
            "sourceUri" to preferences.getString("sourceUri", null),
            "sourceName" to preferences.getString("sourceName", null),
            "outputUri" to preferences.getString("outputUri", null),
            "outputName" to preferences.getString("outputName", null),
            "encoders" to encoderCapabilities(),
            "settings" to AppSettingsStore.asMap(settings),
            "processedHistoryCount" to ProcessingHistory.count(this),
        ) + TranscodeService.state(this)
    }

    private fun applyKeepScreenOn(enabled: Boolean) {
        if (enabled) window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        else window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
    }

    private fun updateSettings(call: MethodCall, result: MethodChannel.Result) {
        val values = call.arguments as? Map<*, *> ?: emptyMap<String, Any>()
        val settings = AppSettingsStore.update(this, values)
        applyKeepScreenOn(settings.keepScreenOn)
        result.success(AppSettingsStore.asMap(settings))
    }

    private fun deleteTask(result: MethodChannel.Result) {
        if (TranscodeService.isMarkedRunning(this)) {
            result.error("queue_active", "运行中的队列不能删除，请先终止", null)
            return
        }
        if (!TaskStore.delete(this)) {
            result.error("delete_failed", "无法删除任务记录", null)
            return
        }
        getSharedPreferences(PREFERENCES, MODE_PRIVATE).edit()
            .remove("taskStatus").remove("taskCurrent").remove("taskTotal")
            .remove("taskCompleted").remove("taskProgress").remove("taskError")
            .remove("taskWarning").remove("lastOutputUri").apply()
        result.success(null)
    }

    private fun encoderCapabilities(): Map<String, List<Map<String, Any>>> {
        val wanted = mapOf("h264" to "video/avc", "hevc" to "video/hevc", "av1" to "video/av01")
        val codecInfos = MediaCodecList(MediaCodecList.ALL_CODECS).codecInfos.filter { it.isEncoder }
        return wanted.mapValues { (_, mime) ->
            codecInfos.filter { info -> info.supportedTypes.any { it.equals(mime, ignoreCase = true) } }
                .map { info ->
                    mapOf(
                        "name" to info.name,
                        "hardware" to info.isHardwareAccelerated,
                        "software" to info.isSoftwareOnly,
                    )
                }
        }
    }

    private fun hasAllFilesAccess(): Boolean =
        Build.VERSION.SDK_INT < Build.VERSION_CODES.R || Environment.isExternalStorageManager()

    private fun openAllFilesAccessSettings(result: MethodChannel.Result) {
        if (hasAllFilesAccess()) {
            result.success(null)
            return
        }
        try {
            startActivity(
                Intent(
                    Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION,
                    Uri.parse("package:$packageName"),
                ),
            )
        } catch (_: Exception) {
            startActivity(Intent(Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION))
        }
        result.success(null)
    }

    private fun pickDirectory(call: MethodCall, result: MethodChannel.Result) {
        if (pendingDirectoryResult != null) {
            result.error("picker_busy", "目录选择器已经打开", null)
            return
        }
        val kind = call.argument<String>("kind")
        if (kind != "source" && kind != "output") {
            result.error("invalid_kind", "目录类型无效", null)
            return
        }
        pendingDirectoryResult = result
        pendingDirectoryKind = kind
        val preferences = getSharedPreferences(PREFERENCES, MODE_PRIVATE)
        val previousUri = preferences.getString("${kind}Uri", null)
        val intent = Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).apply {
            addFlags(
                Intent.FLAG_GRANT_READ_URI_PERMISSION or
                    Intent.FLAG_GRANT_WRITE_URI_PERMISSION or
                    Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION or
                    Intent.FLAG_GRANT_PREFIX_URI_PERMISSION,
            )
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && previousUri != null) {
                putExtra(DocumentsContract.EXTRA_INITIAL_URI, Uri.parse(previousUri))
            }
        }
        startActivityForResult(intent, REQUEST_DIRECTORY)
    }

    @Deprecated("Deprecated by Android; FlutterActivity still forwards this callback")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != REQUEST_DIRECTORY) return
        val result = pendingDirectoryResult
        val kind = pendingDirectoryKind
        pendingDirectoryResult = null
        pendingDirectoryKind = null
        if (resultCode != Activity.RESULT_OK || data?.data == null || result == null || kind == null) {
            result?.success(null)
            return
        }
        val uri = data.data!!
        val requestedFlags = data.flags and
            (Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
        try {
            contentResolver.takePersistableUriPermission(uri, requestedFlags)
        } catch (error: SecurityException) {
            result.error("permission_failed", "无法保留该目录的访问权限", error.message)
            return
        }
        val name = directoryName(uri)
        getSharedPreferences(PREFERENCES, MODE_PRIVATE).edit()
            .putString("${kind}Uri", uri.toString())
            .putString("${kind}Name", name)
            .apply()
        result.success(mapOf("uri" to uri.toString(), "name" to name))
    }

    private fun directoryName(uri: Uri): String = try {
        Uri.decode(DocumentsContract.getTreeDocumentId(uri)).substringAfterLast(':').ifBlank { "存储根目录" }
    } catch (_: Exception) {
        uri.lastPathSegment ?: "已选择目录"
    }

    private fun startTranscodeService(call: MethodCall, result: MethodChannel.Result) {
        val existing = TaskStore.load(this)
        if (TranscodeService.isMarkedRunning(this) || existing?.status in TaskStore.activeStatuses) {
            result.error("queue_active", "已有队列正在运行或暂停", null)
            return
        }
        if (existing?.status in TaskStore.resumableStatuses) {
            result.error("queue_pending", "已有可继续的队列，请先继续或删除它", null)
            return
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            if (pendingServiceResult != null) {
                result.error("permission_busy", "正在等待通知权限", null)
                return
            }
            pendingServiceResult = result
            pendingServiceArguments = call.arguments as? Map<*, *>
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), REQUEST_NOTIFICATIONS)
            return
        }
        launchService(call.arguments as? Map<*, *>)
        result.success(null)
    }

    private var pendingServiceArguments: Map<*, *>? = null

    private fun launchService(arguments: Map<*, *>?) {
        val intent = Intent(this, TranscodeService::class.java).apply {
            putExtra("sourceUri", arguments?.get("sourceUri") as? String)
            putExtra("outputUri", arguments?.get("outputUri") as? String)
            putExtra("overwrite", arguments?.get("overwrite") as? Boolean ?: false)
            putExtra("encoder", arguments?.get("encoder") as? String)
            putExtra("decoder", arguments?.get("decoder") as? String ?: "auto")
            putExtra("av1Preset", arguments?.get("av1Preset") as? Int ?: 2)
            putExtra("targetRatio", arguments?.get("targetRatio") as? Double ?: 0.15)
            putExtra("resolution", arguments?.get("resolution") as? String)
        }
        startForegroundService(intent)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode != REQUEST_NOTIFICATIONS) return
        val result = pendingServiceResult
        val arguments = pendingServiceArguments
        pendingServiceResult = null
        pendingServiceArguments = null
        if (grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) {
            launchService(arguments)
            result?.success(null)
        } else {
            result?.error("notification_denied", "需要通知权限才能可靠地执行后台转码", null)
        }
    }

    companion object {
        private const val CHANNEL = "com.videoshrink.android/platform"
        private const val PREFERENCES = "videoshrink"
        private const val REQUEST_DIRECTORY = 7001
        private const val REQUEST_NOTIFICATIONS = 7002
    }
}
