package com.videoshrink.android

import android.content.Context
import android.media.MediaMetadataRetriever
import android.net.Uri
import android.os.StatFs
import android.provider.DocumentsContract
import java.io.File
import java.util.Locale
import kotlin.math.roundToInt

data class TranscodeOptions(
    val sourceTreeUri: Uri,
    val outputTreeUri: Uri?,
    val overwrite: Boolean,
    val encoder: String,
    val decoder: String,
    val av1Preset: Int,
    val targetRatio: Double,
    val resolution: String,
)

data class PreparedTranscode(
    val inputFile: File,
    val outputFile: File,
    val command: List<String>,
    val durationUs: Long,
)

object TranscodeEngine {
    fun prepare(context: Context, input: VideoDocument, options: TranscodeOptions, index: Int): PreparedTranscode {
        require(options.encoder in setOf("h264", "hevc", "av1")) { "不支持的编码器：${options.encoder}" }
        require(options.decoder in setOf("auto", "hardware", "software")) { "不支持的解码方式：${options.decoder}" }
        require(options.av1Preset in 0..13) { "SVT-AV1 preset 必须在 0–13 之间" }
        require(options.targetRatio in 0.01..1.0) { "目标体积必须在 1%–100% 之间" }

        val directory = File(context.cacheDir, "transcode-work").apply { mkdirs() }
        val inputFile = File(directory, "job-$index.input.${safeExtension(input.name)}")
        val outputFile = File(directory, "job-$index.pending.mp4")
        inputFile.delete()
        outputFile.delete()
        val required = input.size.coerceAtLeast(0L) + (input.size * options.targetRatio).toLong() + MIN_FREE_BYTES
        val usable = StatFs(directory.absolutePath).availableBytes
        require(usable >= required) {
            "内部存储空间不足：任务约需 ${formatBytes(required)}，当前可用 ${formatBytes(usable)}"
        }
        context.contentResolver.openInputStream(input.uri)?.use { source ->
            inputFile.outputStream().buffered(COPY_BUFFER_SIZE).use { destination -> source.copyTo(destination, COPY_BUFFER_SIZE) }
        } ?: error("无法读取源视频")
        require(inputFile.length() > 0L) { "源视频为空或读取失败" }

        val durationUs = durationUs(context, input.uri)
        val totalBitrate = if (durationUs > 0) {
            (input.size.toDouble() * 8_000_000.0 * options.targetRatio / durationUs).roundToInt()
        } else {
            DEFAULT_TOTAL_BITRATE
        }.coerceIn(MIN_TOTAL_BITRATE, MAX_TOTAL_BITRATE)
        val audioBitrate = (totalBitrate / 5).coerceIn(MIN_AUDIO_BITRATE, MAX_AUDIO_BITRATE)
        val videoBitrate = (totalBitrate - audioBitrate).coerceIn(MIN_VIDEO_BITRATE, MAX_VIDEO_BITRATE)

        val command = mutableListOf(
            ffmpegPath(context), "-hide_banner", "-y", "-nostdin", "-nostats",
            "-progress", "pipe:1",
        )
        when (options.decoder) {
            "hardware" -> command += listOf("-hwaccel", "mediacodec")
            "software" -> command += listOf("-hwaccel", "none")
        }
        command += listOf("-i", inputFile.absolutePath, "-map", "0:v:0", "-map", "0:a:0?")
        val requestedHeight = options.resolution.removeSuffix("p").toIntOrNull()
        if (requestedHeight != null) {
            command += listOf("-vf", "scale=-2:$requestedHeight")
        }
        when (options.encoder) {
            "h264" -> command += listOf(
                "-c:v", "h264_mediacodec", "-ndk_codec", "1", "-bitrate_mode", "cbr",
                "-b:v", videoBitrate.toString(), "-g", "60",
            )
            "hevc" -> command += listOf(
                "-c:v", "hevc_mediacodec", "-ndk_codec", "1", "-bitrate_mode", "cbr",
                "-b:v", videoBitrate.toString(), "-g", "60",
            )
            "av1" -> {
                command += listOf(
                    "-c:v", "libsvtav1", "-preset", options.av1Preset.toString(),
                    "-b:v", videoBitrate.toString(), "-pix_fmt", "yuv420p10le", "-g", "240",
                )
                // SVT 4.2 implements M12/M13 only in its real-time prediction structure.
                // Passing rtc=1 is how those user-selected presets remain distinct instead of
                // SVT silently remapping them to M11.
                if (options.av1Preset >= 12) command += listOf("-svtav1-params", "rtc=1:rc=2")
            }
        }
        command += listOf(
            "-c:a", "aac", "-b:a", audioBitrate.toString(),
            "-movflags", "+faststart", outputFile.absolutePath,
        )
        return PreparedTranscode(inputFile, outputFile, command, durationUs)
    }

    fun parseProgress(line: String, durationUs: Long): Int? {
        if (durationUs <= 0 || !line.startsWith("out_time_us=")) return null
        val elapsed = line.substringAfter('=').toLongOrNull() ?: return null
        return (elapsed * 100L / durationUs).toInt().coerceIn(0, 99)
    }

    fun validateOutput(file: File) {
        require(file.length() > MIN_VALID_OUTPUT_BYTES) { "编码器产生的文件过小，拒绝写入" }
    }

    fun sizeWarning(input: VideoDocument, options: TranscodeOptions, output: File): String? {
        if (input.size <= 0) return null
        val requested = input.size * options.targetRatio
        if (output.length() <= requested * 1.25 + SIZE_TOLERANCE_BYTES) return null
        val actualPercent = (output.length().toDouble() * 100.0 / input.size).roundToInt()
        val requestedPercent = (options.targetRatio * 100.0).roundToInt()
        return "${input.name}：目标 $requestedPercent%，实际约 $actualPercent%；编码器在该素材上无法达到所选低码率"
    }

    fun cleanPrepared(prepared: PreparedTranscode?) {
        prepared?.inputFile?.delete()
        prepared?.outputFile?.delete()
    }

    fun commitOutput(context: Context, input: VideoDocument, options: TranscodeOptions, workFile: File): Uri {
        validateOutput(workFile)
        val outputParent = if (options.overwrite) input.parentUri else resolveOutputParent(context, options)
        val baseName = input.name.substringBeforeLast('.', input.name)
        val pendingName = if (options.overwrite) ".$baseName.videoshrink.pending.mp4" else "$baseName.videoshrink.mp4"
        val resolver = context.contentResolver
        val outputUri = DocumentsContract.createDocument(resolver, outputParent, "video/mp4", pendingName)
            ?: error("无法在输出目录创建文件")
        try {
            resolver.openOutputStream(outputUri, "w")?.use { output ->
                workFile.inputStream().buffered().use { source -> source.copyTo(output, COPY_BUFFER_SIZE) }
            } ?: error("无法打开输出文件")
            val outputSize = querySize(context, outputUri)
            require(outputSize == workFile.length()) { "写入校验失败：$outputSize/${workFile.length()} 字节" }
        } catch (error: Exception) {
            runCatching { DocumentsContract.deleteDocument(resolver, outputUri) }
            throw error
        }
        if (!options.overwrite) return outputUri
        require(DocumentsContract.deleteDocument(resolver, input.uri)) {
            "压缩文件已安全写入，但无法删除原文件；已保留两份文件"
        }
        return runCatching { DocumentsContract.renameDocument(resolver, outputUri, "$baseName.mp4") }.getOrNull() ?: outputUri
    }

    private fun ffmpegPath(context: Context): String =
        File(context.applicationInfo.nativeLibraryDir, "libvideoshrink_ffmpeg.so").absolutePath

    private fun durationUs(context: Context, uri: Uri): Long {
        val retriever = MediaMetadataRetriever()
        return try {
            retriever.setDataSource(context, uri)
            (retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)?.toLongOrNull() ?: 0L) * 1000L
        } finally {
            retriever.release()
        }
    }

    private fun safeExtension(name: String): String {
        val extension = name.substringAfterLast('.', "mp4").lowercase(Locale.ROOT)
        return if (extension.matches(Regex("[a-z0-9]{1,8}"))) extension else "mp4"
    }

    private fun resolveOutputParent(context: Context, options: TranscodeOptions): Uri {
        options.outputTreeUri?.let { tree ->
            return DocumentsContract.buildDocumentUriUsingTree(tree, DocumentsContract.getTreeDocumentId(tree))
        }
        val tree = options.sourceTreeUri
        val rootId = DocumentsContract.getTreeDocumentId(tree)
        val root = DocumentsContract.buildDocumentUriUsingTree(tree, rootId)
        val children = DocumentsContract.buildChildDocumentsUriUsingTree(tree, rootId)
        val projection = arrayOf(
            DocumentsContract.Document.COLUMN_DOCUMENT_ID,
            DocumentsContract.Document.COLUMN_DISPLAY_NAME,
            DocumentsContract.Document.COLUMN_MIME_TYPE,
        )
        context.contentResolver.query(children, projection, null, null, null)?.use { cursor ->
            while (cursor.moveToNext()) {
                if (cursor.getString(1) == VideoTreeScanner.OUTPUT_FOLDER &&
                    cursor.getString(2) == DocumentsContract.Document.MIME_TYPE_DIR
                ) return DocumentsContract.buildDocumentUriUsingTree(tree, cursor.getString(0))
            }
        }
        return DocumentsContract.createDocument(
            context.contentResolver, root, DocumentsContract.Document.MIME_TYPE_DIR, VideoTreeScanner.OUTPUT_FOLDER,
        ) ?: error("无法创建 ${VideoTreeScanner.OUTPUT_FOLDER} 输出目录")
    }

    private fun querySize(context: Context, uri: Uri): Long {
        val projection = arrayOf(DocumentsContract.Document.COLUMN_SIZE)
        return context.contentResolver.query(uri, projection, null, null, null)?.use { cursor ->
            if (cursor.moveToFirst() && !cursor.isNull(0)) cursor.getLong(0) else -1L
        } ?: -1L
    }

    private fun formatBytes(bytes: Long): String = when {
        bytes >= 1L shl 30 -> String.format(Locale.ROOT, "%.1f GB", bytes.toDouble() / (1L shl 30))
        bytes >= 1L shl 20 -> String.format(Locale.ROOT, "%.1f MB", bytes.toDouble() / (1L shl 20))
        else -> "${bytes / 1024} KB"
    }

    private const val DEFAULT_TOTAL_BITRATE = 2_500_000
    private const val MIN_TOTAL_BITRATE = 96_000
    private const val MAX_TOTAL_BITRATE = 120_000_000
    private const val MIN_VIDEO_BITRATE = 64_000
    private const val MAX_VIDEO_BITRATE = 100_000_000
    private const val MIN_AUDIO_BITRATE = 32_000
    private const val MAX_AUDIO_BITRATE = 128_000
    private const val MIN_VALID_OUTPUT_BYTES = 16_384
    private const val SIZE_TOLERANCE_BYTES = 16_384
    private const val MIN_FREE_BYTES = 64L * 1024L * 1024L
    private const val COPY_BUFFER_SIZE = 1024 * 1024
}
