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
        private const float FolderTreeThresholdWidth = 900f;
        private const float FolderTreeWidth = 280f;

        private readonly UnityPackageArchiveService archiveService = new UnityPackageArchiveService();
        private readonly HashSet<string> selectedAssetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> expandedFolderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Assets",
            "Packages"
        };

        private string unityPackagePath = string.Empty;
        private string loadedPackagePath = string.Empty;
        private string searchQuery = string.Empty;
        private string destinationFolder = DefaultDestinationFolder;
        private bool preservePackageHierarchy = true;
        private bool overwriteExistingFiles;
        private bool hasUnsavedChanges;
        private Vector2 assetScrollPosition;
        private Vector2 folderTreeScrollPosition;
        private EditableUnityPackageArchive editableArchive;
        private FolderNode folderTreeRoot;
        private bool folderTreeDirty = true;

        [MenuItem("Tools/Orbiters/UnityPackageManager")]
        private static void OpenWindow()
        {
            var window = GetWindow<UnityPackageManagerWindow>();
            window.titleContent = new GUIContent("UnityPackageManager");
            window.minSize = new Vector2(300f, 420f);
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
            DrawSaveControls();
            DrawImportControls();
            DrawEditControls();
            DrawToolbar();

            if (ShouldShowFolderTree())
            {
                DrawWideLayout();
            }
            else
            {
                DrawAssetPane();
            }
        }

        private void DrawHeader()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("UnityPackageManager", EditorStyles.boldLabel);

            var message = ShouldShowFolderTree()
                ? "Inspect, edit, and save .unitypackage archives. Drag selected files onto the folder tree to import them into the project."
                : "Inspect, edit, and save .unitypackage archives. Widen the window to reveal the in-window folder tree.";

            EditorGUILayout.HelpBox(message, MessageType.Info);
        }

        private void DrawPackagePicker()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Package", EditorStyles.boldLabel);

            var rect = EditorGUILayout.GetControlRect();
            rect = EditorGUI.PrefixLabel(rect, new GUIContent("File"));

            const float browseWidth = 90f;
            const float loadWidth = 70f;
            var browseRect = new Rect(rect.xMax - browseWidth, rect.y, browseWidth, rect.height);
            var loadRect = new Rect(rect.xMax - browseWidth - 5f - loadWidth, rect.y, loadWidth, rect.height);
            var fieldRect = new Rect(rect.x, rect.y, rect.width - browseWidth - loadWidth - 10f, rect.height);

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

            var status = string.IsNullOrWhiteSpace(loadedPackagePath)
                ? "No package loaded."
                : (hasUnsavedChanges ? "Loaded package has unsaved changes." : "Loaded package is in sync with disk.");
            EditorGUILayout.LabelField(status, EditorStyles.miniLabel);
            EditorGUILayout.HelpBox("Drop any file onto the File field. Only .unitypackage files can be loaded.", MessageType.None);
            EditorGUILayout.EndVertical();
        }

        private void DrawSaveControls()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Package Editing", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(editableArchive == null))
            {
                if (GUILayout.Button("Save", GUILayout.Width(90f)))
                {
                    SaveArchive(false);
                }

                if (GUILayout.Button("Save As...", GUILayout.Width(90f)))
                {
                    SaveArchive(true);
                }

                if (GUILayout.Button("Add Files...", GUILayout.Width(100f)))
                {
                    AddFilesFromDialog();
                }

                if (GUILayout.Button("Remove Selected", GUILayout.Width(120f)))
                {
                    RemoveSelectedEntries();
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawImportControls()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Import Into Project", EditorStyles.boldLabel);
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

            using (new EditorGUI.DisabledScope(editableArchive == null || selectedAssetPaths.Count == 0))
            {
                if (GUILayout.Button("Import Selected", GUILayout.Width(120f)))
                {
                    ImportSelected();
                }
            }

            EditorGUILayout.EndHorizontal();

            preservePackageHierarchy = EditorGUILayout.ToggleLeft("Preserve Package Hierarchy", preservePackageHierarchy);
            overwriteExistingFiles = EditorGUILayout.ToggleLeft("Overwrite Existing Files", overwriteExistingFiles);
            EditorGUILayout.EndVertical();
        }

        private void DrawEditControls()
        {
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(editableArchive == null))
            {
                if (GUILayout.Button("Add Selected Project Assets"))
                {
                    AddSelectedProjectAssets();
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal();
            searchQuery = EditorGUILayout.TextField("Search", searchQuery);

            using (new EditorGUI.DisabledScope(editableArchive == null || editableArchive.Entries.Count == 0))
            {
                if (GUILayout.Button("Select All Visible", GUILayout.Width(120f)))
                {
                    foreach (var asset in GetVisibleEntries())
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

            if (editableArchive != null)
            {
                EditorGUILayout.LabelField(
                    $"{editableArchive.Entries.Count} assets | {FormatSize(editableArchive.PackageFileSizeBytes)} source size | {selectedAssetPaths.Count} selected",
                    EditorStyles.miniLabel);
            }
        }

        private void DrawWideLayout()
        {
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical("box", GUILayout.Width(FolderTreeWidth));
            DrawFolderTreePanel();
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical();
            DrawAssetPane();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawAssetPane()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Contents", EditorStyles.boldLabel);

            if (editableArchive == null)
            {
                EditorGUILayout.HelpBox("Load a .unitypackage file to inspect and edit its contents.", MessageType.None);
                return;
            }

            var listRect = GUILayoutUtility.GetRect(10f, 100000f, 200f, 100000f);
            GUI.Box(listRect, GUIContent.none);
            HandleAddFilesDrop(listRect);

            GUILayout.BeginArea(new Rect(listRect.x + 4f, listRect.y + 4f, listRect.width - 8f, listRect.height - 8f));
            var visibleEntries = GetVisibleEntries().ToArray();
            if (visibleEntries.Length == 0)
            {
                EditorGUILayout.HelpBox("No assets match the current search.", MessageType.None);
            }
            else
            {
                assetScrollPosition = EditorGUILayout.BeginScrollView(assetScrollPosition);
                foreach (var entry in visibleEntries)
                {
                    DrawAssetRow(entry);
                }

                EditorGUILayout.EndScrollView();
            }

            GUILayout.EndArea();
        }

        private void DrawFolderTreePanel()
        {
            EnsureFolderTreeBuilt();

            EditorGUILayout.LabelField("Project Folders", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Drag selected package files onto a folder to import them.", EditorStyles.miniLabel);
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

            if (string.Equals(destinationFolder, node.ProjectPath, StringComparison.OrdinalIgnoreCase))
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

        private void DrawAssetRow(EditableUnityPackageEntry entry)
        {
            var assetInfo = entry.ToAssetInfo();

            EditorGUILayout.BeginHorizontal("box");
            var isSelected = selectedAssetPaths.Contains(entry.OriginalAssetPath);
            var toggled = EditorGUILayout.Toggle(isSelected, GUILayout.Width(18f));
            if (toggled != isSelected)
            {
                SetSelection(entry.OriginalAssetPath, toggled);
            }

            EditorGUILayout.LabelField(assetInfo.AssetName, GUILayout.Width(220f));
            EditorGUILayout.LabelField(assetInfo.OriginalAssetPath, GUILayout.MinWidth(280f));
            EditorGUILayout.LabelField(FormatSize(assetInfo.AssetSizeBytes), GUILayout.Width(95f));
            EditorGUILayout.LabelField(assetInfo.FileExtension, GUILayout.Width(65f));
            EditorGUILayout.LabelField(assetInfo.HasMetaFile ? "meta" : string.Empty, GUILayout.Width(45f));
            EditorGUILayout.LabelField(assetInfo.HasPreviewImage ? "preview" : string.Empty, GUILayout.Width(55f));
            EditorGUILayout.EndHorizontal();
            var rowRect = GUILayoutUtility.GetLastRect();

            var currentEvent = Event.current;
            if (rowRect.Contains(currentEvent.mousePosition) && currentEvent.type == EventType.MouseDown && currentEvent.button == 0)
            {
                if (!currentEvent.control && !currentEvent.command)
                {
                    selectedAssetPaths.Clear();
                    selectedAssetPaths.Add(entry.OriginalAssetPath);
                }
                else
                {
                    SetSelection(entry.OriginalAssetPath, !isSelected);
                }

                Repaint();
                currentEvent.Use();
            }

            if (rowRect.Contains(currentEvent.mousePosition) && currentEvent.type == EventType.MouseDrag && currentEvent.button == 0)
            {
                UnityPackageProjectDropHandler.BeginDrag(unityPackagePath, GetDragSelection(entry));
                currentEvent.Use();
            }
        }

        private IEnumerable<UnityPackageAssetInfo> GetDragSelection(EditableUnityPackageEntry fallback)
        {
            if (!selectedAssetPaths.Contains(fallback.OriginalAssetPath))
            {
                return new[] { fallback.ToAssetInfo() };
            }

            return editableArchive.Entries
                .Where(entry => selectedAssetPaths.Contains(entry.OriginalAssetPath))
                .Select(entry => entry.ToAssetInfo());
        }

        private IEnumerable<EditableUnityPackageEntry> GetVisibleEntries()
        {
            if (editableArchive == null)
            {
                return Enumerable.Empty<EditableUnityPackageEntry>();
            }

            if (string.IsNullOrWhiteSpace(searchQuery))
            {
                return editableArchive.Entries;
            }

            return editableArchive.Entries.Where(entry =>
                entry.AssetName.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0 ||
                entry.OriginalAssetPath.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0 ||
                entry.DirectoryPath.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void LoadArchive()
        {
            try
            {
                editableArchive = archiveService.ReadEditableArchive(unityPackagePath);
                loadedPackagePath = editableArchive.PackageFilePath;
                unityPackagePath = editableArchive.PackageFilePath;
                selectedAssetPaths.Clear();
                hasUnsavedChanges = false;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("UnityPackageManager", exception.Message, "OK");
            }
        }

        private void ImportSelected()
        {
            if (editableArchive == null)
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
                var tempPath = SaveToTemporaryArchive();
                var imported = UnityPackageManagerApi.ExtractAssets(
                    tempPath,
                    selectedAssetPaths,
                    destinationFolder,
                    new UnityPackageImportOptions
                    {
                        PreservePackageHierarchy = preservePackageHierarchy,
                        OverwriteExistingFiles = overwriteExistingFiles
                    });

                File.Delete(tempPath);

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

        private string SaveToTemporaryArchive()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "orbiters-upm-" + Guid.NewGuid().ToString("N") + ".unitypackage");
            archiveService.SaveArchive(tempPath, editableArchive.Entries);
            return tempPath;
        }

        private void SaveArchive(bool saveAs)
        {
            if (editableArchive == null)
            {
                return;
            }

            try
            {
                var outputPath = loadedPackagePath;
                if (saveAs || string.IsNullOrWhiteSpace(outputPath))
                {
                    outputPath = EditorUtility.SaveFilePanel("Save .unitypackage", GetInitialDirectory(), GetSuggestedPackageName(), "unitypackage");
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    return;
                }

                archiveService.SaveArchive(outputPath, editableArchive.Entries);
                unityPackagePath = outputPath;
                loadedPackagePath = outputPath;
                hasUnsavedChanges = false;
                editableArchive.PackageFilePath = outputPath;
                editableArchive.PackageFileSizeBytes = new FileInfo(outputPath).Length;
                editableArchive.LastWriteTimeUtc = File.GetLastWriteTimeUtc(outputPath);
                EditorUtility.DisplayDialog("UnityPackageManager", "Package saved.", "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("UnityPackageManager", exception.Message, "OK");
            }
        }

        private void AddFilesFromDialog()
        {
            var selected = EditorUtility.OpenFilePanel("Add file to package", GetInitialDirectory(), string.Empty);
            if (string.IsNullOrWhiteSpace(selected))
            {
                return;
            }

            AddFiles(new[] { selected });
        }

        private void AddSelectedProjectAssets()
        {
            var selectedPaths = Selection.assetGUIDs
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(Path.Combine(GetProjectRoot(), path)))
                .ToArray();

            if (selectedPaths.Length == 0)
            {
                EditorUtility.DisplayDialog("UnityPackageManager", "Select one or more project assets first.", "OK");
                return;
            }

            AddFiles(selectedPaths);
        }

        private void AddFiles(IEnumerable<string> sourcePaths)
        {
            if (editableArchive == null)
            {
                EditorUtility.DisplayDialog("UnityPackageManager", "Load a package before adding files.", "OK");
                return;
            }

            foreach (var sourcePath in ExpandSourcePaths(sourcePaths))
            {
                var entry = archiveService.CreateEntryFromFile(sourcePath);
                editableArchive.Entries.RemoveAll(existing =>
                    string.Equals(existing.OriginalAssetPath, entry.OriginalAssetPath, StringComparison.OrdinalIgnoreCase));
                editableArchive.Entries.Add(entry);
                selectedAssetPaths.Add(entry.OriginalAssetPath);
            }

            editableArchive.Entries = editableArchive.Entries
                .OrderBy(entry => entry.OriginalAssetPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            hasUnsavedChanges = true;
            Repaint();
        }

        private IEnumerable<string> ExpandSourcePaths(IEnumerable<string> sourcePaths)
        {
            foreach (var path in sourcePaths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(path);
                if (Directory.Exists(fullPath))
                {
                    foreach (var file in Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories)
                                 .Where(file => !file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
                    {
                        yield return file;
                    }
                }
                else if (File.Exists(fullPath))
                {
                    yield return fullPath;
                }
            }
        }

        private void RemoveSelectedEntries()
        {
            if (editableArchive == null || selectedAssetPaths.Count == 0)
            {
                return;
            }

            editableArchive.Entries.RemoveAll(entry => selectedAssetPaths.Contains(entry.OriginalAssetPath));
            selectedAssetPaths.Clear();
            hasUnsavedChanges = true;
            Repaint();
        }

        private void HandlePackageFileDrop(Rect fieldRect)
        {
            var currentEvent = Event.current;
            if (!fieldRect.Contains(currentEvent.mousePosition) ||
                (currentEvent.type != EventType.DragUpdated && currentEvent.type != EventType.DragPerform))
            {
                return;
            }

            var droppedPath = GetFirstDroppedPath();
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

        private void HandleAddFilesDrop(Rect dropRect)
        {
            var currentEvent = Event.current;
            if (!dropRect.Contains(currentEvent.mousePosition) ||
                (currentEvent.type != EventType.DragUpdated && currentEvent.type != EventType.DragPerform))
            {
                return;
            }

            var droppedPaths = GetDroppedPaths().ToArray();
            if (droppedPaths.Length == 0 || editableArchive == null)
            {
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (currentEvent.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                AddFiles(droppedPaths);
            }

            currentEvent.Use();
        }

        private IEnumerable<string> GetDroppedPaths()
        {
            if (DragAndDrop.paths != null)
            {
                foreach (var path in DragAndDrop.paths)
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        yield return path;
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
                        yield return Path.GetFullPath(Path.Combine(GetProjectRoot(), assetPath));
                    }
                }
            }
        }

        private string GetFirstDroppedPath()
        {
            return GetDroppedPaths().FirstOrDefault();
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

        private string GetSuggestedPackageName()
        {
            if (!string.IsNullOrWhiteSpace(loadedPackagePath))
            {
                return Path.GetFileNameWithoutExtension(loadedPackagePath);
            }

            return "EditedPackage";
        }

        private static string GetProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        private string GetInitialDirectory()
        {
            if (!string.IsNullOrWhiteSpace(unityPackagePath))
            {
                var candidate = unityPackagePath;
                if (File.Exists(candidate))
                {
                    return Path.GetDirectoryName(candidate);
                }

                var directory = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    return directory;
                }
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
