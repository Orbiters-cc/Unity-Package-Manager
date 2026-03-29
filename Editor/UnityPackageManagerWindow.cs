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
        private const float FolderTreeThresholdWidth = 1150f;
        private const float FolderTreeWidth = 280f;
        private const string ViewModePrefKey = "Orbiters.UnityPackageManager.ViewMode";
        private const string ThumbnailSizePrefKey = "Orbiters.UnityPackageManager.ThumbnailSize";

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
        private readonly Stack<PackageEditAction> undoStack = new Stack<PackageEditAction>();
        private readonly Stack<PackageEditAction> redoStack = new Stack<PackageEditAction>();
        private readonly Dictionary<string, ThumbnailCacheItem> thumbnailCache = new Dictionary<string, ThumbnailCacheItem>(StringComparer.OrdinalIgnoreCase);
        private ViewMode currentViewMode = ViewMode.Thumbnail;
        private float thumbnailTileSize = 92f;

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
            currentViewMode = (ViewMode)EditorPrefs.GetInt(ViewModePrefKey, (int)ViewMode.Thumbnail);
            thumbnailTileSize = EditorPrefs.GetFloat(ThumbnailSizePrefKey, 92f);
            EditorApplication.projectChanged += MarkFolderTreeDirty;
        }

        private void OnDisable()
        {
            EditorApplication.projectChanged -= MarkFolderTreeDirty;
            ClearThumbnailCache();
        }

        private void OnGUI()
        {
            HandleUndoRedoShortcuts();
            DrawTopToolbar();
            DrawTopPanels();
            DrawEditControls();

            if (ShouldShowFolderTree())
            {
                DrawWideLayout();
            }
            else
            {
                DrawAssetPane();
            }
        }

        private void DrawTopToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            DrawToolbarButton("Save", "Save active package", "SaveActive", editableArchive != null, () => SaveArchive(false));
            DrawToolbarButton("Save As", "Save package to a new file", "SaveAs", editableArchive != null, () => SaveArchive(true));
            GUILayout.Space(4f);
            DrawToolbarButton("Add Files", "Add files into the package", "Toolbar Plus", editableArchive != null, AddFilesFromDialog);
            DrawToolbarButton("Remove", "Remove selected package entries", "Toolbar Minus", editableArchive != null && selectedAssetPaths.Count > 0, RemoveSelectedEntries);
            GUILayout.Space(4f);
            DrawToolbarButton("Undo", "Undo last package edit", "Undo", editableArchive != null && undoStack.Count > 0, UndoLastEdit);
            DrawToolbarButton("Redo", "Redo last undone package edit", "Redo", editableArchive != null && redoStack.Count > 0, RedoLastEdit);

            GUILayout.FlexibleSpace();

            var status = string.IsNullOrWhiteSpace(loadedPackagePath)
                ? "No package loaded"
                : hasUnsavedChanges ? "Unsaved changes" : "Saved";
            GUILayout.Label(status, EditorStyles.miniLabel);

            EditorGUILayout.EndHorizontal();
        }

        private void DrawTopPanels()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            DrawPackagePicker();
            EditorGUILayout.EndVertical();
            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            DrawImportControls();
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
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

            var message = ShouldShowFolderTree()
                ? "Drag selected files onto the folder tree to import them into the project."
                : "Widen the window to reveal the in-window folder tree.";
            EditorGUILayout.LabelField(message, EditorStyles.miniLabel);
            EditorGUILayout.HelpBox("Drop any file onto the File field. Only .unitypackage files can be loaded.", MessageType.None);
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
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Project Selection", EditorStyles.boldLabel);

            var activeObject = Selection.activeObject;
            var activePath = activeObject != null ? AssetDatabase.GetAssetPath(activeObject) : string.Empty;
            var selectionCount = Selection.objects?.Length ?? 0;
            var hasSelection = !string.IsNullOrWhiteSpace(activePath);

            EditorGUILayout.BeginHorizontal();
            var previewTexture = GetProjectSelectionPreview(activeObject, activePath);
            var previewRect = GUILayoutUtility.GetRect(48f, 48f, GUILayout.Width(48f), GUILayout.Height(48f));
            if (previewTexture != null)
            {
                GUI.DrawTexture(previewRect, previewTexture, ScaleMode.ScaleToFit, true);
            }

            EditorGUILayout.BeginVertical();
            if (selectionCount <= 0)
            {
                EditorGUILayout.LabelField("No project asset selected", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField("Select an asset in the Project window to add it to the package.", EditorStyles.wordWrappedMiniLabel);
            }
            else if (selectionCount == 1)
            {
                EditorGUILayout.LabelField(activeObject.name, EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(activePath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            else
            {
                EditorGUILayout.LabelField($"{selectionCount} project assets selected", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField("The button will add all selected project assets.", EditorStyles.wordWrappedMiniLabel);
            }

            using (new EditorGUI.DisabledScope(editableArchive == null || !hasSelection))
            {
                if (GUILayout.Button(selectionCount > 1 ? "Add Selected Project Assets To Package" : "Add Selected Project Asset To Package"))
                {
                    AddSelectedProjectAssets();
                }
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
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
            DrawContentsToolbar();

            if (editableArchive == null)
            {
                EditorGUILayout.HelpBox("Load a .unitypackage file to inspect and edit its contents.", MessageType.None);
                return;
            }

            EditorGUILayout.BeginVertical("box", GUILayout.ExpandHeight(true));
            var visibleEntries = GetVisibleEntries().ToArray();
            if (visibleEntries.Length == 0)
            {
                EditorGUILayout.HelpBox("No assets match the current search.", MessageType.None);
            }
            else
            {
                assetScrollPosition = EditorGUILayout.BeginScrollView(assetScrollPosition);
                if (currentViewMode == ViewMode.List)
                {
                    foreach (var entry in visibleEntries)
                    {
                        DrawAssetRow(entry);
                    }
                }
                else
                {
                    DrawThumbnailGrid(visibleEntries);
                }

                EditorGUILayout.EndScrollView();
            }
            var dropRect = GUILayoutUtility.GetLastRect();
            HandleAddFilesDrop(dropRect);
            EditorGUILayout.EndVertical();
        }

        private void DrawContentsToolbar()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Contents", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            searchQuery = EditorGUILayout.TextField(searchQuery, GUILayout.MinWidth(180f), GUILayout.MaxWidth(280f));

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

            GUILayout.Space(8f);
            GUILayout.Label("Size", EditorStyles.miniLabel, GUILayout.Width(28f));
            var nextSize = GUILayout.HorizontalSlider(thumbnailTileSize, 56f, 160f, GUILayout.Width(90f));
            if (!Mathf.Approximately(nextSize, thumbnailTileSize))
            {
                thumbnailTileSize = nextSize;
                EditorPrefs.SetFloat(ThumbnailSizePrefKey, thumbnailTileSize);
                Repaint();
            }

            GUILayout.Space(8f);
            GUILayout.Label("View", EditorStyles.miniLabel, GUILayout.Width(30f));
            DrawViewModeToggle(ViewMode.Thumbnail, "Grid", "thumbnail view");
            DrawViewModeToggle(ViewMode.List, "List", "list view");

            EditorGUILayout.EndHorizontal();

            if (editableArchive != null)
            {
                EditorGUILayout.LabelField(
                    $"{editableArchive.Entries.Count} assets | {FormatSize(editableArchive.PackageFileSizeBytes)} source size | {selectedAssetPaths.Count} selected",
                    EditorStyles.miniLabel);
            }
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

        private void DrawThumbnailGrid(IReadOnlyList<EditableUnityPackageEntry> visibleEntries)
        {
            var availableWidth = Mathf.Max(120f, position.width - (ShouldShowFolderTree() ? FolderTreeWidth + 48f : 48f));
            var columns = Mathf.Max(1, Mathf.FloorToInt(availableWidth / (thumbnailTileSize + 16f)));
            var index = 0;

            while (index < visibleEntries.Count)
            {
                EditorGUILayout.BeginHorizontal();
                for (var column = 0; column < columns && index < visibleEntries.Count; column++, index++)
                {
                    DrawThumbnailTile(visibleEntries[index]);
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawThumbnailTile(EditableUnityPackageEntry entry)
        {
            var assetInfo = entry.ToAssetInfo();
            var isSelected = selectedAssetPaths.Contains(entry.OriginalAssetPath);

            EditorGUILayout.BeginVertical("box", GUILayout.Width(thumbnailTileSize + 18f), GUILayout.Height(thumbnailTileSize + 54f));
            var previewRect = GUILayoutUtility.GetRect(thumbnailTileSize, thumbnailTileSize, GUILayout.Width(thumbnailTileSize), GUILayout.Height(thumbnailTileSize));
            if (isSelected)
            {
                EditorGUI.DrawRect(new Rect(previewRect.x - 2f, previewRect.y - 2f, previewRect.width + 4f, previewRect.height + 24f), new Color(0.18f, 0.35f, 0.62f, 0.25f));
            }

            var thumbnail = GetThumbnailTexture(entry);
            if (thumbnail != null)
            {
                GUI.DrawTexture(previewRect, thumbnail, ScaleMode.ScaleToFit, true);
            }

            GUILayout.Space(4f);
            GUILayout.Label(assetInfo.AssetName, EditorStyles.miniLabel, GUILayout.Width(thumbnailTileSize + 10f));
            EditorGUILayout.EndVertical();

            var tileRect = GUILayoutUtility.GetLastRect();
            var currentEvent = Event.current;
            if (tileRect.Contains(currentEvent.mousePosition) && currentEvent.type == EventType.MouseDown && currentEvent.button == 0)
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

            if (tileRect.Contains(currentEvent.mousePosition) && currentEvent.type == EventType.MouseDrag && currentEvent.button == 0)
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
                undoStack.Clear();
                redoStack.Clear();
                ClearThumbnailCache();
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
                undoStack.Clear();
                redoStack.Clear();
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

            var addedEntries = new List<EditableUnityPackageEntry>();
            var replacedEntries = new List<EditableUnityPackageEntry>();
            foreach (var sourcePath in ExpandSourcePaths(sourcePaths))
            {
                var entry = archiveService.CreateEntryFromFile(sourcePath);
                var replaced = editableArchive.Entries
                    .Where(existing => string.Equals(existing.OriginalAssetPath, entry.OriginalAssetPath, StringComparison.OrdinalIgnoreCase))
                    .Select(existing => existing.Clone())
                    .ToArray();
                if (replaced.Length > 0)
                {
                    replacedEntries.AddRange(replaced);
                }

                editableArchive.Entries.RemoveAll(existing =>
                    string.Equals(existing.OriginalAssetPath, entry.OriginalAssetPath, StringComparison.OrdinalIgnoreCase));
                editableArchive.Entries.Add(entry);
                selectedAssetPaths.Add(entry.OriginalAssetPath);
                addedEntries.Add(entry.Clone());
            }

            if (addedEntries.Count == 0)
            {
                return;
            }

            editableArchive.Entries = editableArchive.Entries
                .OrderBy(entry => entry.OriginalAssetPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            PushUndoAction(new PackageEditAction
            {
                Kind = PackageEditActionKind.Add,
                AddedEntries = addedEntries,
                ReplacedEntries = replacedEntries
            });
            hasUnsavedChanges = true;
            ClearThumbnailCache();
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

            var removedEntries = editableArchive.Entries
                .Where(entry => selectedAssetPaths.Contains(entry.OriginalAssetPath))
                .Select(entry => entry.Clone())
                .ToList();

            editableArchive.Entries.RemoveAll(entry => selectedAssetPaths.Contains(entry.OriginalAssetPath));
            selectedAssetPaths.Clear();
            PushUndoAction(new PackageEditAction
            {
                Kind = PackageEditActionKind.Remove,
                RemovedEntries = removedEntries
            });
            hasUnsavedChanges = true;
            ClearThumbnailCache();
            Repaint();
        }

        private void UndoLastEdit()
        {
            if (editableArchive == null || undoStack.Count == 0)
            {
                return;
            }

            var action = undoStack.Pop();
            ApplyUndo(action);
            redoStack.Push(action.Clone());
            NormalizeEntries();
        }

        private void RedoLastEdit()
        {
            if (editableArchive == null || redoStack.Count == 0)
            {
                return;
            }

            var action = redoStack.Pop();
            ApplyRedo(action);
            undoStack.Push(action.Clone());
            NormalizeEntries();
        }

        private void ApplyUndo(PackageEditAction action)
        {
            switch (action.Kind)
            {
                case PackageEditActionKind.Add:
                    foreach (var added in action.AddedEntries)
                    {
                        editableArchive.Entries.RemoveAll(entry =>
                            string.Equals(entry.OriginalAssetPath, added.OriginalAssetPath, StringComparison.OrdinalIgnoreCase));
                    }

                    foreach (var replaced in action.ReplacedEntries)
                    {
                        editableArchive.Entries.RemoveAll(entry =>
                            string.Equals(entry.OriginalAssetPath, replaced.OriginalAssetPath, StringComparison.OrdinalIgnoreCase));
                        editableArchive.Entries.Add(replaced.Clone());
                    }

                    break;
                case PackageEditActionKind.Remove:
                    foreach (var removed in action.RemovedEntries)
                    {
                        editableArchive.Entries.RemoveAll(entry =>
                            string.Equals(entry.OriginalAssetPath, removed.OriginalAssetPath, StringComparison.OrdinalIgnoreCase));
                        editableArchive.Entries.Add(removed.Clone());
                    }

                    break;
            }
        }

        private void ApplyRedo(PackageEditAction action)
        {
            switch (action.Kind)
            {
                case PackageEditActionKind.Add:
                    foreach (var replaced in action.ReplacedEntries)
                    {
                        editableArchive.Entries.RemoveAll(entry =>
                            string.Equals(entry.OriginalAssetPath, replaced.OriginalAssetPath, StringComparison.OrdinalIgnoreCase));
                    }

                    foreach (var added in action.AddedEntries)
                    {
                        editableArchive.Entries.RemoveAll(entry =>
                            string.Equals(entry.OriginalAssetPath, added.OriginalAssetPath, StringComparison.OrdinalIgnoreCase));
                        editableArchive.Entries.Add(added.Clone());
                    }

                    break;
                case PackageEditActionKind.Remove:
                    foreach (var removed in action.RemovedEntries)
                    {
                        editableArchive.Entries.RemoveAll(entry =>
                            string.Equals(entry.OriginalAssetPath, removed.OriginalAssetPath, StringComparison.OrdinalIgnoreCase));
                    }

                    break;
            }
        }

        private void PushUndoAction(PackageEditAction action)
        {
            undoStack.Push(action.Clone());
            redoStack.Clear();
        }

        private void NormalizeEntries()
        {
            editableArchive.Entries = editableArchive.Entries
                .OrderBy(entry => entry.OriginalAssetPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            selectedAssetPaths.RemoveWhere(path =>
                editableArchive.Entries.All(entry => !string.Equals(entry.OriginalAssetPath, path, StringComparison.OrdinalIgnoreCase)));
            hasUnsavedChanges = undoStack.Count > 0;
            ClearThumbnailCache();
            Repaint();
        }

        private void HandleUndoRedoShortcuts()
        {
            var currentEvent = Event.current;
            if (currentEvent.type != EventType.KeyDown)
            {
                return;
            }

            var actionKey = currentEvent.control || currentEvent.command;
            if (!actionKey)
            {
                return;
            }

            if (currentEvent.keyCode == KeyCode.Z)
            {
                UndoLastEdit();
                currentEvent.Use();
            }
            else if (currentEvent.keyCode == KeyCode.Y)
            {
                RedoLastEdit();
                currentEvent.Use();
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

        private void DrawToolbarButton(string text, string tooltip, string iconName, bool enabled, Action onClick)
        {
            using (new EditorGUI.DisabledScope(!enabled))
            {
                var content = EditorGUIUtility.IconContent(iconName);
                content.text = text;
                content.tooltip = tooltip;
                if (GUILayout.Button(content, EditorStyles.toolbarButton, GUILayout.Height(22f)))
                {
                    onClick?.Invoke();
                }
            }
        }

        private void DrawViewModeToggle(ViewMode mode, string label, string tooltip)
        {
            var selected = currentViewMode == mode;
            if (GUILayout.Toggle(selected, new GUIContent(label, tooltip), EditorStyles.toolbarButton, GUILayout.Width(50f)) && !selected)
            {
                currentViewMode = mode;
                EditorPrefs.SetInt(ViewModePrefKey, (int)mode);
                Repaint();
            }
        }

        private Texture2D GetThumbnailTexture(EditableUnityPackageEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.OriginalAssetPath))
            {
                return EditorGUIUtility.FindTexture("DefaultAsset Icon");
            }

            if (thumbnailCache.TryGetValue(entry.OriginalAssetPath, out var cached))
            {
                return cached.Texture;
            }

            Texture2D texture = null;
            var ownsTexture = false;
            if (entry.PreviewBytes != null && entry.PreviewBytes.Length > 0)
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                ownsTexture = true;

                if (!ImageConversion.LoadImage(texture, entry.PreviewBytes, true))
                {
                    DestroyImmediate(texture, false);
                    texture = null;
                    ownsTexture = false;
                }
            }

            if (texture == null)
            {
                texture = TryBuildAssetThumbnail(entry, out ownsTexture) ?? GetFallbackIcon(entry);
            }

            thumbnailCache[entry.OriginalAssetPath] = new ThumbnailCacheItem
            {
                Texture = texture,
                Owned = ownsTexture
            };
            return texture;
        }

        private Texture2D TryBuildAssetThumbnail(EditableUnityPackageEntry entry, out bool ownsTexture)
        {
            ownsTexture = false;
            var extension = (entry.FileExtension ?? string.Empty).ToLowerInvariant();
            switch (extension)
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".tga":
                    if (entry.AssetBytes == null || entry.AssetBytes.Length == 0)
                    {
                        return null;
                    }

                    var imageTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                    {
                        hideFlags = HideFlags.HideAndDontSave
                    };

                    if (ImageConversion.LoadImage(imageTexture, entry.AssetBytes, true))
                    {
                        ownsTexture = true;
                        return imageTexture;
                    }

                    DestroyImmediate(imageTexture, false);
                    return null;
                default:
                    return null;
            }
        }

        private Texture2D GetFallbackIcon(EditableUnityPackageEntry entry)
        {
            var extension = (entry.FileExtension ?? string.Empty).ToLowerInvariant();
            switch (extension)
            {
                case ".prefab":
                    return FindIconOrDefault("Prefab Icon");
                case ".mat":
                    return FindIconOrDefault("Material Icon");
                case ".anim":
                    return FindIconOrDefault("AnimationClip Icon");
                case ".controller":
                    return FindIconOrDefault("AnimatorController Icon");
                case ".fbx":
                case ".obj":
                    return FindIconOrDefault("PrefabModel Icon");
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".tga":
                    return FindIconOrDefault("Texture2D Icon");
                case ".shader":
                    return FindIconOrDefault("Shader Icon");
                case ".cs":
                    return FindIconOrDefault("cs Script Icon");
                default:
                    return FindIconOrDefault("DefaultAsset Icon");
            }
        }

        private void ClearThumbnailCache()
        {
            foreach (var pair in thumbnailCache)
            {
                if (pair.Value.Owned && pair.Value.Texture != null)
                {
                    DestroyImmediate(pair.Value.Texture, false);
                }
            }

            thumbnailCache.Clear();
        }

        private Texture2D GetProjectSelectionPreview(UnityEngine.Object activeObject, string activePath)
        {
            if (activeObject == null)
            {
                return FindIconOrDefault("DefaultAsset Icon");
            }

            var preview = AssetPreview.GetAssetPreview(activeObject);
            if (preview != null)
            {
                return preview;
            }

            preview = AssetPreview.GetMiniThumbnail(activeObject);
            if (preview != null)
            {
                return preview;
            }

            if (!string.IsNullOrWhiteSpace(activePath))
            {
                var icon = AssetDatabase.GetCachedIcon(activePath) as Texture2D;
                if (icon != null)
                {
                    return icon;
                }
            }

            return FindIconOrDefault("DefaultAsset Icon");
        }

        private Texture2D FindIconOrDefault(string iconName)
        {
            return (EditorGUIUtility.IconContent(iconName).image as Texture2D)
                   ?? EditorGUIUtility.FindTexture(iconName)
                   ?? (EditorGUIUtility.IconContent("DefaultAsset Icon").image as Texture2D)
                   ?? EditorGUIUtility.FindTexture("DefaultAsset Icon");
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

        private sealed class ThumbnailCacheItem
        {
            public Texture2D Texture;
            public bool Owned;
        }

        private enum ViewMode
        {
            Thumbnail = 0,
            List = 1
        }

        private enum PackageEditActionKind
        {
            Add,
            Remove
        }

        private sealed class PackageEditAction
        {
            public PackageEditActionKind Kind;
            public List<EditableUnityPackageEntry> AddedEntries = new List<EditableUnityPackageEntry>();
            public List<EditableUnityPackageEntry> RemovedEntries = new List<EditableUnityPackageEntry>();
            public List<EditableUnityPackageEntry> ReplacedEntries = new List<EditableUnityPackageEntry>();

            public PackageEditAction Clone()
            {
                return new PackageEditAction
                {
                    Kind = Kind,
                    AddedEntries = AddedEntries.Select(entry => entry.Clone()).ToList(),
                    RemovedEntries = RemovedEntries.Select(entry => entry.Clone()).ToList(),
                    ReplacedEntries = ReplacedEntries.Select(entry => entry.Clone()).ToList()
                };
            }
        }
    }
}
