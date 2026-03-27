# UnityPackageManager

`UnityPackageManager` is a Unity editor tool for inspecting `.unitypackage` archives before import.

It adds a window at `Tools > Orbiters > UnityPackageManager` where you can:

- open any `.unitypackage`
- inspect every asset path inside it
- search and multi-select archive contents
- import selected assets into a chosen folder
- drag selected assets from the window into folders in the Project window

## Package Structure

This package is split into:

- `Runtime`: public models and interfaces other tools can call
- `Editor`: archive reader/extractor implementation and the editor window

## How To Use

1. Open `Tools > Orbiters > UnityPackageManager`.
2. Pick a `.unitypackage` file with `Browse...`.
3. Review the contents list and select the assets you want.
4. Either:
   - click `Import Selected` to extract into the chosen destination folder
   - or drag the selected rows into a folder in Unity's Project window

## Drag And Drop

Dragging from the tool window into the Project window imports the selected files directly into the target folder.

- If a file already exists and overwrite is disabled, UnityPackageManager generates a unique asset path.
- If `Preserve Package Hierarchy` is enabled for button import, the original package folder layout is preserved under the destination folder.

## API

Other editor tools can use the public API to inspect or extract package contents:

```csharp
using Orbiters.UnityPackageManager;
using UnityEditor;

var archive = UnityPackageManagerApi.ReadArchive(@"C:\Downloads\Example.unitypackage");
foreach (var asset in archive.Assets)
{
    Debug.Log($"{asset.AssetName} | {asset.OriginalAssetPath} | {asset.AssetSizeBytes} bytes");
}

UnityPackageManagerApi.ExtractAssets(
    @"C:\Downloads\Example.unitypackage",
    archive.Assets.Take(2).Select(a => a.OriginalAssetPath),
    "Assets/Imported",
    new UnityPackageImportOptions
    {
        PreservePackageHierarchy = true,
        OverwriteExistingFiles = false
    });
```

## Public Types

- `IUnityPackageReader`
- `IUnityPackageExtractor`
- `UnityPackageManagerApi`
- `UnityPackageArchiveInfo`
- `UnityPackageAssetInfo`
- `UnityPackageImportOptions`

## Git

This folder is initialized as its own git repository so it can be versioned independently from the main Unity project.
