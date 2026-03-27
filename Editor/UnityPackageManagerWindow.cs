using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.UnityPackageManager.Editor
{
    internal sealed class UnityPackageManagerWindow : EditorWindow
    {
        private const string DefaultDestinationFolder = "Assets";

        private string unityPackagePath = string.Empty;
        private string searchQuery = string.Empty;
        private string destinationFolder = DefaultDestinationFolder;
        private bool preservePackageHierarchy = true;
        private bool overwriteExistingFiles;
        private Vector2 scrollPosition;
        private UnityPackageArchiveInfo archiveInfo;
        private readonly HashSet<string> selectedAssetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        [MenuItem("Tools/Orbiters/UnityPackageManager")]
        private static void OpenWindow()
        {
            var window = GetWindow<UnityPackageManagerWindow>();
            window.titleContent = new GUIContent("UnityPackageManager");
            window.minSize = new Vector2(860f, 420f);
            window.Show();
        }

        private void OnGUI()
        {
            DrawHeader();
            DrawPackagePicker();
            DrawImportOptions();
            DrawToolbar();
            DrawAssetList();
        }

        private void DrawHeader()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("UnityPackageManager", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Inspect .unitypackage archives, select the assets you want, then import them or drag them into a folder in the Project window.",
                MessageType.Info);
        }

        private void DrawPackagePicker()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Package", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            unityPackagePath = EditorGUILayout.TextField("File", unityPackagePath);
            if (GUILayout.Button("Browse...", GUILayout.Width(90f)))
            {
                var picked = EditorUtility.OpenFilePanel("Open .unitypackage", GetInitialDirectory(), "unitypackage");
                if (!string.IsNullOrEmpty(picked))
                {
                    unityPackagePath = picked;
                    LoadArchive();
                }
            }

            if (GUILayout.Button("Load", GUILayout.Width(70f)))
            {
                LoadArchive();
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawImportOptions()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Import Options", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            destinationFolder = EditorGUILayout.TextField("Destination", destinationFolder);
            if (GUILayout.Button("Pick Folder", GUILayout.Width(90f)))
            {
                var selected = EditorUtility.OpenFolderPanel("Choose destination folder", GetProjectRoot(), string.Empty);
                if (!string.IsNullOrEmpty(selected))
                {
                    destinationFolder = UnityPackageArchiveService.NormalizeProjectPath(selected, requireExistingFolder: false);
                }
            }

            if (GUILayout.Button("Import Selected", GUILayout.Width(120f)))
            {
                ImportSelected();
            }

            EditorGUILayout.EndHorizontal();

            preservePackageHierarchy = EditorGUILayout.ToggleLeft("Preserve Package Hierarchy", preservePackageHierarchy);
            overwriteExistingFiles = EditorGUILayout.ToggleLeft("Overwrite Existing Files", overwriteExistingFiles);
            EditorGUILayout.EndVertical();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal();
            searchQuery = EditorGUILayout.TextField("Search", searchQuery);

            using (new EditorGUI.DisabledScope(archiveInfo == null || archiveInfo.Assets.Count == 0))
            {
                if (GUILayout.Button("Select All Visible", GUILayout.Width(120f)))
                {
                    foreach (var asset in GetVisibleAssets())
                    {
                        selectedAssetPaths.Add(asset.OriginalAssetPath);
                    }
                }

                if (GUILayout.Button("Clear Selection", GUILayout.Width(110f)))
                {
                    selectedAssetPaths.Clear();
                }
            }

            EditorGUILayout.EndHorizontal();

            if (archiveInfo != null)
            {
                EditorGUILayout.LabelField(
                    $"{archiveInfo.Assets.Count} assets | {FormatSize(archiveInfo.PackageFileSizeBytes)} archive size | {selectedAssetPaths.Count} selected",
                    EditorStyles.miniLabel);
            }
        }

        private void DrawAssetList()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Contents", EditorStyles.boldLabel);

            if (archiveInfo == null)
            {
                EditorGUILayout.HelpBox("Load a .unitypackage file to inspect its contents.", MessageType.None);
                return;
            }

            var visibleAssets = GetVisibleAssets().ToArray();
            if (visibleAssets.Length == 0)
            {
                EditorGUILayout.HelpBox("No assets match the current search.", MessageType.None);
                return;
            }

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            foreach (var asset in visibleAssets)
            {
                DrawAssetRow(asset);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawAssetRow(UnityPackageAssetInfo asset)
        {
            EditorGUILayout.BeginHorizontal("box");
            var isSelected = selectedAssetPaths.Contains(asset.OriginalAssetPath);
            var toggled = EditorGUILayout.Toggle(isSelected, GUILayout.Width(18f));
            if (toggled != isSelected)
            {
                SetSelection(asset.OriginalAssetPath, toggled);
            }

            EditorGUILayout.LabelField(asset.AssetName, GUILayout.Width(220f));
            EditorGUILayout.LabelField(asset.OriginalAssetPath, GUILayout.MinWidth(300f));
            EditorGUILayout.LabelField(FormatSize(asset.AssetSizeBytes), GUILayout.Width(95f));
            EditorGUILayout.LabelField(asset.FileExtension, GUILayout.Width(65f));
            EditorGUILayout.LabelField(asset.HasMetaFile ? "meta" : string.Empty, GUILayout.Width(45f));
            EditorGUILayout.LabelField(asset.HasPreviewImage ? "preview" : string.Empty, GUILayout.Width(55f));
            EditorGUILayout.EndHorizontal();
            var rowRect = GUILayoutUtility.GetLastRect();

            var currentEvent = Event.current;
            if (rowRect.Contains(currentEvent.mousePosition) && currentEvent.type == EventType.MouseDown && currentEvent.button == 0)
            {
                if (!currentEvent.control && !currentEvent.command)
                {
                    selectedAssetPaths.Clear();
                    selectedAssetPaths.Add(asset.OriginalAssetPath);
                }
                else
                {
                    SetSelection(asset.OriginalAssetPath, !isSelected);
                }

                Repaint();
                currentEvent.Use();
            }

            if (rowRect.Contains(currentEvent.mousePosition) && currentEvent.type == EventType.MouseDrag && currentEvent.button == 0)
            {
                UnityPackageProjectDropHandler.BeginDrag(unityPackagePath, GetDragSelection(asset));
                currentEvent.Use();
            }
        }

        private IEnumerable<UnityPackageAssetInfo> GetDragSelection(UnityPackageAssetInfo fallback)
        {
            if (!selectedAssetPaths.Contains(fallback.OriginalAssetPath))
            {
                return new[] { fallback };
            }

            return archiveInfo.Assets.Where(asset => selectedAssetPaths.Contains(asset.OriginalAssetPath));
        }

        private IEnumerable<UnityPackageAssetInfo> GetVisibleAssets()
        {
            if (archiveInfo == null)
            {
                return Enumerable.Empty<UnityPackageAssetInfo>();
            }

            if (string.IsNullOrWhiteSpace(searchQuery))
            {
                return archiveInfo.Assets;
            }

            return archiveInfo.Assets.Where(asset =>
                asset.AssetName.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0 ||
                asset.OriginalAssetPath.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0 ||
                asset.DirectoryPath.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void LoadArchive()
        {
            try
            {
                archiveInfo = UnityPackageManagerApi.ReadArchive(unityPackagePath);
                selectedAssetPaths.Clear();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("UnityPackageManager", exception.Message, "OK");
            }
        }

        private void ImportSelected()
        {
            if (archiveInfo == null)
            {
                EditorUtility.DisplayDialog("UnityPackageManager", "Load a .unitypackage first.", "OK");
                return;
            }

            if (selectedAssetPaths.Count == 0)
            {
                EditorUtility.DisplayDialog("UnityPackageManager", "Select at least one asset to import.", "OK");
                return;
            }

            try
            {
                var imported = UnityPackageManagerApi.ExtractAssets(
                    unityPackagePath,
                    selectedAssetPaths,
                    destinationFolder,
                    new UnityPackageImportOptions
                    {
                        PreservePackageHierarchy = preservePackageHierarchy,
                        OverwriteExistingFiles = overwriteExistingFiles
                    });

                if (imported.Count > 0)
                {
                    EditorUtility.DisplayDialog("UnityPackageManager", $"Imported {imported.Count} assets.", "OK");
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("UnityPackageManager", exception.Message, "OK");
            }
        }

        private void SetSelection(string assetPath, bool isSelected)
        {
            if (isSelected)
            {
                selectedAssetPaths.Add(assetPath);
            }
            else
            {
                selectedAssetPaths.Remove(assetPath);
            }
        }

        private static string GetProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        private string GetInitialDirectory()
        {
            if (!string.IsNullOrWhiteSpace(unityPackagePath) && File.Exists(unityPackagePath))
            {
                return Path.GetDirectoryName(unityPackagePath);
            }

            return GetProjectRoot();
        }

        private static string FormatSize(long sizeBytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB" };
            double size = sizeBytes;
            var suffixIndex = 0;

            while (size >= 1024d && suffixIndex < suffixes.Length - 1)
            {
                size /= 1024d;
                suffixIndex++;
            }

            return $"{size:0.##} {suffixes[suffixIndex]}";
        }
    }
}
