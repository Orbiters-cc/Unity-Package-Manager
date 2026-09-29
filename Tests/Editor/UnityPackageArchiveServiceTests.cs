using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Orbiters.UnityPackageManager.Editor.Tests
{
    public sealed class UnityPackageArchiveServiceTests
    {
        private const string FolderMeta = "fileFormatVersion: 2\nguid: {0}\nfolderAsset: yes\n";
        private readonly UnityPackageArchiveService service = new UnityPackageArchiveService();
        private string tempDirectory;
        private string projectFolder;

        [SetUp]
        public void SetUp()
        {
            tempDirectory = Path.Combine(Path.GetTempPath(), "orbiters-upm-tests-" + NewGuid());
            Directory.CreateDirectory(tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(tempDirectory, true);
            if (projectFolder == null)
            {
                return;
            }

            if (!AssetDatabase.DeleteAsset(projectFolder))
            {
                var absolute = Path.GetFullPath(projectFolder);
                if (Directory.Exists(absolute))
                {
                    Directory.Delete(absolute, true);
                }

                File.Delete(absolute + ".meta");
                AssetDatabase.Refresh();
            }

            projectFolder = null;
        }

        [Test]
        public void FolderEntriesSurviveSaveAndReopen()
        {
            string folderGuid = NewGuid(), fileGuid = NewGuid(), emptyGuid = NewGuid();
            var folderMeta = string.Format(FolderMeta, folderGuid);
            var package = WritePackage(
                (folderGuid + "/pathname", Utf8("Assets/Hat")),
                (folderGuid + "/asset.meta", Utf8(folderMeta)),
                (fileGuid + "/pathname", Utf8("Assets/Hat/a.txt\n00")),
                (fileGuid + "/asset", Utf8("hello")),
                (emptyGuid + "/pathname", Utf8("Assets/Hat/empty.txt")),
                (emptyGuid + "/asset", Array.Empty<byte>()));

            var resaved = Path.Combine(tempDirectory, "resaved.unitypackage");
            service.SaveArchive(resaved, service.ReadEditableArchive(package).Entries);
            var entries = service.ReadEditableArchive(resaved).Entries;

            CollectionAssert.AreEqual(new[] { "Assets/Hat", "Assets/Hat/a.txt", "Assets/Hat/empty.txt" }, entries.Select(entry => entry.OriginalAssetPath));
            var folder = entries[0];
            Assert.IsTrue(folder.IsFolder);
            Assert.AreEqual(folderGuid, folder.PackageGuid);
            Assert.AreEqual(folderMeta, Encoding.UTF8.GetString(folder.MetaBytes));
            Assert.AreEqual("hello", Encoding.UTF8.GetString(entries[1].AssetBytes));
            Assert.IsFalse(entries[2].IsFolder, "an empty file stays a file");
        }

        [Test]
        public void SavingEntriesThatShareAGuidIsRefused()
        {
            var guid = NewGuid();
            var output = Path.Combine(tempDirectory, "shared.unitypackage");
            var error = Assert.Throws<InvalidOperationException>(() =>
                service.SaveArchive(output, new[] { FileEntry(guid, "Assets/Old/Foo.asset"), FileEntry(guid.ToUpperInvariant(), "Assets/New/Foo.asset") }));

            StringAssert.Contains("Assets/Old/Foo.asset", error.Message);
            StringAssert.Contains("Assets/New/Foo.asset", error.Message);
            Assert.IsFalse(File.Exists(output));
        }

        [Test]
        public void AddingAMovedAssetReplacesItsEntry()
        {
            var guid = NewGuid();
            var archive = new EditableUnityPackageArchive { Entries = { FileEntry(guid, "Assets/Old/Foo.asset"), FileEntry(NewGuid(), "Assets/Bar.asset") } };

            var replaced = archive.AddOrReplace(FileEntry(guid, "Assets/New/Foo.asset"));

            CollectionAssert.AreEqual(new[] { "Assets/Old/Foo.asset" }, replaced.Select(entry => entry.OriginalAssetPath));
            CollectionAssert.AreEquivalent(new[] { "Assets/Bar.asset", "Assets/New/Foo.asset" }, archive.Entries.Select(entry => entry.OriginalAssetPath));
        }

        [Test]
        public void WindowSaveUsesUnityDirtyFlagAndWritesTheLoadedArchive()
        {
            var window = ScriptableObject.CreateInstance<UnityPackageManagerWindow>();
            try
            {
                var path = Path.Combine(tempDirectory, "window-save.unitypackage");
                var archive = new EditableUnityPackageArchive { Entries = { FileEntry(NewGuid(), "Assets/Edited.txt", "saved edit") } };
                const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(UnityPackageManagerWindow).GetField("editableArchive", fields).SetValue(window, archive);
                typeof(UnityPackageManagerWindow).GetField("loadedPackagePath", fields).SetValue(window, path);
                SetWindowDirty(window, true);
                window.SaveChanges();
                Assert.IsFalse(((EditorWindow)window).hasUnsavedChanges);
                Assert.AreEqual("saved edit", Encoding.UTF8.GetString(service.ReadEditableArchive(path).Entries.Single().AssetBytes));
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [Test]
        public void WindowSaveWithoutAnArchiveKeepsUnityDirtyFlag()
        {
            var window = ScriptableObject.CreateInstance<UnityPackageManagerWindow>();
            try
            {
                SetWindowDirty(window, true);
                window.SaveChanges();
                Assert.IsTrue(((EditorWindow)window).hasUnsavedChanges, "An unsuccessful save must not allow silent closing.");
            }
            finally
            {
                SetWindowDirty(window, false);
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [TestCase("MCB Test/Assets/Hat/Foo.asset", "Assets/Hat/Foo.asset")]
        [TestCase("MCB Test Backup/Assets/Foo.asset", "Assets/Foo.asset")]
        [TestCase("MCB TestAssets/Foo.asset", "Assets/Foo.asset")]
        [TestCase("Elsewhere/Foo.asset", "Assets/Foo.asset")]
        public void ArchivePathOfAFileComparesWholeFolderNames(string file, string expected)
        {
            var projectRoot = Path.Combine(tempDirectory, "MCB Test");
            Assert.AreEqual(expected, UnityPackageArchiveService.GetArchivePathForFile(projectRoot, Path.Combine(tempDirectory, file)));
        }

        [Test]
        public void DeclaredSizeIsNotAllocatedUpFront()
        {
            var stream = new RecordingStream(new byte[100]);
            Assert.Throws<EndOfStreamException>(() => TarArchiveReader.ReadBytes(stream, 1L << 30));
            Assert.LessOrEqual(stream.LargestBuffer, 1 << 20);
        }

        [Test]
        public void GrowingReadReturnsEveryByte()
        {
            var data = new byte[3 * 1024 * 1024 + 7];
            new System.Random(1).NextBytes(data);
            Assert.IsTrue(data.SequenceEqual(TarArchiveReader.ReadBytes(new MemoryStream(data), data.Length)));
        }

        [Test]
        public void TruncatedHugeEntryFails()
        {
            var package = Path.Combine(tempDirectory, "truncated.unitypackage");
            using (var gzip = new GZipStream(File.Create(package), CompressionMode.Compress))
            {
                gzip.Write(Header("g1/asset", 1L << 30), 0, 512);
            }

            Assert.Throws<EndOfStreamException>(() => service.ReadEditableArchive(package));
        }

        [Test]
        public void ExpandedSizeIsBounded()
        {
            var package = WritePackage(("g1/pathname", Utf8("Assets/a.bin")), ("g1/asset", new byte[4096]));
            Assert.Throws<InvalidDataException>(() => service.ReadEditableArchive(package, 2048, UnityPackageArchiveService.MaxEntries));
        }

        [Test]
        public void EntryCountIsBounded()
        {
            var package = WritePackage(("g1/pathname", Utf8("Assets/a")), ("g2/pathname", Utf8("Assets/b")), ("g3/pathname", Utf8("Assets/c")));
            Assert.Throws<InvalidDataException>(() => service.ReadEditableArchive(package, UnityPackageArchiveService.MaxExpandedBytes, 2));
        }

        [Test]
        public void OversizedPathnameIsRejected()
        {
            var package = WritePackage(("g1/pathname", Utf8("Assets/" + new string('a', UnityPackageArchiveService.MaxPathnameBytes))));
            Assert.Throws<InvalidDataException>(() => service.ReadEditableArchive(package));
        }

        [Test]
        public void BinarySizeFieldIsRejected()
        {
            var package = Path.Combine(tempDirectory, "binary-size.unitypackage");
            using (var gzip = new GZipStream(File.Create(package), CompressionMode.Compress))
            {
                var header = Header("g1/asset", 0);
                header[124] = 0x80;
                gzip.Write(header, 0, 512);
            }

            Assert.Throws<InvalidDataException>(() => service.ReadEditableArchive(package));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EntriesThatLeaveTheDestinationAreRefused(bool overwrite)
        {
            // Every escape attempt stays inside the temporary folder, even if the containment check failed.
            projectFolder = "Assets/__UpmTests_" + NewGuid().Substring(0, 8);
            var destination = projectFolder + "/A/B/Dest";
            var entries = new List<EditableUnityPackageEntry>
            {
                FileEntry(NewGuid(), "Assets/Inside/ok.txt", "ok"),
                FileEntry(NewGuid(), "Assets/../Escaped.txt"),
                FileEntry(NewGuid(), "Packages/com.x/../../Escaped.txt")
            };

            var imported = service.ExtractEntries(entries, destination, new UnityPackageImportOptions { PreservePackageHierarchy = true, OverwriteExistingFiles = overwrite }, out var refused);

            CollectionAssert.AreEqual(new[] { destination + "/Inside/ok.txt" }, imported);
            CollectionAssert.AreEquivalent(new[] { "Assets/../Escaped.txt", "Packages/com.x/../../Escaped.txt" }, refused);
            Assert.AreEqual("ok", File.ReadAllText(destination + "/Inside/ok.txt"));
            Assert.IsFalse(File.Exists(projectFolder + "/A/B/Escaped.txt"));
            Assert.IsFalse(File.Exists(projectFolder + "/A/Escaped.txt"));
        }

        [Test]
        public void FlattenedParentNameIsRefused()
        {
            projectFolder = "Assets/__UpmTests_" + NewGuid().Substring(0, 8);
            var imported = service.ExtractEntries(
                new[] { FileEntry(NewGuid(), "Assets/Hat/..") },
                projectFolder + "/Dest",
                new UnityPackageImportOptions { PreservePackageHierarchy = false },
                out var refused);

            Assert.IsEmpty(imported);
            CollectionAssert.AreEqual(new[] { "Assets/Hat/.." }, refused);
        }

        [TestCase("Assets/../../outside")]
        [TestCase("AssetsBackup/Imported")]
        [TestCase("ProjectSettings")]
        public void DestinationMustBeUnderAssetsOrPackages(string destination)
        {
            Assert.Throws<InvalidOperationException>(() => UnityPackageArchiveService.NormalizeProjectPath(destination, requireExistingFolder: false));
        }

        [Test]
        public void DestinationInASiblingProjectIsRejected()
        {
            var sibling = Path.GetFullPath(Path.Combine(Application.dataPath, "..")) + " Backup/Assets";
            Assert.Throws<InvalidOperationException>(() => UnityPackageArchiveService.NormalizeProjectPath(sibling, requireExistingFolder: false));
        }

        [Test]
        public void FolderEntriesImportAsFoldersWithTheirGuid()
        {
            projectFolder = "Assets/__UpmTests_" + NewGuid().Substring(0, 8);
            var folderGuid = NewGuid();
            var entries = new[]
            {
                new EditableUnityPackageEntry { PackageGuid = folderGuid, OriginalAssetPath = "Assets/Hat/Empty", MetaBytes = Utf8(string.Format(FolderMeta, folderGuid)) },
                FileEntry(NewGuid(), "Assets/Hat/a.txt", "a")
            };

            var imported = service.ExtractEntries(entries, projectFolder, new UnityPackageImportOptions { PreservePackageHierarchy = true }, out var refused);

            Assert.IsEmpty(refused);
            CollectionAssert.AreEquivalent(new[] { projectFolder + "/Hat/Empty", projectFolder + "/Hat/a.txt" }, imported);
            Assert.IsTrue(AssetDatabase.IsValidFolder(projectFolder + "/Hat/Empty"));
            Assert.AreEqual(folderGuid, AssetDatabase.AssetPathToGUID(projectFolder + "/Hat/Empty"));
        }

        [Test]
        public void ImportWithoutOverwriteKeepsFilesWithTheSameName()
        {
            projectFolder = "Assets/__UpmTests_" + NewGuid().Substring(0, 8);
            var destination = projectFolder + "/New";
            var entries = new[] { FileEntry(NewGuid(), "Assets/One/a.txt", "one"), FileEntry(NewGuid(), "Assets/Two/a.txt", "two") };

            var imported = service.ExtractEntries(entries, destination, new UnityPackageImportOptions { PreservePackageHierarchy = false }, out _);

            CollectionAssert.AreEqual(new[] { destination + "/a.txt", destination + "/a 1.txt" }, imported);
            Assert.AreEqual("one", File.ReadAllText(destination + "/a.txt"));
            Assert.AreEqual("two", File.ReadAllText(destination + "/a 1.txt"));
        }

        private static void SetWindowDirty(EditorWindow window, bool dirty)
        {
            typeof(EditorWindow).GetProperty("hasUnsavedChanges").GetSetMethod(true).Invoke(window, new object[] { dirty });
        }

        private static string NewGuid()
        {
            return Guid.NewGuid().ToString("N");
        }

        private static byte[] Utf8(string text)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        private static EditableUnityPackageEntry FileEntry(string guid, string path, string content = "x")
        {
            return new EditableUnityPackageEntry { PackageGuid = guid, OriginalAssetPath = path, AssetBytes = Utf8(content) };
        }

        private string WritePackage(params (string name, byte[] data)[] records)
        {
            var package = Path.Combine(tempDirectory, NewGuid() + ".unitypackage");
            using (var gzip = new GZipStream(File.Create(package), CompressionMode.Compress))
            {
                foreach (var (name, data) in records)
                {
                    gzip.Write(Header(name, data.Length), 0, 512);
                    gzip.Write(data, 0, data.Length);
                    var padding = (512 - data.Length % 512) % 512;
                    gzip.Write(new byte[padding], 0, padding);
                }

                gzip.Write(new byte[1024], 0, 1024);
            }

            return package;
        }

        private static byte[] Header(string name, long size)
        {
            var header = new byte[512];
            Encoding.ASCII.GetBytes(name).CopyTo(header, 0);
            Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0')).CopyTo(header, 124);
            header[156] = (byte)'0';
            return header;
        }

        private sealed class RecordingStream : MemoryStream
        {
            public int LargestBuffer;

            public RecordingStream(byte[] data) : base(data)
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                LargestBuffer = Math.Max(LargestBuffer, buffer.Length);
                return base.Read(buffer, offset, count);
            }
        }
    }
}
