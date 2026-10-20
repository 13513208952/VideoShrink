package com.videoshrink.android

import android.content.Context
import android.net.Uri
import android.provider.DocumentsContract

data class VideoDocument(
    val uri: Uri,
    val parentUri: Uri,
    val name: String,
    val mimeType: String,
    val size: Long,
    val lastModified: Long,
)

object VideoTreeScanner {
    private val videoExtensions = setOf(
        "3gp", "avi", "flv", "m2ts", "m4v", "mkv", "mov", "mp4", "mpeg", "mpg", "mts", "ts", "webm",
    )

    fun scan(context: Context, treeUri: Uri, settings: AppSettings = AppSettingsStore.load(context)): List<VideoDocument> {
        val results = mutableListOf<VideoDocument>()
        val rootId = DocumentsContract.getTreeDocumentId(treeUri)
        scanChildren(context, treeUri, rootId, results, settings, 0)
        return results.sortedBy { it.name.lowercase() }
    }

    private fun scanChildren(
        context: Context,
        treeUri: Uri,
        parentId: String,
        results: MutableList<VideoDocument>,
        settings: AppSettings,
        depth: Int,
    ) {
        val parentUri = DocumentsContract.buildDocumentUriUsingTree(treeUri, parentId)
        val childrenUri = DocumentsContract.buildChildDocumentsUriUsingTree(treeUri, parentId)
        val projection = arrayOf(
            DocumentsContract.Document.COLUMN_DOCUMENT_ID,
            DocumentsContract.Document.COLUMN_DISPLAY_NAME,
            DocumentsContract.Document.COLUMN_MIME_TYPE,
            DocumentsContract.Document.COLUMN_SIZE,
            DocumentsContract.Document.COLUMN_LAST_MODIFIED,
        )
        context.contentResolver.query(childrenUri, projection, null, null, null)?.use { cursor ->
            val idIndex = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_DOCUMENT_ID)
            val nameIndex = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_DISPLAY_NAME)
            val mimeIndex = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_MIME_TYPE)
            val sizeIndex = cursor.getColumnIndexOrThrow(DocumentsContract.Document.COLUMN_SIZE)
            val modifiedIndex = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_LAST_MODIFIED)
            while (cursor.moveToNext()) {
                val documentId = cursor.getString(idIndex)
                val name = cursor.getString(nameIndex) ?: continue
                val mime = cursor.getString(mimeIndex) ?: "application/octet-stream"
                if (mime == DocumentsContract.Document.MIME_TYPE_DIR) {
                    if ((depth == 0 || settings.includeSubdirectories) &&
                        name != OUTPUT_FOLDER && (!settings.skipHidden || !name.startsWith("."))
                    ) {
                        scanChildren(context, treeUri, documentId, results, settings, depth + 1)
                    }
                    continue
                }
                if (name.contains(".videoshrink.pending.")) continue
                if (settings.skipHidden && name.startsWith(".")) continue
                val extension = name.substringAfterLast('.', "").lowercase()
                if (mime.startsWith("video/") || extension in videoExtensions) {
                    results += VideoDocument(
                        uri = DocumentsContract.buildDocumentUriUsingTree(treeUri, documentId),
                        parentUri = parentUri,
                        name = name,
                        mimeType = mime,
                        size = if (cursor.isNull(sizeIndex)) 0L else cursor.getLong(sizeIndex),
                        lastModified = if (modifiedIndex < 0 || cursor.isNull(modifiedIndex)) 0L else cursor.getLong(modifiedIndex),
                    )
                }
            }
        }
    }

    const val OUTPUT_FOLDER = "VideoShrink"
}
