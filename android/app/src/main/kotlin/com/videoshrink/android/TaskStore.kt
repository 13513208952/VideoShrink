package com.videoshrink.android

import android.content.Context
import android.net.Uri
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.util.UUID

data class TaskFileRecord(
    val uri: String,
    val parentUri: String,
    val name: String,
    val mimeType: String,
    val size: Long,
    val lastModified: Long,
    var status: String = "pending",
    var error: String = "",
    var outputUri: String = "",
) {
    fun document() = VideoDocument(
        Uri.parse(uri), Uri.parse(parentUri), name, mimeType, size, lastModified,
    )
}

data class TaskBatch(
    val id: String,
    val createdAt: Long,
    var updatedAt: Long,
    var status: String,
    val options: TranscodeOptions,
    val files: MutableList<TaskFileRecord>,
    var currentIndex: Int = 0,
    var progress: Int = 0,
    var error: String = "",
    var warning: String = "",
)

object TaskStore {
    private const val FILE_NAME = "active-task.json"
    private var cached: TaskBatch? = null
    private var cachedModified = -1L
    val activeStatuses = setOf("scanning", "queued", "preparing", "encoding", "writing", "paused", "cancelling")
    val resumableStatuses = setOf("cancelled", "interrupted", "error")

    @Synchronized
    fun create(context: Context, options: TranscodeOptions, videos: List<VideoDocument>, skipped: Set<String>): TaskBatch {
        val now = System.currentTimeMillis()
        val batch = TaskBatch(
            id = UUID.randomUUID().toString(), createdAt = now, updatedAt = now, status = "queued",
            options = options,
            files = videos.map { video ->
                TaskFileRecord(
                    video.uri.toString(), video.parentUri.toString(), video.name, video.mimeType,
                    video.size, video.lastModified,
                    status = if (video.uri.toString() in skipped) "skipped" else "pending",
                )
            }.toMutableList(),
        )
        save(context, batch)
        return batch
    }

    @Synchronized
    fun load(context: Context): TaskBatch? {
        val file = File(context.filesDir, FILE_NAME)
        if (!file.isFile) return null
        if (cached != null && cachedModified == file.lastModified()) return cached
        return runCatching { fromJson(JSONObject(file.readText())) }.getOrNull().also {
            cached = it
            cachedModified = file.lastModified()
        }
    }

    @Synchronized
    fun save(context: Context, batch: TaskBatch) {
        batch.updatedAt = System.currentTimeMillis()
        val destination = File(context.filesDir, FILE_NAME)
        val temporary = File(context.filesDir, "$FILE_NAME.tmp")
        temporary.writeText(toJson(batch).toString())
        if (!temporary.renameTo(destination)) {
            temporary.copyTo(destination, overwrite = true)
            temporary.delete()
        }
        cached = batch
        cachedModified = destination.lastModified()
    }

    @Synchronized
    fun delete(context: Context): Boolean = File(context.filesDir, FILE_NAME).let {
        val deleted = !it.exists() || it.delete()
        if (deleted) { cached = null; cachedModified = -1L }
        deleted
    }

    fun asMap(batch: TaskBatch?): Map<String, Any?> {
        if (batch == null) return mapOf("task" to null)
        val completed = batch.files.count { it.status == "completed" }
        val failed = batch.files.count { it.status == "failed" }
        val skipped = batch.files.count { it.status == "skipped" }
        val current = batch.files.getOrNull(batch.currentIndex)
        return mapOf(
            "task" to mapOf(
                "id" to batch.id,
                "createdAt" to batch.createdAt,
                "updatedAt" to batch.updatedAt,
                "status" to batch.status,
                "progress" to batch.progress,
                "currentIndex" to batch.currentIndex,
                "total" to batch.files.size,
                "completed" to completed,
                "failed" to failed,
                "skipped" to skipped,
                "current" to current?.name,
                "error" to batch.error.ifBlank { null },
                "warning" to batch.warning.ifBlank { null },
                "profile" to profileLabel(batch.options),
                "files" to batch.files.map {
                    mapOf(
                        "name" to it.name, "size" to it.size, "status" to it.status,
                        "error" to it.error.ifBlank { null }, "outputUri" to it.outputUri.ifBlank { null },
                    )
                },
            ),
        )
    }

    fun profileKey(options: TranscodeOptions): String = listOf(
        options.encoder, options.decoder, options.av1Preset,
        "%.4f".format(java.util.Locale.ROOT, options.targetRatio), options.resolution,
        if (options.overwrite) "overwrite" else "new-folder",
    ).joinToString("|")

    private fun profileLabel(options: TranscodeOptions): String = buildString {
        append(options.encoder.uppercase())
        if (options.encoder == "av1") append(" · preset ${options.av1Preset}")
        append(" · ${options.resolution} · ${(options.targetRatio * 100).toInt()}%")
    }

    private fun toJson(batch: TaskBatch) = JSONObject().apply {
        put("id", batch.id); put("createdAt", batch.createdAt); put("updatedAt", batch.updatedAt)
        put("status", batch.status); put("currentIndex", batch.currentIndex); put("progress", batch.progress)
        put("error", batch.error); put("warning", batch.warning)
        put("options", JSONObject().apply {
            put("sourceTreeUri", batch.options.sourceTreeUri.toString())
            put("outputTreeUri", batch.options.outputTreeUri?.toString())
            put("overwrite", batch.options.overwrite); put("encoder", batch.options.encoder)
            put("decoder", batch.options.decoder); put("av1Preset", batch.options.av1Preset)
            put("targetRatio", batch.options.targetRatio); put("resolution", batch.options.resolution)
        })
        put("files", JSONArray().apply { batch.files.forEach { file -> put(JSONObject().apply {
            put("uri", file.uri); put("parentUri", file.parentUri); put("name", file.name)
            put("mimeType", file.mimeType); put("size", file.size); put("lastModified", file.lastModified)
            put("status", file.status); put("error", file.error); put("outputUri", file.outputUri)
        }) } })
    }

    private fun fromJson(json: JSONObject): TaskBatch {
        val optionsJson = json.getJSONObject("options")
        val options = TranscodeOptions(
            Uri.parse(optionsJson.getString("sourceTreeUri")),
            optionsJson.optString("outputTreeUri").takeIf { it.isNotBlank() && it != "null" }?.let(Uri::parse),
            optionsJson.optBoolean("overwrite"), optionsJson.getString("encoder"),
            optionsJson.optString("decoder", "auto"), optionsJson.optInt("av1Preset", 2),
            optionsJson.optDouble("targetRatio", 0.15), optionsJson.optString("resolution", "1080p"),
        )
        val filesJson = json.getJSONArray("files")
        val files = MutableList(filesJson.length()) { index -> filesJson.getJSONObject(index).let {
            TaskFileRecord(
                it.getString("uri"), it.getString("parentUri"), it.getString("name"),
                it.optString("mimeType", "video/*"), it.optLong("size"), it.optLong("lastModified"),
                it.optString("status", "pending"), it.optString("error"), it.optString("outputUri"),
            )
        } }
        return TaskBatch(
            json.getString("id"), json.getLong("createdAt"), json.optLong("updatedAt"),
            json.optString("status", "interrupted"), options, files,
            json.optInt("currentIndex"), json.optInt("progress"),
            json.optString("error"), json.optString("warning"),
        )
    }
}
