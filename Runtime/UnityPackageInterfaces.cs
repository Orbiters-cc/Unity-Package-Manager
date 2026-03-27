using System.Collections.Generic;

namespace Orbiters.UnityPackageManager
{
    public interface IUnityPackageReader
    {
        UnityPackageArchiveInfo ReadArchive(string unityPackageFilePath);
    }

    public interface IUnityPackageExtractor
    {
        IReadOnlyList<string> ExtractAssets(
            string unityPackageFilePath,
            IEnumerable<string> originalAssetPaths,
            string destinationFolderPath,
            UnityPackageImportOptions options);
    }
}
