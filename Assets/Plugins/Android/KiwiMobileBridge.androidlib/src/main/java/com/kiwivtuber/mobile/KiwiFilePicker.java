package com.kiwivtuber.mobile;

import android.app.Activity;
import android.content.Intent;
import com.unity3d.player.UnityPlayer;

import java.io.File;

public final class KiwiFilePicker {
    private KiwiFilePicker() {}

    public static void open(
            String gameObjectName,
            String successMethod,
            String errorMethod,
            String destinationDirectory,
            long maximumBytes) {
        Activity activity = UnityPlayer.currentActivity;
        if (activity == null) {
            UnityPlayer.UnitySendMessage(gameObjectName, errorMethod, "Android Activity is unavailable.");
            return;
        }

        Intent intent = new Intent(activity, KiwiFilePickerActivity.class);
        intent.putExtra("unityGameObject", gameObjectName);
        intent.putExtra("unitySuccessMethod", successMethod);
        intent.putExtra("unityErrorMethod", errorMethod);
        intent.putExtra("destinationDirectory", destinationDirectory);
        intent.putExtra("maximumBytes", Math.max(0L, maximumBytes));
        activity.startActivity(intent);
    }

    public static void cleanup(String path) {
        Activity activity = UnityPlayer.currentActivity;
        if (activity == null || path == null || path.trim().isEmpty()) return;

        try {
            File root = new File(activity.getCacheDir(), "KiwiImports").getCanonicalFile();
            File target = new File(path).getCanonicalFile();
            String rootPrefix = root.getPath() + File.separator;
            if (target.getPath().startsWith(rootPrefix) && target.isFile()) {
                //noinspection ResultOfMethodCallIgnored
                target.delete();
            }
        } catch (Exception ignored) {
        }
    }
}
