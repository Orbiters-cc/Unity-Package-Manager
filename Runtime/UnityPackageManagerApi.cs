using System;
using System.Collections.Generic;

namespace Orbiters.UnityPackageManager
{
    public static class UnityPackageManagerApi
    {
        public static Func<IUnityPackageReader> ReaderFactory;
        public static Func<IUnityPackageExtractor> ExtractorFactory;

        public static UnityPackageArchiveInfo ReadArchive(string unityPackageFilePath)
        {
            return GetReader().ReadArchive(unityPackageFilePath);
        }

        public static IReadOnlyList<string> ExtractAssets(
            string unityPackageFilePath,
            IEnumerable<string> originalAssetPaths,
            string destinationFolderPath,
            UnityPackageImportOptions options = null)
        {
            return GetExtractor().ExtractAssets(
                unityPackageFilePath,
                originalAssetPaths,
                destinationFolderPath,
                options ?? new UnityPackageImportOptions());
        }

        private static IUnityPackageReader GetReader()
        {
            if (ReaderFactory == null)
            {
                throw new InvalidOperationException(
                    "UnityPackageManagerApi.ReaderFactory is not registered. " +
                    "The editor implementation must be loaded before calling this API.");
            }

            return ReaderFactory.Invoke();
        }

        private static IUnityPackageExtractor GetExtractor()
        {
            if (ExtractorFactory == null)
            {
                throw new InvalidOperationException(
                    "UnityPackageManagerApi.ExtractorFactory is not registered. " +
                    "The editor implementation must be loaded before calling this API.");
            }

            return ExtractorFactory.Invoke();
        }
    }
}
