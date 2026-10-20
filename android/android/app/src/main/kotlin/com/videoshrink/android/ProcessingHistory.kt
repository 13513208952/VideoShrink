package com.videoshrink.android

import android.content.Context
import android.net.Uri
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

object ProcessingHistory {
    private const val FILE_NAME = "processing-history.json"
    private const val META_PREFERENCES = "processing-history-meta"
    private const val MAX_RECORDS = 5000

    data class Record(
        val uri: String, val size: Long, val lastModified: Long,
        val profile: String, val outputUri: String, val processedAt: Long,
    )

    @Synchronized
    fun skippedUris(
        context: Context,
        videos: List<VideoDocument>,
        profile: String,
        settings: AppSettings,
    ): Set<String> {
        val records = read(context)
        val generated = if (settings.skipGenerated) records.mapTo(hashSetOf()) { it.outputUri } else emptySet()
        val byInput = if (settings.skipProcessed) records.filter { it.profile == profile }.groupBy { it.uri } else emptyMap()
        return videos.filter { video ->
            video.uri.toString() in generated || byInput[video.uri.toString()].orEmpty().any {
                it.size == video.size &&
                    (!settings.strictMatching || it.lastModified <= 0L || video.lastModified <= 0L || it.lastModified == video.lastModified)
            }
        }.mapTo(hashSetOf()) { it.uri.toString() }
    }

    @Synchronized
    fun matches(context: Context, video: VideoDocument, profile: String, strict: Boolean): Boolean =
        read(context).any {
            it.uri == video.uri.toString() && it.size == video.size && it.profile == profile &&
                (!strict || it.lastModified <= 0L || video.lastModified <= 0L || it.lastModified == video.lastModified)
        }

    @Synchronized
    fun isGenerated(context: Context, uri: Uri): Boolean =
        read(context).any { it.outputUri == uri.toString() }

    @Synchronized
    fun add(context: Context, input: VideoDocument, profile: String, outputUri: Uri) {
        val records = read(context).filterNot {
            it.uri == input.uri.toString() && it.profile == profile
        }.toMutableList()
        records += Record(
            input.uri.toString(), input.size, input.lastModified, profile,
            outputUri.toString(), System.currentTimeMillis(),
        )
        write(context, records.takeLast(MAX_RECORDS))
        context.getSharedPreferences(META_PREFERENCES, Context.MODE_PRIVATE).edit()
            .putInt("count", records.size.coerceAtMost(MAX_RECORDS)).apply()
    }

    @Synchronized fun count(context: Context): Int {
        val preferences = context.getSharedPreferences(META_PREFERENCES, Context.MODE_PRIVATE)
        if (preferences.contains("count")) return preferences.getInt("count", 0)
        return read(context).size.also { preferences.edit().putInt("count", it).apply() }
    }

    @Synchronized fun clear(context: Context): Boolean {
        val deleted = File(context.filesDir, FILE_NAME).let { !it.exists() || it.delete() }
        if (deleted) context.getSharedPreferences(META_PREFERENCES, Context.MODE_PRIVATE)
            .edit().putInt("count", 0).apply()
        return deleted
    }

    private fun read(context: Context): List<Record> {
        val file = File(context.filesDir, FILE_NAME)
        if (!file.isFile) return emptyList()
        return runCatching {
            val array = JSONArray(file.readText())
            List(array.length()) { index -> array.getJSONObject(index).let {
                Record(
                    it.getString("uri"), it.optLong("size"), it.optLong("lastModified"),
                    it.getString("profile"), it.optString("outputUri"), it.optLong("processedAt"),
                )
            } }
        }.getOrDefault(emptyList())
    }

    private fun write(context: Context, records: List<Record>) {
        val array = JSONArray()
        records.forEach { record -> array.put(JSONObject().apply {
            put("uri", record.uri); put("size", record.size); put("lastModified", record.lastModified)
            put("profile", record.profile); put("outputUri", record.outputUri); put("processedAt", record.processedAt)
        }) }
        val destination = File(context.filesDir, FILE_NAME)
        val temporary = File(context.filesDir, "$FILE_NAME.tmp")
        temporary.writeText(array.toString())
        if (!temporary.renameTo(destination)) {
            temporary.copyTo(destination, overwrite = true)
            temporary.delete()
        }
    }
}
