package com.kiwivtuber.mobile;

import android.app.Activity;
import android.content.ContentResolver;
import android.content.Intent;
import android.database.Cursor;
import android.net.Uri;
import android.os.Bundle;
import android.provider.OpenableColumns;

import com.unity3d.player.UnityPlayer;

import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.util.Locale;

public final class KiwiFilePickerActivity extends Activity {
    private static final int REQUEST_OPEN_VRM = 41021;
    private static final int BUFFER_SIZE = 256 * 1024;
    private static final int MAX_FILE_NAME_LENGTH = 120;

    private String gameObjectName;
    private String successMethod;
    private String errorMethod;
    private long maximumBytes;
    private String destinationDirectory;
    private boolean pickerOpened;
    private volatile boolean completed;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        Intent launch = getIntent();
        gameObjectName = launch.getStringExtra("unityGameObject");
        successMethod = launch.getStringExtra("unitySuccessMethod");
        errorMethod = launch.getStringExtra("unityErrorMethod");
        destinationDirectory = launch.getStringExtra("destinationDirectory");
        maximumBytes = Math.max(0L, launch.getLongExtra("maximumBytes", 0L));

        if (savedInstanceState == null) {
            openPicker();
        } else {
            pickerOpened = true;
        }
    }

    private void openPicker() {
        if (pickerOpened) return;
        pickerOpened = true;

        try {
            Intent intent = new Intent(Intent.ACTION_OPEN_DOCUMENT);
            intent.addCategory(Intent.CATEGORY_OPENABLE);
            intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            intent.setType("*/*");
            startActivityForResult(intent, REQUEST_OPEN_VRM);
        } catch (Exception ex) {
            completeError("Unable to open Android document picker: " + ex.getMessage());
        }
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);

        if (requestCode != REQUEST_OPEN_VRM || completed) return;

        if (resultCode != RESULT_OK || data == null || data.getData() == null) {
            completeError("CANCELLED");
            return;
        }

        final Uri uri = data.getData();
        Thread worker = new Thread(() -> importSelectedUri(uri), "KiwiVrmImport");
        worker.setPriority(Thread.NORM_PRIORITY - 1);
        worker.start();
    }

    private void importSelectedUri(Uri uri) {
        File output = null;
        try {
            DocumentInfo info = queryDocumentInfo(uri);
            String name = info.name;
            if (name == null || name.trim().isEmpty()) name = "avatar.vrm";
            if (!name.toLowerCase(Locale.ROOT).endsWith(".vrm")) {
                completeError("Selected file is not a .vrm file.");
                return;
            }

            if (maximumBytes > 0 && info.size >= 0 && info.size > maximumBytes) {
                completeError("Selected VRM exceeds the configured runtime model size limit.");
                return;
            }

            File modelsDir = resolveManagedDestination(destinationDirectory);
            if (!modelsDir.exists() && !modelsDir.mkdirs()) {
                throw new IllegalStateException("Unable to create the managed Models directory.");
            }

            name = sanitize(name);
            output = createUniqueFile(modelsDir, name);
            copyUriToFile(uri, output, maximumBytes);
            completeSuccess(output.getAbsolutePath());
        } catch (Exception ex) {
            if (output != null && output.exists()) {
                //noinspection ResultOfMethodCallIgnored
                output.delete();
            }
            completeError("VRM import failed: " + safeMessage(ex));
        }
    }

    private File resolveManagedDestination(String requestedPath) throws Exception {
        if (requestedPath == null || requestedPath.trim().isEmpty()) {
            throw new IllegalStateException("Managed Models directory was not provided.");
        }

        File requested = new File(requestedPath).getCanonicalFile();
        File internalRoot = getFilesDir().getCanonicalFile();
        File external = getExternalFilesDir(null);
        File externalRoot = external != null ? external.getCanonicalFile() : null;

        if (isInside(requested, internalRoot) ||
            (externalRoot != null && isInside(requested, externalRoot))) {
            return requested;
        }

        throw new SecurityException("Managed Models directory is outside the app storage sandbox.");
    }

    private static boolean isInside(File target, File root) {
        String targetPath = target.getPath();
        String rootPath = root.getPath();
        return targetPath.equals(rootPath) || targetPath.startsWith(rootPath + File.separator);
    }

    private DocumentInfo queryDocumentInfo(Uri uri) {
        ContentResolver resolver = getContentResolver();
        Cursor cursor = null;
        String name = null;
        long size = -1L;
        try {
            cursor = resolver.query(uri, null, null, null, null);
            if (cursor != null && cursor.moveToFirst()) {
                int nameIndex = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME);
                if (nameIndex >= 0) name = cursor.getString(nameIndex);
                int sizeIndex = cursor.getColumnIndex(OpenableColumns.SIZE);
                if (sizeIndex >= 0 && !cursor.isNull(sizeIndex)) size = cursor.getLong(sizeIndex);
            }
        } finally {
            if (cursor != null) cursor.close();
        }
        return new DocumentInfo(name, size);
    }

    private void copyUriToFile(Uri uri, File output, long limit) throws Exception {
        long total = 0L;
        try (InputStream input = getContentResolver().openInputStream(uri);
             FileOutputStream stream = new FileOutputStream(output, false)) {
            if (input == null) throw new IllegalStateException("Unable to read selected document.");
            byte[] buffer = new byte[BUFFER_SIZE];
            int count;
            while ((count = input.read(buffer)) > 0) {
                total += count;
                if (limit > 0 && total > limit) {
                    throw new IllegalStateException("Selected VRM exceeds the configured runtime model size limit.");
                }
                stream.write(buffer, 0, count);
            }
            stream.getFD().sync();
        }

        if (total <= 0) {
            throw new IllegalStateException("Selected VRM is empty.");
        }
    }

    private static File createUniqueFile(File directory, String name) throws Exception {
        String safeName = sanitize(name);
        File candidate = new File(directory, safeName);
        if (!candidate.exists()) return candidate;

        String lower = safeName.toLowerCase(Locale.ROOT);
        String extension = lower.endsWith(".vrm") ? ".vrm" : "";
        String stem = extension.isEmpty() ? safeName : safeName.substring(0, safeName.length() - extension.length());
        for (int i = 2; i < 10000; i++) {
            candidate = new File(directory, stem + "_" + i + extension);
            if (!candidate.exists()) return candidate;
        }
        return new File(directory, stem + "_" + System.nanoTime() + extension);
    }

    private static String sanitize(String name) {
        String result = name == null ? "avatar.vrm" : name.replaceAll("[\\\\/:*?\"<>|]", "_").trim();
        if (result.isEmpty()) result = "avatar.vrm";
        if (result.length() > MAX_FILE_NAME_LENGTH) {
            String lower = result.toLowerCase(Locale.ROOT);
            String extension = lower.endsWith(".vrm") ? ".vrm" : "";
            int maxStem = Math.max(1, MAX_FILE_NAME_LENGTH - extension.length());
            String stem = extension.isEmpty() ? result : result.substring(0, result.length() - extension.length());
            result = stem.substring(0, Math.min(stem.length(), maxStem)) + extension;
        }
        return result;
    }

    private void completeSuccess(final String path) {
        if (completed) return;
        completed = true;
        runOnUiThread(() -> {
            sendSuccess(path);
            finish();
        });
    }

    private void completeError(final String message) {
        if (completed) return;
        completed = true;
        runOnUiThread(() -> {
            sendError(message);
            finish();
        });
    }

    private void sendSuccess(String path) {
        if (gameObjectName != null && successMethod != null) {
            UnityPlayer.UnitySendMessage(gameObjectName, successMethod, path == null ? "" : path);
        }
    }

    private void sendError(String message) {
        if (gameObjectName != null && errorMethod != null) {
            UnityPlayer.UnitySendMessage(gameObjectName, errorMethod, message == null ? "ERROR" : message);
        }
    }

    private static String safeMessage(Exception ex) {
        if (ex == null) return "Unknown error";
        String message = ex.getMessage();
        return message == null || message.trim().isEmpty() ? ex.getClass().getSimpleName() : message;
    }

    private static final class DocumentInfo {
        final String name;
        final long size;

        DocumentInfo(String name, long size) {
            this.name = name;
            this.size = size;
        }
    }
}
