"""Shared release archive policy: ship package assets with their original Unity GUIDs."""

PACKAGE_ROOTS = ("Editor", "Runtime")
ROOT_FILES = ("LICENSE", "README.md", "package.json")
PRIVATE_NAMES = frozenset(("credentials", "credentials.json", "credentials.xml", "id_rsa", "id_ed25519"))


def validate_zip_name(name):
    # Inspect raw segments: PurePath normalizes away '.' and duplicate separators.
    if not name or "\\" in name or "\0" in name or "\n" in name or "\r" in name:
        return "has an invalid archive name"
    if any(part in ("", ".", "..") or ":" in part for part in name.split("/")):
        return "contains an invalid path segment"
    return None


def is_allowed_payload(name):
    if validate_zip_name(name) or name.lower().endswith(".meta"):
        return False
    parts = name.split("/")
    if any(part.startswith(".") or part.lower() in PRIVATE_NAMES for part in parts):
        return False
    return name in ROOT_FILES or len(parts) > 1 and parts[0] in PACKAGE_ROOTS


def metadata_for_payload(name):
    parts = name.split("/")
    return ["/".join(parts[:i]) + ".meta" for i in range(1, len(parts) + 1)]


def required_metadata(payloads):
    return {meta for name in payloads for meta in metadata_for_payload(name)}
