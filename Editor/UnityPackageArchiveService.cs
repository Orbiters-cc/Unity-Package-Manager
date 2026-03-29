using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace Orbiters.UnityPackageManager.Editor
{
    [InitializeOnLoad]
    internal static class UnityPackageManagerApiRegistration
    {
        static UnityPackageManagerApiRegistration()
        {
            UnityPackageManagerApi.ReaderFactory = () => new UnityPackageArchiveService();
            UnityPackageManagerApi.ExtractorFactory = () => new UnityPackageArchiveService();
        }
    }

    internal sealed class UnityPackageArchiveService : IUnityPackageReader, IUnityPackageExtractor
    {
        public UnityPackageArchiveInfo ReadArchive(string unityPackageFilePath)
        {
            return ReadEditableArchive(unityPackageFilePath).ToArchiveInfo();
        }

        public EditableUnityPackageArchive ReadEditableArchive(string unityPackageFilePath)
        {
            var fullPath = ValidateUnityPackageFile(unityPackageFilePath);
            var fileInfo = new FileInfo(fullPath);
            var entryByGuid = new Dictionary<string, EditableUnityPackageEntry>(StringComparer.OrdinalIgnoreCase);

            using (var fileStream = File.OpenRead(fullPath))
            using (var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress))
            {
                TarArchiveReader.IterateEntries(gzipStream, (entryName, size, dataStream) =>
                {
                    var packageGuid = TarArchiveReader.GetTopLevelDirectory(entryName);
                    if (string.IsNullOrEmpty(packageGuid))
                    {
                        TarArchiveReader.Skip(dataStream, size);
                        return;
                    }

                    if (!entryByGuid.TryGetValue(packageGuid, out var entry))
                    {
                        entry = new EditableUnityPackageEntry { PackageGuid = packageGuid };
                        entryByGuid.Add(packageGuid, entry);
                    }

                    switch (TarArchiveReader.GetEntrySuffix(entryName))
                    {
                        case "pathname":
                            entry.OriginalAssetPath = NormalizeArchivePath(TarArchiveReader.ReadUtf8String(dataStream, size));
                            break;
                        case "asset":
                            entry.AssetBytes = TarArchiveReader.ReadBytes(dataStream, size);
                            break;
                        case "asset.meta":
                            entry.MetaBytes = TarArchiveReader.ReadBytes(dataStream, size);
                            break;
                        case "preview.png":
                            entry.PreviewBytes = TarArchiveReader.ReadBytes(dataStream, size);
                            break;
                        default:
                            TarArchiveReader.Skip(dataStream, size);
                            break;
                    }
                });
            }

            var entries = entryByGuid.Values
                .Where(entry => !string.IsNullOrWhiteSpace(entry.OriginalAssetPath))
                .OrderBy(entry => entry.OriginalAssetPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new EditableUnityPackageArchive
            {
                PackageFilePath = fullPath,
                PackageFileSizeBytes = fileInfo.Length,
                LastWriteTimeUtc = fileInfo.LastWriteTimeUtc,
                Entries = entries
            };
        }

        public IReadOnlyList<string> ExtractAssets(
            string unityPackageFilePath,
            IEnumerable<string> originalAssetPaths,
            string destinationFolderPath,
            UnityPackageImportOptions options)
        {
            var fullPath = ValidateUnityPackageFile(unityPackageFilePath);
            var normalizedDestinationFolder = NormalizeProjectPath(destinationFolderPath, requireExistingFolder: false);
            EnsureDirectoryExists(normalizedDestinationFolder);

            var resolvedOptions = options ?? new UnityPackageImportOptions();
            var editableArchive = ReadEditableArchive(fullPath);
            var selectedPaths = new HashSet<string>(
                (originalAssetPaths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizeArchivePath),
                StringComparer.OrdinalIgnoreCase);

            if (selectedPaths.Count == 0)
            {
                return Array.Empty<string>();
            }

            var selectedEntries = editableArchive.Entries
                .Where(entry => selectedPaths.Contains(entry.OriginalAssetPath))
                .ToList();

            if (selectedEntries.Count == 0)
            {
                return Array.Empty<string>();
            }

            var outputPaths = new List<string>(selectedEntries.Count);
            foreach (var entry in selectedEntries)
            {
                var relativePath = resolvedOptions.PreservePackageHierarchy
                    ? GetRelativeImportPath(entry.OriginalAssetPath)
                    : entry.AssetName;

                var destinationAssetPath = CombineProjectPath(normalizedDestinationFolder, relativePath);
                if (!resolvedOptions.OverwriteExistingFiles)
                {
                    destinationAssetPath = AssetDatabase.GenerateUniqueAssetPath(destinationAssetPath);
                }

                outputPaths.Add(destinationAssetPath);
                WriteEntryToProject(entry, destinationAssetPath);
            }

            AssetDatabase.Refresh();
            return outputPaths;
        }

        public EditableUnityPackageEntry CreateEntryFromFile(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("A source file path is required.", nameof(sourcePath));
            }

            var fullPath = Path.GetFullPath(sourcePath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("The source file could not be found.", fullPath);
            }

            if (fullPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Meta files should not be added directly.");
            }

            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var normalizedSource = fullPath.Replace('\\', '/');
            var normalizedProjectRoot = projectRoot.Replace('\\', '/');
            var isInsideProject = normalizedSource.StartsWith(normalizedProjectRoot, StringComparison.OrdinalIgnoreCase);
            var originalAssetPath = isInsideProject
                ? normalizedSource.Substring(normalizedProjectRoot.Length).TrimStart('/')
                : "Assets/" + Path.GetFileName(fullPath);

            var metaPath = fullPath + ".meta";
            byte[] metaBytes = null;
            string packageGuid = null;

            if (File.Exists(metaPath))
            {
                metaBytes = File.ReadAllBytes(metaPath);
                packageGuid = ExtractGuidFromMeta(metaBytes);
            }

            if (string.IsNullOrWhiteSpace(packageGuid))
            {
                packageGuid = Guid.NewGuid().ToString("N");
                metaBytes = Encoding.UTF8.GetBytes(BuildMinimalMeta(packageGuid));
            }

            return new EditableUnityPackageEntry
            {
                PackageGuid = packageGuid,
                OriginalAssetPath = NormalizeArchivePath(originalAssetPath),
                AssetBytes = File.ReadAllBytes(fullPath),
                MetaBytes = metaBytes
            };
        }

        public void SaveArchive(string outputPath, IEnumerable<EditableUnityPackageEntry> entries)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("An output file path is required.", nameof(outputPath));
            }

            var fullOutputPath = Path.GetFullPath(outputPath);
            if (!fullOutputPath.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Output file must use the .unitypackage extension.");
            }

            var outputDirectory = Path.GetDirectoryName(fullOutputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            var filteredEntries = (entries ?? Enumerable.Empty<EditableUnityPackageEntry>())
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.OriginalAssetPath) && entry.AssetBytes != null)
                .OrderBy(entry => entry.OriginalAssetPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            using (var fileStream = File.Create(fullOutputPath))
            using (var gzipStream = new GZipStream(fileStream, (CompressionLevel)CompressionLevel.Optimal))
            {
                foreach (var entry in filteredEntries)
                {
                    var packageGuid = string.IsNullOrWhiteSpace(entry.PackageGuid)
                        ? Guid.NewGuid().ToString("N")
                        : entry.PackageGuid;

                    TarArchiveWriter.WriteFile(gzipStream, packageGuid + "/pathname", Encoding.UTF8.GetBytes(NormalizeArchivePath(entry.OriginalAssetPath)));
                    TarArchiveWriter.WriteFile(gzipStream, packageGuid + "/asset", entry.AssetBytes);

                    if (entry.MetaBytes != null && entry.MetaBytes.Length > 0)
                    {
                        TarArchiveWriter.WriteFile(gzipStream, packageGuid + "/asset.meta", entry.MetaBytes);
                    }

                    if (entry.PreviewBytes != null && entry.PreviewBytes.Length > 0)
                    {
                        TarArchiveWriter.WriteFile(gzipStream, packageGuid + "/preview.png", entry.PreviewBytes);
                    }
                }

                TarArchiveWriter.WriteEndOfArchive(gzipStream);
            }
        }

        private static string GetRelativeImportPath(string originalAssetPath)
        {
            var normalized = NormalizeArchivePath(originalAssetPath);

            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                return normalized.Substring("Assets/".Length);
            }

            if (normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                var segments = normalized.Split('/');
                if (segments.Length > 2)
                {
                    return string.Join("/", segments.Skip(2));
                }
            }

            return Path.GetFileName(normalized);
        }

        private static void WriteEntryToProject(EditableUnityPackageEntry entry, string destinationAssetPath)
        {
            WriteBytesToProjectFile(destinationAssetPath, entry.AssetBytes);

            if (entry.MetaBytes != null && entry.MetaBytes.Length > 0)
            {
                WriteBytesToProjectFile(destinationAssetPath + ".meta", entry.MetaBytes);
            }
        }

        private static void WriteBytesToProjectFile(string projectRelativePath, byte[] bytes)
        {
            if (bytes == null)
            {
                return;
            }

            var absolutePath = ProjectRelativeToAbsolute(projectRelativePath);
            var directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(absolutePath, bytes);
        }

        private static string ExtractGuidFromMeta(byte[] metaBytes)
        {
            if (metaBytes == null || metaBytes.Length == 0)
            {
                return null;
            }

            var text = Encoding.UTF8.GetString(metaBytes);
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (!line.StartsWith("guid:", StringComparison.Ordinal))
                {
                    continue;
                }

                var guid = line.Substring("guid:".Length).Trim();
                return string.IsNullOrWhiteSpace(guid) ? null : guid;
            }

            return null;
        }

        private static string BuildMinimalMeta(string guid)
        {
            return "fileFormatVersion: 2\n" +
                   "guid: " + guid + "\n";
        }

        private static string ValidateUnityPackageFile(string unityPackageFilePath)
        {
            if (string.IsNullOrWhiteSpace(unityPackageFilePath))
            {
                throw new ArgumentException("A .unitypackage file path is required.", nameof(unityPackageFilePath));
            }

            var fullPath = Path.GetFullPath(unityPackageFilePath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("The .unitypackage file could not be found.", fullPath);
            }

            if (!fullPath.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The selected file is not a .unitypackage archive.");
            }

            return fullPath;
        }

        internal static string NormalizeProjectPath(string projectPath, bool requireExistingFolder)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                throw new ArgumentException("A destination folder path is required.", nameof(projectPath));
            }

            var normalized = projectPath.Replace('\\', '/').Trim();
            if (Path.IsPathRooted(normalized))
            {
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/');
                var absolute = Path.GetFullPath(normalized).Replace('\\', '/');
                if (!absolute.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Destination must be inside the current Unity project.");
                }

                normalized = absolute.Substring(projectRoot.Length).TrimStart('/');
            }

            if (!normalized.StartsWith("Assets", StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith("Packages", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Destination must be under Assets or Packages.");
            }

            if (requireExistingFolder && !AssetDatabase.IsValidFolder(normalized))
            {
                throw new DirectoryNotFoundException($"Destination folder does not exist: {normalized}");
            }

            return normalized;
        }

        internal static string NormalizeArchivePath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').TrimStart('/');
        }

        private static void EnsureDirectoryExists(string projectRelativeFolder)
        {
            Directory.CreateDirectory(ProjectRelativeToAbsolute(projectRelativeFolder));
        }

        private static string ProjectRelativeToAbsolute(string projectRelativePath)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(projectRoot, projectRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static string CombineProjectPath(string folderPath, string relativePath)
        {
            return (folderPath.TrimEnd('/') + "/" + relativePath.TrimStart('/')).Replace('\\', '/');
        }
    }

    internal sealed class EditableUnityPackageArchive
    {
        public string PackageFilePath;
        public long PackageFileSizeBytes;
        public DateTime LastWriteTimeUtc;
        public List<EditableUnityPackageEntry> Entries = new List<EditableUnityPackageEntry>();

        public UnityPackageArchiveInfo ToArchiveInfo()
        {
            return new UnityPackageArchiveInfo
            {
                PackageFilePath = PackageFilePath,
                PackageFileSizeBytes = PackageFileSizeBytes,
                LastWriteTimeUtc = LastWriteTimeUtc,
                Assets = Entries
                    .Select(entry => entry.ToAssetInfo())
                    .OrderBy(asset => asset.OriginalAssetPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
        }
    }

    internal sealed class EditableUnityPackageEntry
    {
        public string PackageGuid;
        public string OriginalAssetPath;
        public byte[] AssetBytes;
        public byte[] MetaBytes;
        public byte[] PreviewBytes;

        public string AssetName => Path.GetFileName(OriginalAssetPath ?? string.Empty);
        public string DirectoryPath => Path.GetDirectoryName(OriginalAssetPath ?? string.Empty)?.Replace('\\', '/') ?? string.Empty;
        public string FileExtension => Path.GetExtension(AssetName);

        public UnityPackageAssetInfo ToAssetInfo()
        {
            return new UnityPackageAssetInfo
            {
                PackageGuid = PackageGuid,
                OriginalAssetPath = OriginalAssetPath,
                AssetName = AssetName,
                DirectoryPath = DirectoryPath,
                FileExtension = FileExtension,
                AssetSizeBytes = AssetBytes?.LongLength ?? 0,
                MetaSizeBytes = MetaBytes?.LongLength ?? 0,
                PreviewSizeBytes = PreviewBytes?.LongLength ?? 0,
                HasAssetPayload = AssetBytes != null && AssetBytes.Length > 0,
                HasMetaFile = MetaBytes != null && MetaBytes.Length > 0,
                HasPreviewImage = PreviewBytes != null && PreviewBytes.Length > 0
            };
        }
    }

    internal static class TarArchiveReader
    {
        private const int TarBlockSize = 512;

        public static void IterateEntries(Stream stream, Action<string, long, Stream> handler)
        {
            var header = new byte[TarBlockSize];

            while (ReadExactly(stream, header, TarBlockSize))
            {
                if (IsAllZeros(header))
                {
                    break;
                }

                var entryName = ReadTarString(header, 0, 100);
                var prefix = ReadTarString(header, 345, 155);
                if (!string.IsNullOrEmpty(prefix))
                {
                    entryName = prefix + "/" + entryName;
                }

                var size = ReadOctal(header, 124, 12);
                handler(entryName, size, stream);
                SkipPadding(stream, size);
            }
        }

        public static string GetTopLevelDirectory(string entryName)
        {
            if (string.IsNullOrWhiteSpace(entryName))
            {
                return null;
            }

            var normalized = entryName.Replace('\\', '/');
            var slashIndex = normalized.IndexOf('/');
            return slashIndex > 0 ? normalized.Substring(0, slashIndex) : null;
        }

        public static string GetEntrySuffix(string entryName)
        {
            if (string.IsNullOrWhiteSpace(entryName))
            {
                return null;
            }

            var normalized = entryName.Replace('\\', '/');
            var slashIndex = normalized.IndexOf('/');
            return slashIndex >= 0 && slashIndex + 1 < normalized.Length
                ? normalized.Substring(slashIndex + 1)
                : normalized;
        }

        public static string ReadUtf8String(Stream stream, long size)
        {
            return Encoding.UTF8.GetString(ReadBytes(stream, size)).TrimEnd('\0', '\r', '\n');
        }

        public static byte[] ReadBytes(Stream stream, long size)
        {
            if (size > int.MaxValue)
            {
                throw new InvalidOperationException("Archive entry is too large to read into memory.");
            }

            var buffer = new byte[(int)size];
            CopyExactly(stream, buffer, size);
            return buffer;
        }

        public static void Skip(Stream stream, long size)
        {
            CopyExactly(stream, Stream.Null, size);
        }

        public static void CopyExactly(Stream input, Stream output, long size)
        {
            var buffer = new byte[81920];
            var remaining = size;

            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = input.Read(buffer, 0, toRead);
                if (read <= 0)
                {
                    throw new EndOfStreamException("Unexpected end of archive stream.");
                }

                output.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        public static void CopyExactly(Stream input, byte[] output, long size)
        {
            var remaining = (int)size;
            var offset = 0;

            while (remaining > 0)
            {
                var read = input.Read(output, offset, remaining);
                if (read <= 0)
                {
                    throw new EndOfStreamException("Unexpected end of archive stream.");
                }

                offset += read;
                remaining -= read;
            }
        }

        private static bool ReadExactly(Stream stream, byte[] buffer, int size)
        {
            var offset = 0;
            while (offset < size)
            {
                var read = stream.Read(buffer, offset, size - offset);
                if (read == 0)
                {
                    if (offset == 0)
                    {
                        return false;
                    }

                    throw new EndOfStreamException("Unexpected end of tar archive.");
                }

                offset += read;
            }

            return true;
        }

        private static void SkipPadding(Stream stream, long size)
        {
            var remainder = size % TarBlockSize;
            if (remainder == 0)
            {
                return;
            }

            Skip(stream, TarBlockSize - remainder);
        }

        private static bool IsAllZeros(byte[] buffer)
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] != 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static string ReadTarString(byte[] buffer, int offset, int length)
        {
            return Encoding.ASCII.GetString(buffer, offset, length).Trim('\0', ' ');
        }

        private static long ReadOctal(byte[] buffer, int offset, int length)
        {
            var value = ReadTarString(buffer, offset, length);
            if (string.IsNullOrWhiteSpace(value))
            {
                return 0;
            }

            return Convert.ToInt64(value, 8);
        }
    }

    internal static class TarArchiveWriter
    {
        private const int TarBlockSize = 512;

        public static void WriteFile(Stream stream, string entryName, byte[] data)
        {
            var payload = data ?? Array.Empty<byte>();
            var header = new byte[TarBlockSize];

            WriteString(header, 0, 100, entryName);
            WriteOctal(header, 100, 8, 420);
            WriteOctal(header, 108, 8, 0);
            WriteOctal(header, 116, 8, 0);
            WriteOctal(header, 124, 12, payload.LongLength);
            WriteOctal(header, 136, 12, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            for (var i = 148; i < 156; i++)
            {
                header[i] = 0x20;
            }

            header[156] = (byte)'0';
            WriteString(header, 257, 6, "ustar");
            WriteString(header, 263, 2, "00");

            var checksum = header.Sum(value => (int)value);
            WriteChecksum(header, 148, checksum);

            stream.Write(header, 0, header.Length);
            if (payload.Length > 0)
            {
                stream.Write(payload, 0, payload.Length);
                WritePadding(stream, payload.Length);
            }
        }

        public static void WriteEndOfArchive(Stream stream)
        {
            var emptyBlock = new byte[TarBlockSize];
            stream.Write(emptyBlock, 0, emptyBlock.Length);
            stream.Write(emptyBlock, 0, emptyBlock.Length);
        }

        private static void WritePadding(Stream stream, int length)
        {
            var remainder = length % TarBlockSize;
            if (remainder == 0)
            {
                return;
            }

            var padding = new byte[TarBlockSize - remainder];
            stream.Write(padding, 0, padding.Length);
        }

        private static void WriteString(byte[] buffer, int offset, int maxLength, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            var bytes = Encoding.ASCII.GetBytes(value);
            var count = Math.Min(bytes.Length, maxLength);
            Array.Copy(bytes, 0, buffer, offset, count);
        }

        private static void WriteOctal(byte[] buffer, int offset, int length, long value)
        {
            var octal = Convert.ToString(value, 8);
            octal = octal.Length >= length ? octal.Substring(octal.Length - (length - 1)) : octal.PadLeft(length - 1, '0');
            var bytes = Encoding.ASCII.GetBytes(octal);
            Array.Copy(bytes, 0, buffer, offset, bytes.Length);
            buffer[offset + length - 1] = 0;
        }

        private static void WriteChecksum(byte[] buffer, int offset, int checksum)
        {
            var text = Convert.ToString(checksum, 8).PadLeft(6, '0');
            var bytes = Encoding.ASCII.GetBytes(text);
            Array.Copy(bytes, 0, buffer, offset, bytes.Length);
            buffer[offset + 6] = 0;
            buffer[offset + 7] = (byte)' ';
        }
    }
}
