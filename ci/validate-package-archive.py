#!/usr/bin/env python3
import sys
import zipfile

from package_archive_rules import ROOT_FILES, is_allowed_payload, required_metadata, validate_zip_name
REQUIRED_FILES = (
    "Editor/orbiters.unitypackagemanager.Editor.asmdef",
    "Editor/UnityPackageArchiveService.cs",
    "Editor/UnityPackageManagerWindow.cs",
    "Editor/UnityPackageProjectDropHandler.cs",
    "Runtime/orbiters.unitypackagemanager.asmdef",
    "Runtime/UnityPackageManagerApi.cs",
)


def validate_archive(zip_path):
    with zipfile.ZipFile(zip_path) as archive:
        names = sorted(name for name in archive.namelist() if not name.endswith("/"))

    if not names:
        return ["Package archive is empty."]

    failures = []
    if len(set(name.casefold() for name in names)) != len(names):
        failures.append("Package archive has duplicate or case-colliding paths.")
    metas = required_metadata(name for name in names if is_allowed_payload(name))
    for name in names:
        invalid_reason = validate_zip_name(name)
        if invalid_reason:
            failures.append(f"{name}: {invalid_reason}")
            continue

        if not is_allowed_payload(name) and name not in metas:
            failures.append(f"{name}: not in package allowlist")

    for required_file in (*ROOT_FILES, *REQUIRED_FILES, *sorted(metas)):
        if required_file not in names:
            failures.append(f"{required_file}: required package file missing")
    return failures


def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: validate-package-archive.py <package.zip>")

    failures = validate_archive(sys.argv[1])

    if failures:
        print("Package archive validation failed:")
        for failure in failures:
            print(" - " + failure)
        raise SystemExit(1)

    print("Package archive validation passed, including original Unity metadata.")


if __name__ == "__main__":
    main()
