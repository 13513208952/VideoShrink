package com.videoshrink.android

import android.content.Context

data class AppSettings(
    val skipProcessed: Boolean = true,
    val skipGenerated: Boolean = true,
    val strictMatching: Boolean = true,
    val includeSubdirectories: Boolean = true,
    val skipHidden: Boolean = true,
    val keepScreenOn: Boolean = true,
)

object AppSettingsStore {
    private const val PREFERENCES = "videoshrink_settings"

    fun load(context: Context): AppSettings {
        val values = context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
        return AppSettings(
            skipProcessed = values.getBoolean("skipProcessed", true),
            skipGenerated = values.getBoolean("skipGenerated", true),
            strictMatching = values.getBoolean("strictMatching", true),
            includeSubdirectories = values.getBoolean("includeSubdirectories", true),
            skipHidden = values.getBoolean("skipHidden", true),
            keepScreenOn = values.getBoolean("keepScreenOn", true),
        )
    }

    fun update(context: Context, values: Map<*, *>): AppSettings {
        val editor = context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE).edit()
        listOf(
            "skipProcessed", "skipGenerated", "strictMatching",
            "includeSubdirectories", "skipHidden", "keepScreenOn",
        ).forEach { key -> (values[key] as? Boolean)?.let { editor.putBoolean(key, it) } }
        editor.apply()
        return load(context)
    }

    fun asMap(settings: AppSettings): Map<String, Any> = mapOf(
        "skipProcessed" to settings.skipProcessed,
        "skipGenerated" to settings.skipGenerated,
        "strictMatching" to settings.strictMatching,
        "includeSubdirectories" to settings.includeSubdirectories,
        "skipHidden" to settings.skipHidden,
        "keepScreenOn" to settings.keepScreenOn,
    )
}
