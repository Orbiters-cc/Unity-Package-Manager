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
        private const float FolderTreeThresholdWidth = 1180f;
        private const float FolderTreeMinWidth = 320f;

        private string unityPackagePath = string.Empty;
        private string searchQuery = string.Empty;
        private string destinationFolder = DefaultDestinationFolder;
        private bool preservePackageHierarchy = true;
        private bool overwriteExistingFiles;
        private Vector2 assetScrollPosition;
        private Vector2 folderTreeScrollPosition;
        private UnityPackageArchiveInfo archiveInfo;
        private FolderNode folderTreeRoot;
        private bool folderTreeDirty = true;
        private readonly HashSet<string> selectedAssetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> expandedFolderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Assets",
            "Packages"
        };

        [MenuItem("Tools/Orbiters/UnityPackageManager")]
        private static void OpenWindow()
        {
            var window = GetWindow<UnityPackageManagerWindow>();
            window.titleContent = new GUIContent("UnityPackageManager");
            window.minSize = new Vector2(860f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.projectChanged += MarkFolderTreeDirty;
        }

        private void OnDisable()
        {
            EditorApplication.projectChanged -= MarkFolderTreeDirty;
        }

        private void OnGUI()
        {
            DrawHeader();
            DrawPackagePicker();
            DrawImportOptions();
            DrawToolbar();

            if (ShouldShowFolderTree())
            {
                DrawWideLayout();
            }
            else
            {
                DrawNarrowLayout();
            }
        }

        private void DrawHeader()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("UnityPackageManager", EditorStyles.boldLabel);

            var message = ShouldShowFolderTree()
                ? "Inspect .unitypackage archives, select files, then import them or drag them directly onto the folder tree."
                : "Inspect .unitypackage archives, select files, then import them. Widen the window to enable the in-window folder tree for drag and drop.";

            EditorGUILayout.HelpBox(message, MessageType.Info);
        }

        private void DrawPackagePicker()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Package", EditorStyles.boldLabel);

            var rect = EditorGUILayout.GetControlRect();
            rect = EditorGUI.PrefixLabel(rect, new GUIContent("File"));

            var browseRect = new Rect(rect.xMax - 90f, rect.y, 90f, rect.height);
            var loadRect = new Rect(rect.xMax - 165f, rect.y, 70f, rect.height);
            var fieldRect = new Rect(rect.x, rect.y, rect.width - 170f, rect.height);

            unityPackagePath = EditorGUI.TextField(fieldRect, unityPackagePath);
            HandlePackageFileDrop(fieldRect);

            if (GUI.Button(loadRect, "Load"))
            {
                LoadArchive();
            }

            if (GUI.Button(browseRect, "Browse..."))
            {
                var picked = EditorUtility.OpenFilePanel("Open .unitypackage", GetInitialDirectory(), "unitypackage");
                if (!string.IsNullOrEmpty(picked))
                {
                    unityPackagePath = picked;
                    LoadArchive();
                }
            }

            EditorGUILayout.HelpBox("Drop any file from the OS or Unity onto the File field. The tool only loads .unitypackage archives.", MessageType.None);
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

        private void DrawWideLayout()
        {
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical(GUILayout.MinWidth(position.width - FolderTreeMinWidth - 24f));
            DrawAssetList();
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical("box", GUILayout.Width(FolderTreeMinWidth));
            DrawFolderTreePanel();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawNarrowLayout()
        {
            DrawAssetList();
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

            assetScrollPosition = EditorGUILayout.BeginScrollView(assetScrollPosition);
            foreach (var asset in visibleAssets)
            {
                DrawAssetRow(asset);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawFolderTreePanel()
        {
            EnsureFolderTreeBuilt();

            EditorGUILayout.LabelField("Project Folders", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Drag selected package files onto a folder below.", EditorStyles.miniLabel);
            EditorGUILayout.Space(4f);

            folderTreeScrollPosition = EditorGUILayout.BeginScrollView(folderTreeScrollPosition);
            if (folderTreeRoot != null)
            {
                foreach (var child in folderTreeRoot.Children)
                {
                    DrawFolderNode(child, 0);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawFolderNode(FolderNode node, int depth)
        {
            var rowRect = EditorGUILayout.GetControlRect();
            rowRect.xMin += depth * 14f;
            rowRect.width -= depth * 14f;

            var isExpanded = expandedFolderPaths.Contains(node.ProjectPath);
            var foldoutRect = new Rect(rowRect.x, rowRect.y, 14f, rowRect.height);
            var labelRect = new Rect(rowRect.x + 14f, rowRect.y, rowRect.width - 14f, rowRect.height);

            if (node.Children.Count > 0)
            {
                var nextExpanded = EditorGUI.Foldout(foldoutRect, isExpanded, GUIContent.none);
                if (nextExpanded != isExpanded)
                {
                    SetFolderExpanded(node.ProjectPath, nextExpanded);
                }
            }

            var isSelectedDestination = string.Equals(destinationFolder, node.ProjectPath, StringComparison.OrdinalIgnoreCase);
            if (isSelectedDestination)
            {
                EditorGUI.DrawRect(rowRect, new Color(0.18f, 0.35f, 0.62f, 0.35f));
            }

            EditorGUI.LabelField(labelRect, node.Name, EditorStyles.label);

            var currentEvent = Event.current;
            if (currentEvent.type == EventType.MouseDown && currentEvent.button == 0 && labelRect.Contains(currentEvent.mousePosition))
            {
                destinationFolder = node.ProjectPath;
                Repaint();
                currentEvent.Use();
            }

            UnityPackageProjectDropHandler.HandleFolderDrop(
                rowRect,
                node.ProjectPath,
                path => destinationFolder = path);

            if (expandedFolderPaths.Contains(node.ProjectPath))
            {
                foreach (var child in node.Children)
                {
                    DrawFolderNode(child, depth + 1);
                }
            }
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

        private void HandlePackageFileDrop(Rect fieldRect)
        {
            var currentEvent = Event.current;
            if (!fieldRect.Contains(currentEvent.mousePosition) ||
                (currentEvent.type != EventType.DragUpdated && currentEvent.type != EventType.DragPerform))
            {
                return;
            }

            var droppedPath = GetFirstDroppedFilePath();
            if (string.IsNullOrWhiteSpace(droppedPath))
            {
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (currentEvent.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                unityPackagePath = droppedPath;

                if (droppedPath.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
                {
                    LoadArchive();
                }
            }

            currentEvent.Use();
        }

        private string GetFirstDroppedFilePath()
        {
            if (DragAndDrop.paths != null)
            {
                foreach (var path in DragAndDrop.paths)
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        return path;
                    }
                }
            }

            if (DragAndDrop.objectReferences != null)
            {
                foreach (var reference in DragAndDrop.objectReferences)
                {
                    if (reference == null)
                    {
                        continue;
                    }

                    var assetPath = AssetDatabase.GetAssetPath(reference);
                    if (!string.IsNullOrWhiteSpace(assetPath))
                    {
                        var absolutePath = Path.GetFullPath(Path.Combine(GetProjectRoot(), assetPath));
                        return absolutePath;
                    }
                }
            }

            return null;
        }

        private void EnsureFolderTreeBuilt()
        {
            if (!folderTreeDirty && folderTreeRoot != null)
            {
                return;
            }

            folderTreeRoot = BuildFolderTree();
            folderTreeDirty = false;
        }

        private FolderNode BuildFolderTree()
        {
            var root = new FolderNode("Project", string.Empty);
            AddFolderBranch(root, "Assets");
            AddFolderBranch(root, "Packages");
            return root;
        }

        private void AddFolderBranch(FolderNode root, string topLevelProjectPath)
        {
            var absolutePath = Path.Combine(GetProjectRoot(), topLevelProjectPath);
            if (!Directory.Exists(absolutePath))
            {
                return;
            }

            root.Children.Add(BuildFolderNode(topLevelProjectPath, absolutePath));
        }

        private FolderNode BuildFolderNode(string projectPath, string absolutePath)
        {
            var node = new FolderNode(Path.GetFileName(projectPath), projectPath);
            foreach (var directory in Directory.GetDirectories(absolutePath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(directory);
                if (string.IsNullOrEmpty(name) || name.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                var childProjectPath = (projectPath + "/" + name).Replace('\\', '/');
                node.Children.Add(BuildFolderNode(childProjectPath, directory));
            }

            return node;
        }

        private void SetFolderExpanded(string projectPath, bool isExpanded)
        {
            if (isExpanded)
            {
                expandedFolderPaths.Add(projectPath);
            }
            else
            {
                expandedFolderPaths.Remove(projectPath);
            }
        }

        private void MarkFolderTreeDirty()
        {
            folderTreeDirty = true;
            Repaint();
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

        private bool ShouldShowFolderTree()
        {
            return position.width >= FolderTreeThresholdWidth;
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

        private sealed class FolderNode
        {
            public FolderNode(string name, string projectPath)
            {
                Name = name;
                ProjectPath = projectPath;
            }

            public string Name { get; }
            public string ProjectPath { get; }
            public List<FolderNode> Children { get; } = new List<FolderNode>();
        }
    }
}
