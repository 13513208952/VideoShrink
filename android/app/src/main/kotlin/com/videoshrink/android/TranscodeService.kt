package com.videoshrink.android

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.Uri
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import java.io.File
import java.util.ArrayDeque

class TranscodeService : Service() {
    private var wakeLock: PowerManager.WakeLock? = null
    @Volatile private var process: Process? = null
    @Volatile private var prepared: PreparedTranscode? = null
    private var queue: List<VideoDocument> = emptyList()
    private var options: TranscodeOptions? = null
    private var currentIndex = 0
    private var failedCount = 0
    private var lastError = ""
    private var lastWarning = ""
    @Volatile private var stopping = false

    override fun onCreate() {
        super.onCreate()
        getSystemService(NotificationManager::class.java).createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "视频转码", NotificationManager.IMPORTANCE_LOW),
        )
        getSharedPreferences(PREFERENCES, MODE_PRIVATE).edit().putBoolean(RUNNING_KEY, true).apply()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            stopping = true
            stopActiveProcess()
            stopSelf()
            return START_NOT_STICKY
        }
        val initialNotification = buildNotification("正在扫描视频", 0, true)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.VANILLA_ICE_CREAM) {
            startForeground(
                NOTIFICATION_ID,
                initialNotification,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROCESSING,
            )
        } else {
            startForeground(
                NOTIFICATION_ID,
                initialNotification,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC,
            )
        }
        acquireWakeLock()
        if (process != null || queue.isNotEmpty()) return START_NOT_STICKY

        val sourceUri = intent?.getStringExtra("sourceUri")?.let(Uri::parse)
        if (sourceUri == null) {
            finishWithError("未提供源目录")
            return START_NOT_STICKY
        }
        val parsedOptions = runCatching {
            TranscodeOptions(
                sourceTreeUri = sourceUri,
                outputTreeUri = intent.getStringExtra("outputUri")?.let(Uri::parse),
                overwrite = intent.getBooleanExtra("overwrite", false),
                encoder = intent.getStringExtra("encoder") ?: "hevc",
                decoder = intent.getStringExtra("decoder") ?: "auto",
                av1Preset = intent.getIntExtra("av1Preset", 2).also {
                    require(it in 0..13) { "SVT-AV1 preset 必须在 0–13 之间" }
                },
                targetRatio = intent.getDoubleExtra("targetRatio", 0.15),
                resolution = intent.getStringExtra("resolution") ?: "1080p",
            )
        }.getOrElse {
            finishWithError(it.message ?: "任务参数无效")
            return START_NOT_STICKY
        }
        options = parsedOptions
        beginScan(sourceUri)
        return START_NOT_STICKY
    }

    private fun beginScan(sourceUri: Uri) {
        updateState(status = "scanning", total = 0, completed = 0, progress = 0, error = "", warning = "")
        Thread({
            val scanResult = runCatching { VideoTreeScanner.scan(this, sourceUri) }
            if (stopping) return@Thread
            scanResult.onSuccess { videos ->
                queue = videos
                currentIndex = 0
                failedCount = 0
                lastError = ""
                lastWarning = ""
                updateState(status = "queued", total = videos.size, completed = 0, progress = 0)
                if (videos.isEmpty()) finishNormally("没有找到可处理的视频") else processNext()
            }.onFailure { finishWithError("扫描失败：${it.message ?: it.javaClass.simpleName}") }
        }, "VideoShrink-Scanner").start()
    }

    private fun processNext() {
        if (stopping) return
        if (currentIndex >= queue.size) {
            val detail = if (failedCount == 0) "已完成 ${queue.size} 个视频"
            else "完成 ${queue.size - failedCount} 个，失败 $failedCount 个"
            finishNormally(detail)
            return
        }
        val input = queue[currentIndex]
        val selectedOptions = options ?: return finishWithError("任务参数已丢失")
        updateState(
            status = "preparing", current = input.name, total = queue.size,
            completed = currentIndex, progress = 0, error = "",
        )
        updateNotification("正在准备 ${input.name}", 0, true)
        Thread({ runOne(input, selectedOptions) }, "VideoShrink-Encoder").start()
    }

    private fun runOne(input: VideoDocument, selectedOptions: TranscodeOptions) {
        val result = runCatching {
            val job = TranscodeEngine.prepare(this, input, selectedOptions, currentIndex)
            prepared = job
            if (stopping) error("任务已停止")
            updateState(status = "encoding", progress = 0)
            updateNotification("正在压缩 ${input.name}", 0, false)
            val recentLog = ArrayDeque<String>()
            val active = ProcessBuilder(job.command).redirectErrorStream(true).start()
            process = active
            var lastProgress = -1
            active.inputStream.bufferedReader().useLines { lines ->
                lines.forEach { line ->
                    if (recentLog.size >= MAX_LOG_LINES) recentLog.removeFirst()
                    recentLog.addLast(line)
                    val value = TranscodeEngine.parseProgress(line, job.durationUs)
                    if (value != null && value != lastProgress) {
                        lastProgress = value
                        updateState(status = "encoding", progress = value)
                        updateNotification("正在压缩 ${input.name}", value, false)
                    }
                }
            }
            val exitCode = active.waitFor()
            process = null
            if (stopping) error("任务已停止")
            require(exitCode == 0) {
                recentLog.lastOrNull {
                    it.contains("error", ignoreCase = true) && !it.contains("Conversion failed", ignoreCase = true)
                } ?: recentLog.lastOrNull { it.isNotBlank() && !it.startsWith("progress=") }
                    ?: "FFmpeg 退出码 $exitCode"
            }
            TranscodeEngine.validateOutput(job.outputFile)
            TranscodeEngine.sizeWarning(input, selectedOptions, job.outputFile)?.let {
                lastWarning = it
                updateState(warning = it)
            }
            updateState(status = "writing", progress = 100)
            updateNotification("正在校验并写入 ${input.name}", 100, true)
            TranscodeEngine.commitOutput(this, input, selectedOptions, job.outputFile)
        }
        val job = prepared
        prepared = null
        TranscodeEngine.cleanPrepared(job)
        if (stopping) return
        result.onSuccess { outputUri ->
            updateState(lastOutput = outputUri.toString())
        }.onFailure { error ->
            failedCount++
            lastError = "${input.name}：${error.message ?: "编码失败"}"
            updateState(error = lastError)
        }
        currentIndex++
        updateState(completed = currentIndex, progress = 0)
        processNext()
    }

    private fun stopActiveProcess() {
        process?.let { active ->
            runCatching { active.destroy() }
            if (active.isAlive) runCatching { active.destroyForcibly() }
        }
        process = null
    }

    private fun finishNormally(detail: String) {
        updateState(
            status = if (failedCount == 0) "completed" else "completed_with_errors",
            completed = currentIndex, progress = 100, error = lastError, warning = lastWarning,
        )
        updateNotification(detail, 100, false)
        stopSelf()
    }

    private fun finishWithError(message: String) {
        lastError = message
        updateState(status = "error", error = message, progress = 0)
        updateNotification(message, 0, false)
        stopSelf()
    }

    @Suppress("WakelockTimeout")
    private fun acquireWakeLock() {
        if (wakeLock?.isHeld == true) return
        wakeLock = getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "VideoShrink:Transcode")
            .apply { acquire() }
    }

    private fun buildNotification(text: String, progress: Int, indeterminate: Boolean): Notification {
        val launchIntent = packageManager.getLaunchIntentForPackage(packageName)
        val contentIntent = PendingIntent.getActivity(
            this, 0, launchIntent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        return Notification.Builder(this, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_sys_upload)
            .setContentTitle("VideoShrink")
            .setContentText(text)
            .setContentIntent(contentIntent)
            .setOnlyAlertOnce(true)
            .setOngoing(true)
            .setProgress(100, progress.coerceIn(0, 100), indeterminate)
            .build()
    }

    private fun updateNotification(text: String, progress: Int, indeterminate: Boolean) {
        getSystemService(NotificationManager::class.java)
            .notify(NOTIFICATION_ID, buildNotification(text, progress, indeterminate))
    }

    private fun updateState(
        status: String? = null,
        current: String? = null,
        total: Int? = null,
        completed: Int? = null,
        progress: Int? = null,
        error: String? = null,
        warning: String? = null,
        lastOutput: String? = null,
    ) {
        getSharedPreferences(PREFERENCES, MODE_PRIVATE).edit().apply {
            status?.let { putString(STATUS_KEY, it) }
            current?.let { putString(CURRENT_KEY, it) }
            total?.let { putInt(TOTAL_KEY, it) }
            completed?.let { putInt(COMPLETED_KEY, it) }
            progress?.let { putInt(PROGRESS_KEY, it) }
            error?.let { putString(ERROR_KEY, it) }
            warning?.let { putString(WARNING_KEY, it) }
            lastOutput?.let { putString(LAST_OUTPUT_KEY, it) }
        }.apply()
    }

    override fun onDestroy() {
        stopping = true
        stopActiveProcess()
        TranscodeEngine.cleanPrepared(prepared)
        prepared = null
        wakeLock?.let { if (it.isHeld) it.release() }
        wakeLock = null
        getSharedPreferences(PREFERENCES, MODE_PRIVATE).edit().putBoolean(RUNNING_KEY, false).apply()
        super.onDestroy()
    }

    override fun onTimeout(startId: Int, fgsType: Int) {
        updateState(status = "error", error = "Android 已达到媒体处理服务的时间上限")
        stopSelf(startId)
    }

    override fun onBind(intent: Intent?): IBinder? = null

    companion object {
        const val ACTION_STOP = "com.videoshrink.android.action.STOP"
        const val PREFERENCES = "videoshrink"
        private const val RUNNING_KEY = "serviceRunning"
        private const val STATUS_KEY = "taskStatus"
        private const val CURRENT_KEY = "taskCurrent"
        private const val TOTAL_KEY = "taskTotal"
        private const val COMPLETED_KEY = "taskCompleted"
        private const val PROGRESS_KEY = "taskProgress"
        private const val ERROR_KEY = "taskError"
        private const val WARNING_KEY = "taskWarning"
        private const val LAST_OUTPUT_KEY = "lastOutputUri"
        private const val CHANNEL_ID = "transcode"
        private const val NOTIFICATION_ID = 1001
        private const val MAX_LOG_LINES = 80

        fun isMarkedRunning(context: Context): Boolean =
            context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE).getBoolean(RUNNING_KEY, false)

        fun state(context: Context): Map<String, Any?> {
            val preferences = context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
            return mapOf(
                "serviceRunning" to preferences.getBoolean(RUNNING_KEY, false),
                "taskStatus" to preferences.getString(STATUS_KEY, "idle"),
                "taskCurrent" to preferences.getString(CURRENT_KEY, null),
                "taskTotal" to preferences.getInt(TOTAL_KEY, 0),
                "taskCompleted" to preferences.getInt(COMPLETED_KEY, 0),
                "taskProgress" to preferences.getInt(PROGRESS_KEY, 0),
                "taskError" to preferences.getString(ERROR_KEY, null),
                "taskWarning" to preferences.getString(WARNING_KEY, null),
                "lastOutputUri" to preferences.getString(LAST_OUTPUT_KEY, null),
            )
        }
    }
}
