using System;
using System.Collections.Generic;

namespace Orbiters.UnityPackageManager
{
    [Serializable]
    public sealed class UnityPackageAssetInfo
    {
        public string PackageGuid;
        public string OriginalAssetPath;
        public string AssetName;
        public string DirectoryPath;
        public string FileExtension;
        public long AssetSizeBytes;
        public long MetaSizeBytes;
        public long PreviewSizeBytes;
        public bool HasAssetPayload;
        public bool HasMetaFile;
        public bool HasPreviewImage;
    }

    [Serializable]
    public sealed class UnityPackageArchiveInfo
    {
        public string PackageFilePath;
        public long PackageFileSizeBytes;
        public DateTime LastWriteTimeUtc;
        public IReadOnlyList<UnityPackageAssetInfo> Assets;
    }

    [Serializable]
    public sealed class UnityPackageImportOptions
    {
        public bool PreservePackageHierarchy = true;
        public bool OverwriteExistingFiles;
    }
}
