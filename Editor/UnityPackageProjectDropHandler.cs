using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.UnityPackageManager.Editor
{
    [InitializeOnLoad]
    internal static class UnityPackageProjectDropHandler
    {
        private const string DragPayloadKey = "Orbiters.UnityPackageManager.DragPayload";

        static UnityPackageProjectDropHandler()
        {
            EditorApplication.projectWindowItemOnGUI += OnProjectWindowItemGui;
        }

        public static void BeginDrag(string unityPackagePath, IEnumerable<UnityPackageAssetInfo> assets)
        {
            var selectedAssets = assets?.ToArray();
            if (selectedAssets == null || selectedAssets.Length == 0)
            {
                return;
            }

            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = new Object[0];
            DragAndDrop.paths = new string[0];
            DragAndDrop.SetGenericData(DragPayloadKey, new DragPayload
            {
                UnityPackagePath = unityPackagePath,
                AssetPaths = selectedAssets.Select(asset => asset.OriginalAssetPath).ToArray()
            });
            DragAndDrop.StartDrag($"Import {selectedAssets.Length} package asset(s)");
        }

        private static void OnProjectWindowItemGui(string guid, Rect selectionRect)
        {
            var currentEvent = Event.current;
            if ((currentEvent.type != EventType.DragUpdated && currentEvent.type != EventType.DragPerform) ||
                !(DragAndDrop.GetGenericData(DragPayloadKey) is DragPayload payload))
            {
                return;
            }

            if (!selectionRect.Contains(currentEvent.mousePosition))
            {
                return;
            }

            var assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            var targetFolder = AssetDatabase.IsValidFolder(assetPath)
                ? assetPath
                : System.IO.Path.GetDirectoryName(assetPath)?.Replace('\\', '/');

            if (string.IsNullOrEmpty(targetFolder))
            {
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

            if (currentEvent.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                UnityPackageManagerApi.ExtractAssets(
                    payload.UnityPackagePath,
                    payload.AssetPaths,
                    targetFolder,
                    new UnityPackageImportOptions
                    {
                        PreservePackageHierarchy = false,
                        OverwriteExistingFiles = false
                    });
                DragAndDrop.SetGenericData(DragPayloadKey, null);
            }

            currentEvent.Use();
        }

        private sealed class DragPayload
        {
            public string UnityPackagePath;
            public string[] AssetPaths;
        }
    }
}
