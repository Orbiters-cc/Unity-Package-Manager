import hashlib
import importlib.util
from pathlib import Path
import tempfile
import unittest
import zipfile

from package_archive_rules import ROOT_FILES, is_allowed_payload, required_metadata, validate_zip_name


def load_script(name):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


builder = load_script("build-package-file-list")
validator = load_script("validate-package-archive")


class ReleaseArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="orbiters-upm-release-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.payloads = list(ROOT_FILES + validator.REQUIRED_FILES) + ["Editor/UI/style.uss"]
        for name in self.payloads:
            self.write(name, "package payload: " + name)
        for name in required_metadata(self.payloads):
            guid = hashlib.md5(name.encode()).hexdigest()
            self.write(name, "fileFormatVersion: 2\nguid: " + guid + "\n")

    def write(self, name, content):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")

    def archive(self, names):
        path = self.root / "release.zip"
        with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
            for name in names:
                archive.writestr(name, (self.root / name).read_bytes() if (self.root / name).is_file() else b"unwanted")
        return path

    def test_zip_preserves_every_payload_and_folder_guid(self):
        files = builder.collect_package_files(self.root)
        self.assertEqual(set(self.payloads) | required_metadata(self.payloads), set(files))
        path = self.archive(files)
        self.assertEqual([], validator.validate_archive(path))
        with zipfile.ZipFile(path) as archive:
            for meta in required_metadata(self.payloads):
                self.assertEqual((self.root / meta).read_bytes(), archive.read(meta))
        self.assertEqual({"./" + name for name in files if name.endswith(".meta")},
                         set(builder.collect_meta_files(self.root, files)))

    def test_repository_and_credentials_are_excluded_even_under_editor(self):
        forbidden = [".git/config", ".github/workflows/release.yml", ".git-credentials", "Tests/test.cs",
                     "ci/script.py", "Editor/.git/config", "Runtime/.env", "Editor/credentials.json",
                     "Runtime/credentials/token", "Editor/.secret/data", "Editor/id_rsa"]
        for name in forbidden:
            self.write(name, "fake credential canary")
            self.write(name + ".meta", "fake metadata")
        files = builder.collect_package_files(self.root)
        self.assertFalse(set(files) & set(forbidden))
        self.assertFalse(set(files) & {name + ".meta" for name in forbidden})
        for name in forbidden:
            with self.subTest(name=name):
                self.assertTrue(validator.validate_archive(self.archive(files + [name])))

    def test_missing_guid_metadata_fails_instead_of_regenerating_it(self):
        (self.root / "Editor/UnityPackageArchiveService.cs.meta").unlink()
        with self.assertRaisesRegex(ValueError, "UnityPackageArchiveService.cs.meta"):
            builder.collect_package_files(self.root)

    def test_validator_rejects_missing_file_and_folder_metadata(self):
        files = builder.collect_package_files(self.root)
        for meta in required_metadata(self.payloads):
            with self.subTest(meta=meta):
                errors = validator.validate_archive(self.archive([name for name in files if name != meta]))
                self.assertTrue(any(meta in error for error in errors))

    def test_orphan_metadata_is_not_shipped(self):
        self.write("Editor/Deleted.cs.meta", "orphan guid")
        files = builder.collect_package_files(self.root)
        self.assertNotIn("Editor/Deleted.cs.meta", files)
        self.assertTrue(validator.validate_archive(self.archive(files + ["Editor/Deleted.cs.meta"])))

    def test_unsafe_archive_names_are_rejected(self):
        for name in ["/Editor/a", "Editor/../a", "Editor/./a", "Editor//a", "Editor\\a", "Editor/C:/a",
                     "Editor/a\nEditor/b", "Editor/a\r", "Editor/a\0", "", "C:/file", "../package.json"]:
            with self.subTest(name=name):
                self.assertIsNotNone(validate_zip_name(name))
                self.assertFalse(is_allowed_payload(name))

    def test_duplicate_archive_paths_are_rejected(self):
        files = builder.collect_package_files(self.root)
        self.assertTrue(validator.validate_archive(self.archive(files + ["editor/UnityPackageArchiveService.cs"])))

    def test_symlink_files_and_directories_are_not_followed(self):
        try:
            (self.root / "Editor/linked.cs").symlink_to(self.root / "package.json")
            (self.root / "Runtime/linked").symlink_to(self.root / "Editor", target_is_directory=True)
        except OSError as error:
            self.skipTest("Symlinks unavailable on this host: " + str(error))
        files = builder.collect_package_files(self.root)
        self.assertFalse(any("linked" in name for name in files))

    def test_metadata_symlink_is_rejected(self):
        meta = self.root / "Editor.meta"
        meta.unlink()
        try:
            meta.symlink_to(self.root / "Runtime.meta")
        except OSError as error:
            self.skipTest("Symlinks unavailable on this host: " + str(error))
        with self.assertRaisesRegex(ValueError, "Editor.meta"):
            builder.collect_package_files(self.root)

    def test_large_tree_listing_is_deterministic(self):
        for i in range(1000):
            name = "Runtime/Batch/asset" + str(i) + ".txt"
            self.write(name, str(i))
            self.write(name + ".meta", "guid: " + format(i, "032x"))
        self.write("Runtime/Batch.meta", "guid: " + "f" * 32)
        first = builder.collect_package_files(self.root)
        self.assertEqual(first, builder.collect_package_files(self.root))
        self.assertEqual([], validator.validate_archive(self.archive(first)))


if __name__ == "__main__":
    unittest.main()
