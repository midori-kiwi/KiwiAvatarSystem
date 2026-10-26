using System;
using System.IO;
using UnityEngine;

public static class KiwiAvatarStorage
{
    private const string MobileRootFolder = "KiwiAvatarSystem";

    public static bool IsMobileRuntime
    {
        get
        {
#if UNITY_ANDROID || UNITY_IOS
            return !Application.isEditor;
#else
            return false;
#endif
        }
    }

    public static string RootDirectory
    {
        get
        {
#if UNITY_ANDROID || UNITY_IOS
            if (!Application.isEditor)
            {
                return Path.Combine(Application.persistentDataPath, MobileRootFolder);
            }
#endif
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return root;
        }
    }

    public static string ModelsDirectory => Path.Combine(RootDirectory, "Models");
    public static string ProfilesDirectory => Path.Combine(ModelsDirectory, "Profiles");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(ProfilesDirectory);
    }

    public static int NormalizeExistingModelFileNames()
    {
        EnsureDirectories();
        int renamed = 0;

        foreach (string path in Directory.EnumerateFiles(
            ModelsDirectory,
            "*",
            SearchOption.TopDirectoryOnly))
        {
            if (!string.Equals(
                Path.GetExtension(path),
                ".vrm",
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string cleanName = NormalizeDuplicateSuffixes(
                SanitizeFileName(Path.GetFileName(path))
            );
            string requested = Path.Combine(ModelsDirectory, cleanName);

            if (string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(requested),
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string destination = MakeUniqueDestination(requested);
            File.Move(path, destination);
            renamed++;
        }

        return renamed;
    }

    public static string ImportExternalFile(string sourcePath, long maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Selected VRM file was not found.", sourcePath);
        }

        if (!string.Equals(Path.GetExtension(sourcePath), ".vrm", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Only .vrm files are supported.");
        }

        FileInfo sourceInfo = new FileInfo(sourcePath);
        if (sourceInfo.Length <= 0)
        {
            throw new InvalidDataException("Selected VRM file is empty.");
        }

        if (maximumBytes > 0 && sourceInfo.Length > maximumBytes)
        {
            throw new InvalidDataException(
                "Selected VRM exceeds the configured runtime model size limit."
            );
        }

        EnsureDirectories();

        string rawName = Path.GetFileName(sourcePath);
        string safeName = NormalizeDuplicateSuffixes(
            SanitizeFileName(rawName)
        );
        if (!safeName.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase))
        {
            safeName += ".vrm";
        }

        string sourceFull = Path.GetFullPath(sourcePath);
        string destinationFull = Path.GetFullPath(Path.Combine(ModelsDirectory, safeName));

        if (string.Equals(sourceFull, destinationFull, StringComparison.OrdinalIgnoreCase))
        {
            using (FileStream existing = File.OpenRead(sourceFull))
            {
                ValidateVrmGlbHeader(existing);
            }
            return destinationFull;
        }

        destinationFull = MakeUniqueDestination(destinationFull);
        // An existing .importing file may belong to another operation.
        // CreateNew is the only way this invocation acquires a temporary leaf.
        string temp = destinationFull + "." + Guid.NewGuid().ToString("N") + ".importing";
        bool tempOwned = false;
        try
        {
            using (FileStream input = File.OpenRead(sourceFull))
            {
                if (input.Length <= 0)
                    throw new InvalidDataException("Selected VRM file is empty.");
                if (maximumBytes > 0 && input.Length > maximumBytes)
                    throw new InvalidDataException(
                        "Selected VRM exceeds the configured runtime model size limit.");

                ValidateVrmGlbHeader(input);

                using (FileStream output = new FileStream(
                    temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    tempOwned = true;
                    byte[] buffer = new byte[1024 * 1024];
                    long copied = 0;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        long nextLength = checked(copied + read);
                        if (maximumBytes > 0 && nextLength > maximumBytes)
                            throw new InvalidDataException(
                                "Selected VRM exceeds the configured runtime model size limit.");
                        output.Write(buffer, 0, read);
                        copied = nextLength;
                    }
                    if (copied <= 0)
                        throw new InvalidDataException("Selected VRM file is empty.");
                    output.Flush(true);
                }
            }

            // The two-argument File.Move rejects an existing destination.
            File.Move(temp, destinationFull);
            tempOwned = false;
            return destinationFull;
        }
        finally
        {
            if (tempOwned)
            {
                // Preserve the original import failure if cleanup also fails.
                try { File.Delete(temp); } catch { }
            }
        }
    }

    // VRM 0.x and 1.0 use the GLB 2.0 container. This is a cheap
    // pre-publication container check, not VRM JSON/mesh validation.
    private static void ValidateVrmGlbHeader(FileStream input)
    {
        const int headerSize = 20; // GLB header and first JSON chunk header.
        if (input.Length < headerSize)
            throw new InvalidDataException("Selected VRM has an invalid GLB header.");

        byte[] header = new byte[headerSize];
        int offset = 0;
        while (offset < header.Length)
        {
            int count = input.Read(header, offset, header.Length - offset);
            if (count == 0)
                throw new InvalidDataException("Selected VRM has a truncated GLB header.");
            offset += count;
        }

        uint version = ReadGlbUInt32(header, 4);
        uint totalLength = ReadGlbUInt32(header, 8);
        uint jsonLength = ReadGlbUInt32(header, 12);
        uint jsonType = ReadGlbUInt32(header, 16);
        if (header[0] != (byte)'g' || header[1] != (byte)'l' ||
            header[2] != (byte)'T' || header[3] != (byte)'F' ||
            version != 2 || totalLength != input.Length ||
            jsonType != 0x4E4F534A || jsonLength == 0 ||
            (jsonLength & 3) != 0 || jsonLength > input.Length - headerSize)
        {
            throw new InvalidDataException("Selected VRM is not a valid GLB 2.0 container header.");
        }

        input.Position = 0;
    }

    private static uint ReadGlbUInt32(byte[] header, int offset)
    {
        return (uint)header[offset] |
               ((uint)header[offset + 1] << 8) |
               ((uint)header[offset + 2] << 16) |
               ((uint)header[offset + 3] << 24);
    }

    private static string MakeUniqueDestination(string requestedPath)
    {
        string directory = Path.GetDirectoryName(requestedPath);
        string stem = Path.GetFileNameWithoutExtension(requestedPath);
        string extension = Path.GetExtension(requestedPath);

        // If a clean suffix previously generated by this importer comes back as
        // an input name, continue the original sequence instead of producing
        // names such as Avatar_2_2.vrm.
        int separator = stem.LastIndexOf('_');
        if (
            separator > 0 &&
            IsNumericSuffix(stem, separator + 1) &&
            File.Exists(
                Path.Combine(directory, stem.Substring(0, separator) + extension)
            )
        )
        {
            stem = stem.Substring(0, separator);
            requestedPath = Path.Combine(directory, stem + extension);
        }

        if (!File.Exists(requestedPath))
        {
            return requestedPath;
        }

        // Never create Windows-style duplicate names such as "Avatar (1).vrm".
        // Use a clean numeric suffix instead so file names stay predictable.
        for (int i = 2; i < 10000; i++)
        {
            string candidate = Path.Combine(directory, stem + "_" + i + extension);
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, stem + "_" + DateTime.UtcNow.Ticks + extension);
    }

    public static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "avatar.vrm";
        }

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalid, '_');
        }

        fileName = fileName.Trim();
        if (string.IsNullOrEmpty(fileName))
        {
            return "avatar.vrm";
        }

        const int MaxFileNameLength = 120;
        if (fileName.Length > MaxFileNameLength)
        {
            string extension = Path.GetExtension(fileName);
            string stem = Path.GetFileNameWithoutExtension(fileName);
            int maxStem = Mathf.Max(1, MaxFileNameLength - extension.Length);
            fileName = stem.Substring(0, Mathf.Min(stem.Length, maxStem)) + extension;
        }

        return fileName;
    }

    public static string NormalizeDuplicateSuffixes(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "avatar.vrm";
        }

        string extension = Path.GetExtension(fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName).Trim();
        bool changed;

        do
        {
            changed = false;

            if (TryTrimSuffix(ref stem, " - Copy"))
            {
                changed = true;
                continue;
            }

            if (TryTrimSuffix(ref stem, " Copy"))
            {
                changed = true;
                continue;
            }

            if (TryTrimSuffix(ref stem, "_copy"))
            {
                changed = true;
                continue;
            }

            int open = stem.LastIndexOf(" (", StringComparison.Ordinal);
            if (
                open >= 0 &&
                stem.EndsWith(")", StringComparison.Ordinal) &&
                IsNumericSuffix(stem, open + 2, stem.Length - open - 3)
            )
            {
                stem = stem.Substring(0, open).TrimEnd();
                changed = true;
            }
        }
        while (changed && stem.Length > 0);

        if (stem.Length == 0)
        {
            stem = "avatar";
        }

        return stem + extension;
    }

    private static bool TryTrimSuffix(ref string value, string suffix)
    {
        if (!value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        value = value.Substring(0, value.Length - suffix.Length).TrimEnd();
        return true;
    }

    private static bool IsNumericSuffix(string value, int start)
    {
        return IsNumericSuffix(value, start, value.Length - start);
    }

    private static bool IsNumericSuffix(string value, int start, int length)
    {
        if (length <= 0 || start < 0 || start + length > value.Length)
        {
            return false;
        }

        for (int i = start; i < start + length; i++)
        {
            char character = value[i];
            if (character < '0' || character > '9')
            {
                return false;
            }

        }

        return true;
    }

    public static bool IsPathInsideDirectory(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string prefix = fullDirectory + Path.DirectorySeparatorChar;

        StringComparison comparison =
            Application.platform == RuntimePlatform.WindowsPlayer ||
            Application.platform == RuntimePlatform.WindowsEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        return fullPath.StartsWith(prefix, comparison);
    }
}
