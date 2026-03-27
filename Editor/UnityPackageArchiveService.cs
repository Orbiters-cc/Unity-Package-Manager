using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

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
            var fullPath = ValidateUnityPackageFile(unityPackageFilePath);
            var fileInfo = new FileInfo(fullPath);
            var stateByGuid = ParseArchive(fullPath);

            var assets = stateByGuid.Values
                .Where(state => !string.IsNullOrEmpty(state.Pathname))
                .Select(CreateAssetInfo)
                .OrderBy(asset => asset.OriginalAssetPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new UnityPackageArchiveInfo
            {
                PackageFilePath = fullPath,
                PackageFileSizeBytes = fileInfo.Length,
                LastWriteTimeUtc = fileInfo.LastWriteTimeUtc,
                Assets = assets
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
            var assetInfos = ReadArchive(fullPath).Assets;
            var selectedPaths = new HashSet<string>(
                (originalAssetPaths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizeArchivePath),
                StringComparer.OrdinalIgnoreCase);

            if (selectedPaths.Count == 0)
            {
                return Array.Empty<string>();
            }

            var selectedAssets = assetInfos
                .Where(asset => selectedPaths.Contains(NormalizeArchivePath(asset.OriginalAssetPath)))
                .ToDictionary(asset => asset.PackageGuid, StringComparer.OrdinalIgnoreCase);

            if (selectedAssets.Count == 0)
            {
                return Array.Empty<string>();
            }

            var outputPaths = new List<string>();
            foreach (var asset in selectedAssets.Values)
            {
                outputPaths.Add(BuildDestinationAssetPath(asset, normalizedDestinationFolder, resolvedOptions));
            }

            var destinationByGuid = selectedAssets.Values
                .Zip(outputPaths, (asset, outputPath) => new { asset.PackageGuid, OutputPath = outputPath })
                .ToDictionary(item => item.PackageGuid, item => item.OutputPath, StringComparer.OrdinalIgnoreCase);

            using (var fileStream = File.OpenRead(fullPath))
            using (var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress))
            {
                TarArchiveReader.IterateEntries(gzipStream, (entryName, size, dataStream) =>
                {
                    var packageGuid = TarArchiveReader.GetTopLevelDirectory(entryName);
                    if (string.IsNullOrEmpty(packageGuid) || !destinationByGuid.TryGetValue(packageGuid, out var destinationAssetPath))
                    {
                        TarArchiveReader.Skip(dataStream, size);
                        return;
                    }

                    var suffix = TarArchiveReader.GetEntrySuffix(entryName);
                    if (suffix != "asset" && suffix != "asset.meta")
                    {
                        TarArchiveReader.Skip(dataStream, size);
                        return;
                    }

                    var targetPath = suffix == "asset.meta" ? destinationAssetPath + ".meta" : destinationAssetPath;
                    WriteStreamToProjectFile(targetPath, dataStream, size);
                });
            }

            AssetDatabase.Refresh();
            return outputPaths;
        }

        private static UnityPackageAssetInfo CreateAssetInfo(UnityPackageEntryState state)
        {
            var normalizedPath = NormalizeArchivePath(state.Pathname);
            var assetName = Path.GetFileName(normalizedPath);
            var directoryPath = Path.GetDirectoryName(normalizedPath)?.Replace('\\', '/') ?? string.Empty;

            return new UnityPackageAssetInfo
            {
                PackageGuid = state.PackageGuid,
                OriginalAssetPath = normalizedPath,
                AssetName = assetName,
                DirectoryPath = directoryPath,
                FileExtension = Path.GetExtension(assetName),
                AssetSizeBytes = state.AssetSizeBytes,
                MetaSizeBytes = state.MetaSizeBytes,
                PreviewSizeBytes = state.PreviewSizeBytes,
                HasAssetPayload = state.AssetSizeBytes > 0,
                HasMetaFile = state.MetaSizeBytes > 0,
                HasPreviewImage = state.PreviewSizeBytes > 0
            };
        }

        private static Dictionary<string, UnityPackageEntryState> ParseArchive(string unityPackageFilePath)
        {
            var stateByGuid = new Dictionary<string, UnityPackageEntryState>(StringComparer.OrdinalIgnoreCase);

            using (var fileStream = File.OpenRead(unityPackageFilePath))
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

                    if (!stateByGuid.TryGetValue(packageGuid, out var state))
                    {
                        state = new UnityPackageEntryState { PackageGuid = packageGuid };
                        stateByGuid.Add(packageGuid, state);
                    }

                    switch (TarArchiveReader.GetEntrySuffix(entryName))
                    {
                        case "pathname":
                            state.Pathname = TarArchiveReader.ReadUtf8String(dataStream, size);
                            break;
                        case "asset":
                            state.AssetSizeBytes = size;
                            TarArchiveReader.Skip(dataStream, size);
                            break;
                        case "asset.meta":
                            state.MetaSizeBytes = size;
                            TarArchiveReader.Skip(dataStream, size);
                            break;
                        case "preview.png":
                            state.PreviewSizeBytes = size;
                            TarArchiveReader.Skip(dataStream, size);
                            break;
                        default:
                            TarArchiveReader.Skip(dataStream, size);
                            break;
                    }
                });
            }

            return stateByGuid;
        }

        private static string BuildDestinationAssetPath(
            UnityPackageAssetInfo asset,
            string destinationFolderPath,
            UnityPackageImportOptions options)
        {
            var relativePath = options.PreservePackageHierarchy
                ? GetRelativeImportPath(asset.OriginalAssetPath)
                : asset.AssetName;

            var combined = CombineProjectPath(destinationFolderPath, relativePath);
            return options.OverwriteExistingFiles ? combined : AssetDatabase.GenerateUniqueAssetPath(combined);
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

        private static void WriteStreamToProjectFile(string projectRelativePath, Stream sourceStream, long size)
        {
            var absolutePath = ProjectRelativeToAbsolute(projectRelativePath);
            var directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var output = new FileStream(absolutePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                TarArchiveReader.CopyExactly(sourceStream, output, size);
            }
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

        internal static string NormalizeArchivePath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').TrimStart('/');
        }

        private sealed class UnityPackageEntryState
        {
            public string PackageGuid;
            public string Pathname;
            public long AssetSizeBytes;
            public long MetaSizeBytes;
            public long PreviewSizeBytes;
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
            if (size > int.MaxValue)
            {
                throw new InvalidOperationException("Archive pathname entry is too large to read into memory.");
            }

            var buffer = new byte[(int)size];
            CopyExactly(stream, buffer, size);
            return Encoding.UTF8.GetString(buffer).TrimEnd('\0', '\r', '\n');
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
}
