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
import android.system.Os
import android.system.OsConstants
import java.io.File
import java.util.ArrayDeque

class TranscodeService : Service() {
    private var wakeLock: PowerManager.WakeLock? = null
    @Volatile private var process: Process? = null
    @Volatile private var processPid: Int? = null
    @Volatile private var prepared: PreparedTranscode? = null
    @Volatile private var paused = false
    @Volatile private var cancelling = false
    @Volatile private var workerActive = false
    private var finalized = false
    private var batch: TaskBatch? = null

    override fun onCreate() {
        super.onCreate()
        getSystemService(NotificationManager::class.java).createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "视频转码", NotificationManager.IMPORTANCE_LOW),
        )
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_PAUSE -> {
                pauseCurrent()
                return START_NOT_STICKY
            }
            ACTION_CANCEL -> {
                cancelCurrent()
                return START_NOT_STICKY
            }
            ACTION_RESUME -> {
                if (process != null) resumeCurrent() else {
                    beginForeground("正在恢复队列")
                    resumeSavedBatch()
                }
                return START_NOT_STICKY
            }
        }
        if (batch != null || process != null) return START_NOT_STICKY
        beginForeground("正在扫描视频")
        val sourceUri = intent?.getStringExtra("sourceUri")?.let(Uri::parse)
        if (sourceUri == null) return finishWithError("未提供源目录").let { START_NOT_STICKY }
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
        TaskStore.delete(this)
        getSharedPreferences(PREFERENCES, MODE_PRIVATE).edit()
            .remove(CURRENT_KEY).remove(LAST_OUTPUT_KEY).apply()
        beginScan(parsedOptions)
        return START_NOT_STICKY
    }

    private fun beginForeground(text: String) {
        val notification = buildNotification(text, 0, true)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.VANILLA_ICE_CREAM) {
            startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROCESSING)
        } else {
            startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        }
        getSharedPreferences(PREFERENCES, MODE_PRIVATE).edit().putBoolean(RUNNING_KEY, true).apply()
        acquireWakeLock()
    }

    private fun beginScan(options: TranscodeOptions) {
        updateFlatState("scanning", null, 0, 0, 0, "", "")
        Thread({
            val settings = AppSettingsStore.load(this)
            val scanResult = runCatching { VideoTreeScanner.scan(this, options.sourceTreeUri, settings) }
            if (cancelling) return@Thread
            scanResult.onSuccess { videos ->
                val profile = TaskStore.profileKey(options)
                val skipped = ProcessingHistory.skippedUris(this, videos, profile, settings)
                batch = TaskStore.create(this, options, videos, skipped)
                val done = skipped.size
                updateFlatState("queued", null, videos.size, done, 0, "", "")
                if (videos.isEmpty()) finishNormally("没有找到可处理的视频") else processNext()
            }.onFailure { finishWithError("扫描失败：${it.message ?: it.javaClass.simpleName}") }
        }, "VideoShrink-Scanner").start()
    }

    private fun resumeSavedBatch() {
        val saved = TaskStore.load(this)
        if (saved == null || saved.status !in TaskStore.resumableStatuses + "paused") {
            finishWithError("没有可继续的队列")
            return
        }
        saved.files.forEach { if (it.status in setOf("preparing", "encoding", "writing")) it.status = "pending" }
        saved.error = ""
        saved.progress = 0
        saved.status = "queued"
        saved.currentIndex = saved.files.indexOfFirst { it.status == "pending" }.let { if (it < 0) saved.files.size else it }
        batch = saved
        cancelling = false
        paused = false
        TaskStore.save(this, saved)
        updateFlatState("queued", saved.files.getOrNull(saved.currentIndex)?.name, saved.files.size, doneCount(saved), 0, "", saved.warning)
        processNext()
    }

    private fun processNext() {
        val currentBatch = batch ?: return finishWithError("任务参数已丢失")
        if (cancelling || paused) return
        val next = (currentBatch.currentIndex until currentBatch.files.size).firstOrNull {
            currentBatch.files[it].status == "pending"
        }
        if (next == null) {
            val failures = currentBatch.files.count { it.status == "failed" }
            val completed = currentBatch.files.count { it.status == "completed" }
            finishNormally(if (failures == 0) "已处理 $completed 个视频" else "完成 $completed 个，失败 $failures 个")
            return
        }
        currentBatch.currentIndex = next
        currentBatch.status = "preparing"
        currentBatch.progress = 0
        currentBatch.files[next].apply { status = "preparing"; error = "" }
        TaskStore.save(this, currentBatch)
        val input = currentBatch.files[next]
        updateFlatState("preparing", input.name, currentBatch.files.size, doneCount(currentBatch), 0, "", currentBatch.warning)
        updateNotification("正在准备 ${input.name}", 0, true)
        workerActive = true
        Thread({ runOne(currentBatch, next) }, "VideoShrink-Encoder").start()
    }

    private fun runOne(currentBatch: TaskBatch, index: Int) {
        val record = currentBatch.files[index]
        val input = record.document()
        val result = runCatching {
            val job = TranscodeEngine.prepare(this, input, currentBatch.options, index)
            prepared = job
            if (cancelling) error("任务已终止")
            record.status = "encoding"
            currentBatch.status = if (paused) "paused" else "encoding"
            TaskStore.save(this, currentBatch)
            updateFlatState(currentBatch.status, record.name, progress = 0)
            updateNotification("正在压缩 ${record.name}", 0, false)
            val recentLog = ArrayDeque<String>()
            val pidFile = File(job.outputFile.parentFile, "job-$index.pid").apply { delete() }
            val wrappedCommand = listOf(
                "/system/bin/sh", "-c", "echo \$\$ > \"\$1\"; shift; exec \"\$@\"",
                "videoshrink", pidFile.absolutePath,
            ) + job.command
            val active = ProcessBuilder(wrappedCommand).redirectErrorStream(true).start()
            process = active
            var pidAttempts = 0
            while (processPid == null && active.isAlive && pidAttempts < 100) {
                processPid = pidFile.takeIf { file -> file.isFile }?.readText()?.trim()?.toIntOrNull()
                if (processPid == null) Thread.sleep(5)
                pidAttempts++
            }
            pidFile.delete()
            if (paused) signal(OsConstants.SIGSTOP)
            var lastProgress = -1
            active.inputStream.bufferedReader().useLines { lines ->
                lines.forEach { line ->
                    if (recentLog.size >= MAX_LOG_LINES) recentLog.removeFirst()
                    recentLog.addLast(line)
                    val value = TranscodeEngine.parseProgress(line, job.durationUs)
                    if (value != null && value != lastProgress) {
                        lastProgress = value
                        currentBatch.progress = value
                        updateFlatState(if (paused) "paused" else "encoding", record.name, progress = value)
                        updateNotification(if (paused) "已暂停 ${record.name}" else "正在压缩 ${record.name}", value, false)
                    }
                }
            }
            val exitCode = active.waitFor()
            process = null
            processPid = null
            if (cancelling) error("任务已终止")
            require(exitCode == 0) {
                recentLog.lastOrNull { it.contains("error", true) && !it.contains("Conversion failed", true) }
                    ?: recentLog.lastOrNull { it.isNotBlank() && !it.startsWith("progress=") }
                    ?: "FFmpeg 退出码 $exitCode"
            }
            TranscodeEngine.validateOutput(job.outputFile)
            TranscodeEngine.sizeWarning(input, currentBatch.options, job.outputFile)?.let {
                currentBatch.warning = it
                updateFlatState(warning = it)
            }
            record.status = "writing"
            currentBatch.status = "writing"
            TaskStore.save(this, currentBatch)
            updateFlatState("writing", record.name, progress = 100)
            updateNotification("正在校验并写入 ${record.name}", 100, true)
            TranscodeEngine.commitOutput(this, input, currentBatch.options, job.outputFile)
        }
        val job = prepared
        prepared = null
        TranscodeEngine.cleanPrepared(job)
        if (cancelling) {
            workerActive = false
            return
        }
        result.onSuccess { outputUri ->
            record.status = "completed"
            record.outputUri = outputUri.toString()
            ProcessingHistory.add(this, input, TaskStore.profileKey(currentBatch.options), outputUri)
            updateFlatState(lastOutput = outputUri.toString())
        }.onFailure { error ->
            record.status = "failed"
            record.error = error.message ?: "编码失败"
            currentBatch.error = "${record.name}：${record.error}"
            updateFlatState(error = currentBatch.error)
        }
        currentBatch.currentIndex = index + 1
        currentBatch.progress = 0
        currentBatch.status = "queued"
        TaskStore.save(this, currentBatch)
        updateFlatState("queued", completed = doneCount(currentBatch), progress = 0)
        workerActive = false
        processNext()
    }

    private fun pauseCurrent() {
        val currentBatch = batch ?: TaskStore.load(this) ?: return
        if (currentBatch.status !in TaskStore.activeStatuses || currentBatch.status == "paused") return
        paused = true
        signal(OsConstants.SIGSTOP)
        currentBatch.status = "paused"
        batch = currentBatch
        TaskStore.save(this, currentBatch)
        releaseWakeLock()
        updateFlatState("paused", progress = currentBatch.progress)
        updateNotification("队列已暂停", currentBatch.progress, false)
    }

    private fun resumeCurrent() {
        val currentBatch = batch ?: return resumeSavedBatch()
        paused = false
        acquireWakeLock()
        signal(OsConstants.SIGCONT)
        if (process != null) {
            currentBatch.status = "encoding"
            TaskStore.save(this, currentBatch)
            updateFlatState("encoding", progress = currentBatch.progress)
            updateNotification("继续压缩 ${currentBatch.files.getOrNull(currentBatch.currentIndex)?.name ?: "视频"}", currentBatch.progress, false)
        } else if (workerActive) {
            currentBatch.status = currentBatch.files.getOrNull(currentBatch.currentIndex)?.status ?: "preparing"
            TaskStore.save(this, currentBatch)
            updateFlatState(currentBatch.status, progress = currentBatch.progress)
        } else {
            currentBatch.status = "queued"
            TaskStore.save(this, currentBatch)
            processNext()
        }
    }

    private fun cancelCurrent() {
        cancelling = true
        paused = false
        val currentBatch = batch ?: TaskStore.load(this)
        currentBatch?.let {
            it.files.getOrNull(it.currentIndex)?.let { file ->
                if (file.status in setOf("preparing", "encoding", "writing")) file.status = "pending"
            }
            it.status = "cancelled"
            it.progress = 0
            TaskStore.save(this, it)
        }
        stopActiveProcess()
        updateFlatState("cancelled", progress = 0)
        finalized = true
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    private fun signal(signal: Int) {
        processPid?.let { pid -> runCatching { Os.kill(pid, signal) } }
    }

    private fun stopActiveProcess() {
        process?.let { active ->
            runCatching { active.destroy() }
            if (active.isAlive) runCatching { active.destroyForcibly() }
        }
        process = null
        processPid = null
    }

    private fun finishNormally(detail: String) {
        val currentBatch = batch
        val failures = currentBatch?.files?.count { it.status == "failed" } ?: 0
        currentBatch?.let {
            it.status = if (failures == 0) "completed" else "completed_with_errors"
            it.progress = 100
            TaskStore.save(this, it)
        }
        updateFlatState(
            if (failures == 0) "completed" else "completed_with_errors",
            completed = currentBatch?.let(::doneCount), progress = 100,
            error = currentBatch?.error, warning = currentBatch?.warning,
        )
        updateNotification(detail, 100, false)
        finalized = true
        stopSelf()
    }

    private fun finishWithError(message: String) {
        batch?.let {
            it.status = "error"; it.error = message; TaskStore.save(this, it)
        }
        updateFlatState("error", progress = 0, error = message)
        updateNotification(message, 0, false)
        finalized = true
        stopSelf()
    }

    private fun doneCount(value: TaskBatch): Int = value.files.count { it.status in setOf("completed", "failed", "skipped") }

    @Suppress("WakelockTimeout")
    private fun acquireWakeLock() {
        if (wakeLock?.isHeld == true) return
        wakeLock = getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "VideoShrink:Transcode")
            .apply { acquire() }
    }

    private fun releaseWakeLock() {
        wakeLock?.let { if (it.isHeld) it.release() }
        wakeLock = null
    }

    private fun actionIntent(action: String, requestCode: Int): PendingIntent = PendingIntent.getService(
        this, requestCode, Intent(this, TranscodeService::class.java).setAction(action),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    private fun buildNotification(text: String, progress: Int, indeterminate: Boolean): Notification {
        val launchIntent = packageManager.getLaunchIntentForPackage(packageName)
        val contentIntent = PendingIntent.getActivity(
            this, 0, launchIntent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val notificationStatus = batch?.status
        val activeNotification = notificationStatus == null || notificationStatus in TaskStore.activeStatuses
        val builder = Notification.Builder(this, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_sys_upload)
            .setContentTitle("VideoShrink")
            .setContentText(text)
            .setContentIntent(contentIntent)
            .setOnlyAlertOnce(true)
            .setOngoing(activeNotification)
            .setProgress(100, progress.coerceIn(0, 100), indeterminate)
        if (!cancelling && activeNotification) {
            if (notificationStatus != null) {
                if (paused) builder.addAction(0, "继续", actionIntent(ACTION_RESUME, 2))
                else builder.addAction(0, "暂停", actionIntent(ACTION_PAUSE, 1))
            }
            builder.addAction(0, "终止", actionIntent(ACTION_CANCEL, 3))
        }
        return builder.build()
    }

    private fun updateNotification(text: String, progress: Int, indeterminate: Boolean) {
        getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, buildNotification(text, progress, indeterminate))
    }

    private fun updateFlatState(
        status: String? = null, current: String? = null, total: Int? = null,
        completed: Int? = null, progress: Int? = null, error: String? = null,
        warning: String? = null, lastOutput: String? = null,
    ) {
        getSharedPreferences(PREFERENCES, MODE_PRIVATE).edit().apply {
            status?.let { putString(STATUS_KEY, it) }; current?.let { putString(CURRENT_KEY, it) }
            total?.let { putInt(TOTAL_KEY, it) }; completed?.let { putInt(COMPLETED_KEY, it) }
            progress?.let { putInt(PROGRESS_KEY, it) }; error?.let { putString(ERROR_KEY, it) }
            warning?.let { putString(WARNING_KEY, it) }; lastOutput?.let { putString(LAST_OUTPUT_KEY, it) }
        }.apply()
    }

    override fun onDestroy() {
        if (!finalized && !cancelling) {
            (batch ?: TaskStore.load(this))?.let {
                if (it.status in TaskStore.activeStatuses) {
                    it.files.getOrNull(it.currentIndex)?.let { file ->
                        if (file.status in setOf("preparing", "encoding", "writing")) file.status = "pending"
                    }
                    it.status = "interrupted"
                    TaskStore.save(this, it)
                    updateFlatState("interrupted")
                }
            }
        }
        stopActiveProcess()
        TranscodeEngine.cleanPrepared(prepared)
        prepared = null
        releaseWakeLock()
        getSharedPreferences(PREFERENCES, MODE_PRIVATE).edit().putBoolean(RUNNING_KEY, false).apply()
        super.onDestroy()
    }

    override fun onTimeout(startId: Int, fgsType: Int) {
        val message = "Android 已达到媒体处理服务的时间上限"
        batch?.let { it.status = "error"; it.error = message; TaskStore.save(this, it) }
        updateFlatState("error", error = message)
        finalized = true
        stopSelf(startId)
    }

    override fun onBind(intent: Intent?): IBinder? = null

    companion object {
        const val ACTION_PAUSE = "com.videoshrink.android.action.PAUSE"
        const val ACTION_RESUME = "com.videoshrink.android.action.RESUME"
        const val ACTION_CANCEL = "com.videoshrink.android.action.CANCEL"
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
            val batch = TaskStore.load(context)
            return mapOf(
                "serviceRunning" to preferences.getBoolean(RUNNING_KEY, false),
                "taskStatus" to preferences.getString(STATUS_KEY, batch?.status ?: "idle"),
                "taskCurrent" to preferences.getString(CURRENT_KEY, null),
                "taskTotal" to preferences.getInt(TOTAL_KEY, batch?.files?.size ?: 0),
                "taskCompleted" to preferences.getInt(COMPLETED_KEY, batch?.let { value -> value.files.count { it.status in setOf("completed", "failed", "skipped") } } ?: 0),
                "taskProgress" to preferences.getInt(PROGRESS_KEY, batch?.progress ?: 0),
                "taskError" to preferences.getString(ERROR_KEY, batch?.error),
                "taskWarning" to preferences.getString(WARNING_KEY, batch?.warning),
                "lastOutputUri" to preferences.getString(LAST_OUTPUT_KEY, null),
            ) + TaskStore.asMap(batch)
        }
    }
}
