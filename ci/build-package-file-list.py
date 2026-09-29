#!/usr/bin/env python3
import argparse
import os
from pathlib import Path

from package_archive_rules import PACKAGE_ROOTS, ROOT_FILES, is_allowed_payload, required_metadata


def to_posix(path):
    return path.as_posix()


def collect_package_files(root):
    files = set()

    for root_file in ROOT_FILES:
        path = root / root_file
        if path.is_file() and not path.is_symlink():
            files.add(to_posix(path.relative_to(root)))

    for package_root in PACKAGE_ROOTS:
        package_root_path = root / package_root
        if package_root_path.is_dir() and not package_root_path.is_symlink():
            for directory, subdirs, names in os.walk(package_root_path, followlinks=False):
                parent = Path(directory)
                subdirs[:] = [name for name in subdirs if not (parent / name).is_symlink() and
                              is_allowed_payload(to_posix((parent / name / "asset").relative_to(root)))]
                for name in names:
                    path = parent / name
                    relative = to_posix(path.relative_to(root))
                    if path.is_file() and not path.is_symlink() and is_allowed_payload(relative):
                        files.add(relative)

    # UPM ZIPs need the very same GUIDs as UnityPackage exports. Never synthesize metadata here.
    missing = []
    for name in required_metadata(files):
        meta = root / name
        if not meta.is_file() or meta.is_symlink():
            missing.append(name)
        else:
            files.add(name)
    if missing:
        raise ValueError("Missing original Unity metadata: " + ", ".join(sorted(missing)))

    return sorted(files, key=str.lower)


def collect_meta_files(root, files):
    # This list is a subset of the ZIP, so both formats ship exactly the same GUIDs.
    return sorted(("./" + name for name in files if name.endswith(".meta")), key=str.lower)


def main():
    parser = argparse.ArgumentParser(description="Build the explicit UnityPackageManager package release file list.")
    parser.add_argument("--root", default=".", help="Package root to scan.")
    parser.add_argument("--output", default="packageFiles", help="Output file path.")
    parser.add_argument("--meta-output", help="Optional output path for the .meta file list used by create-unitypackage.")
    args = parser.parse_args()

    root = Path(args.root).resolve()
    output = Path(args.output)
    if not output.is_absolute():
        output = root / output

    files = collect_package_files(root)
    if not files:
        raise SystemExit("Package allowlist produced no files.")

    output.write_text("\n".join(files) + "\n", encoding="utf-8")
    print(f"Wrote {len(files)} package file entries to {output}")

    if args.meta_output:
        meta_output = Path(args.meta_output)
        if not meta_output.is_absolute():
            meta_output = root / meta_output

        metas = collect_meta_files(root, files)
        if not metas:
            raise SystemExit("Package allowlist produced no .meta files.")

        meta_output.write_text("\n".join(metas) + "\n", encoding="utf-8")
        print(f"Wrote {len(metas)} package .meta file entries to {meta_output}")


if __name__ == "__main__":
    main()
