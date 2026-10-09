using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Orbiters.Toolkit.Editor;
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
        // What reading a package may cost: a small crafted file can declare or expand to far more than it holds, and
        // everything read here stays in memory. Orbiters Toolkit's reader checks the archive itself.
        internal const int MaxEntries = UnityPackageReader.DefaultMaxEntries;
        internal const int MaxPathnameBytes = UnityPackageIndex.MaxPathnameBytes;
        internal const long MaxExpandedBytes = UnityPackageReader.DefaultMaxExpandedBytes;

        private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

        public UnityPackageArchiveInfo ReadArchive(string unityPackageFilePath)
        {
            return ReadEditableArchive(unityPackageFilePath).ToArchiveInfo();
        }

        public EditableUnityPackageArchive ReadEditableArchive(string unityPackageFilePath)
        {
            return ReadEditableArchive(unityPackageFilePath, MaxExpandedBytes, MaxEntries);
        }

        internal EditableUnityPackageArchive ReadEditableArchive(string unityPackageFilePath, long maxExpandedBytes, int maxEntries)
        {
            var fullPath = ValidateUnityPackageFile(unityPackageFilePath);
            var fileInfo = new FileInfo(fullPath);
            var entryByGuid = new Dictionary<string, EditableUnityPackageEntry>(StringComparer.OrdinalIgnoreCase);

            // Unity's importer reads a pathname's first line: some exporters write more after it.
            var options = new UnityPackageReader.Options { MaxExpandedBytes = maxExpandedBytes, MaxEntries = maxEntries, FirstLinePathname = true };
            UnityPackageReader.Read(fullPath, options, record =>
            {
                if (!entryByGuid.TryGetValue(record.Guid, out var entry))
                {
                    entry = new EditableUnityPackageEntry { PackageGuid = record.Guid };
                    entryByGuid.Add(record.Guid, entry);
                }

                switch (record.Part)
                {
                    case UnityPackageReader.Part.Pathname:
                        entry.OriginalAssetPath = NormalizeArchivePath(record.Pathname.Trim());
                        break;
                    case UnityPackageReader.Part.Asset:
                        entry.AssetBytes = record.ReadAll();
                        break;
                    case UnityPackageReader.Part.Meta:
                        entry.MetaBytes = record.ReadAll();
                        break;
                    case UnityPackageReader.Part.Preview:
                        entry.PreviewBytes = record.ReadAll();
                        break;
                }
            });

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
            var outputPaths = ExtractAssets(unityPackageFilePath, originalAssetPaths, destinationFolderPath, options, out var refusedPaths);
            if (refusedPaths.Count > 0)
            {
                Debug.LogWarning("UnityPackageManager: " + DescribeRefusedEntries(refusedPaths, destinationFolderPath));
            }

            return outputPaths;
        }

        internal IReadOnlyList<string> ExtractAssets(
            string unityPackageFilePath,
            IEnumerable<string> originalAssetPaths,
            string destinationFolderPath,
            UnityPackageImportOptions options,
            out IReadOnlyList<string> refusedPaths)
        {
            var fullPath = ValidateUnityPackageFile(unityPackageFilePath);
            refusedPaths = Array.Empty<string>();
            var selectedPaths = new HashSet<string>(
                (originalAssetPaths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizeArchivePath),
                StringComparer.OrdinalIgnoreCase);

            if (selectedPaths.Count == 0)
            {
                return Array.Empty<string>();
            }

            var selectedEntries = ReadEditableArchive(fullPath).Entries
                .Where(entry => selectedPaths.Contains(entry.OriginalAssetPath))
                .ToList();

            return selectedEntries.Count == 0
                ? Array.Empty<string>()
                : ExtractEntries(selectedEntries, destinationFolderPath, options, out refusedPaths);
        }

        /// <summary>
        /// Writes entries under the destination folder. An entry whose pathname would land outside that folder is not
        /// written; its package path is returned in <paramref name="refusedPaths"/>.
        /// </summary>
        internal IReadOnlyList<string> ExtractEntries(
            IEnumerable<EditableUnityPackageEntry> entries,
            string destinationFolderPath,
            UnityPackageImportOptions options,
            out IReadOnlyList<string> refusedPaths)
        {
            var destinationFolder = NormalizeProjectPath(destinationFolderPath, requireExistingFolder: false);
            var destinationRoot = ProjectRelativeToAbsolute(destinationFolder);
            Directory.CreateDirectory(destinationRoot);

            var resolvedOptions = options ?? new UnityPackageImportOptions();
            var refused = new List<string>();
            var outputPaths = new List<string>();
            // Path order puts a folder record before its contents, so its .meta is written before files create the folder.
            foreach (var entry in (entries ?? Enumerable.Empty<EditableUnityPackageEntry>())
                         .Where(entry => entry != null)
                         .OrderBy(entry => entry.OriginalAssetPath, StringComparer.OrdinalIgnoreCase))
            {
                var relativePath = resolvedOptions.PreservePackageHierarchy
                    ? GetRelativeImportPath(entry.OriginalAssetPath)
                    : entry.AssetName;

                var destinationAssetPath = IsSafeRelativePath(relativePath)
                    ? CombineProjectPath(destinationFolder, relativePath)
                    : null;
                if (destinationAssetPath != null && !entry.IsFolder && !resolvedOptions.OverwriteExistingFiles)
                {
                    destinationAssetPath = GenerateUniqueProjectPath(destinationAssetPath);
                }

                if (destinationAssetPath == null ||
                    !TryGetContainedPath(destinationRoot, ProjectRelativeToAbsolute(destinationAssetPath), out var pathInDestination) ||
                    pathInDestination.Length == 0)
                {
                    refused.Add(entry.OriginalAssetPath);
                    continue;
                }

                if (WriteEntryToProject(entry, destinationAssetPath, resolvedOptions.OverwriteExistingFiles))
                {
                    outputPaths.Add(destinationAssetPath);
                }
            }

            refusedPaths = refused;
            AssetDatabase.Refresh();
            return outputPaths;
        }

        internal static string DescribeRefusedEntries(IReadOnlyCollection<string> refusedPaths, string destinationFolder)
        {
            const int listed = 10;
            var lines = refusedPaths.Take(listed).Select(path => "- " + path);
            var more = refusedPaths.Count > listed ? $"\n...and {refusedPaths.Count - listed} more" : string.Empty;
            return $"Skipped {refusedPaths.Count} package {(refusedPaths.Count == 1 ? "entry" : "entries")} whose path would be written outside {destinationFolder}:\n" +
                   string.Join("\n", lines) + more;
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

            var originalAssetPath = GetArchivePathForFile(GetProjectRoot(), fullPath);
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

            // Folder records have no asset payload, only their pathname and .meta: they are kept too.
            var filteredEntries = (entries ?? Enumerable.Empty<EditableUnityPackageEntry>())
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.OriginalAssetPath))
                .OrderBy(entry => entry.OriginalAssetPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Records are keyed by GUID: a second record with the same GUID would replace the first when read back.
            var sharedGuid = filteredEntries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.PackageGuid))
                .GroupBy(entry => entry.PackageGuid, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (sharedGuid != null)
            {
                throw new InvalidOperationException(
                    $"These entries share the GUID {sharedGuid.Key}, so only one of them would survive in the package. Remove all but one before saving:\n" +
                    string.Join("\n", sharedGuid.Select(entry => "- " + entry.OriginalAssetPath)));
            }

            var outputDirectory = Path.GetDirectoryName(fullOutputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            using (var fileStream = File.Create(fullOutputPath))
            using (var gzipStream = new GZipStream(fileStream, (CompressionLevel)CompressionLevel.Optimal))
            {
                foreach (var entry in filteredEntries)
                {
                    var packageGuid = string.IsNullOrWhiteSpace(entry.PackageGuid)
                        ? Guid.NewGuid().ToString("N")
                        : entry.PackageGuid;

                    TarArchiveWriter.WriteFile(gzipStream, packageGuid + "/pathname", Encoding.UTF8.GetBytes(NormalizeArchivePath(entry.OriginalAssetPath)));
                    if (!entry.IsFolder)
                    {
                        TarArchiveWriter.WriteFile(gzipStream, packageGuid + "/asset", entry.AssetBytes);
                    }

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

        private static bool WriteEntryToProject(EditableUnityPackageEntry entry, string destinationAssetPath, bool overwrite)
        {
            var absolutePath = ProjectRelativeToAbsolute(destinationAssetPath);
            if (entry.IsFolder)
            {
                // An existing folder keeps its own .meta (and GUID) unless overwriting.
                if (Directory.Exists(absolutePath) && !overwrite)
                {
                    return false;
                }

                Directory.CreateDirectory(absolutePath);
            }
            else
            {
                WriteBytesToFile(absolutePath, entry.AssetBytes);
            }

            if (entry.MetaBytes != null && entry.MetaBytes.Length > 0)
            {
                WriteBytesToFile(absolutePath + ".meta", entry.MetaBytes);
            }

            return true;
        }

        private static void WriteBytesToFile(string absolutePath, byte[] bytes)
        {
            var directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(absolutePath, bytes);
        }

        // Checked on disk: files written earlier in the same import are not in the AssetDatabase yet, and Unity's
        // GenerateUniqueAssetPath returns nothing for a folder it does not know.
        private static string GenerateUniqueProjectPath(string projectRelativePath)
        {
            if (!ProjectPathExists(projectRelativePath))
            {
                return projectRelativePath;
            }

            var directory = Path.GetDirectoryName(projectRelativePath)?.Replace('\\', '/');
            var name = Path.GetFileNameWithoutExtension(projectRelativePath);
            var extension = Path.GetExtension(projectRelativePath);
            for (var index = 1; ; index++)
            {
                var candidate = $"{directory}/{name} {index}{extension}";
                if (!ProjectPathExists(candidate))
                {
                    return candidate;
                }
            }
        }

        private static bool ProjectPathExists(string projectRelativePath)
        {
            var absolutePath = ProjectRelativeToAbsolute(projectRelativePath);
            return File.Exists(absolutePath) || Directory.Exists(absolutePath);
        }

        // A pathname may only name something below the destination: nothing rooted, no "." or "..", and no character
        // Windows reads as a drive, stream or device separator.
        private static bool IsSafeRelativePath(string relativePath)
        {
            return !string.IsNullOrEmpty(relativePath) &&
                   !Path.IsPathRooted(relativePath) &&
                   relativePath.Split('/').All(segment =>
                       segment.Length > 0 && segment != "." && segment != ".." && segment.IndexOfAny(InvalidFileNameChars) < 0);
        }

        /// <summary>
        /// Path of <paramref name="path"/> below <paramref name="root"/>, both canonicalized. Whole folder names are
        /// compared, so "MCB Test Backup" is not inside "MCB Test".
        /// </summary>
        internal static bool TryGetContainedPath(string root, string path, out string relativePath)
        {
            relativePath = null;
            string normalizedRoot;
            string normalizedPath;
            try
            {
                normalizedRoot = Path.GetFullPath(root).Replace('\\', '/').TrimEnd('/');
                normalizedPath = Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
            }
            catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
            {
                return false;
            }

            if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                relativePath = string.Empty;
                return true;
            }

            if (!normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            relativePath = normalizedPath.Substring(normalizedRoot.Length + 1);
            return true;
        }

        // Files inside the project keep their project path; any other file goes directly under Assets/.
        internal static string GetArchivePathForFile(string projectRoot, string fullPath)
        {
            return TryGetContainedPath(projectRoot, fullPath, out var projectPath) && projectPath.Length > 0
                ? projectPath
                : "Assets/" + Path.GetFileName(fullPath);
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

            var projectRoot = GetProjectRoot();
            var trimmed = projectPath.Replace('\\', '/').Trim();
            var absolute = Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(projectRoot, trimmed);
            if (!TryGetContainedPath(projectRoot, absolute, out var normalized))
            {
                throw new InvalidOperationException("Destination must be inside the current Unity project.");
            }

            var topFolder = normalized.Split('/')[0];
            if (!string.Equals(topFolder, "Assets", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(topFolder, "Packages", StringComparison.OrdinalIgnoreCase))
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

        private static string GetProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        private static string ProjectRelativeToAbsolute(string projectRelativePath)
        {
            return Path.GetFullPath(Path.Combine(GetProjectRoot(), projectRelativePath.Replace('/', Path.DirectorySeparatorChar)));
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

        /// <summary>Adds the entry in place of the entries for the same asset, which it returns.</summary>
        public List<EditableUnityPackageEntry> AddOrReplace(EditableUnityPackageEntry entry)
        {
            var replaced = Entries.Where(existing => existing.IsSameAsset(entry)).ToList();
            Entries.RemoveAll(replaced.Contains);
            Entries.Add(entry);
            return replaced;
        }

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

        /// <summary>Folder records carry a pathname and a .meta but no asset payload.</summary>
        public bool IsFolder => AssetBytes == null;

        /// <summary>Same path, or same GUID: an asset moved in the project keeps its GUID.</summary>
        public bool IsSameAsset(EditableUnityPackageEntry other)
        {
            return other != null &&
                   (string.Equals(OriginalAssetPath, other.OriginalAssetPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrWhiteSpace(PackageGuid) && string.Equals(PackageGuid, other.PackageGuid, StringComparison.OrdinalIgnoreCase));
        }

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

        public EditableUnityPackageEntry Clone()
        {
            return new EditableUnityPackageEntry
            {
                PackageGuid = PackageGuid,
                OriginalAssetPath = OriginalAssetPath,
                AssetBytes = CloneBytes(AssetBytes),
                MetaBytes = CloneBytes(MetaBytes),
                PreviewBytes = CloneBytes(PreviewBytes)
            };
        }

        private static byte[] CloneBytes(byte[] bytes)
        {
            if (bytes == null)
            {
                return null;
            }

            var clone = new byte[bytes.Length];
            Buffer.BlockCopy(bytes, 0, clone, 0, bytes.Length);
            return clone;
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
