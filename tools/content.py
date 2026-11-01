#!/usr/bin/env python3
"""Shared content loader for the Python tuning tools (specs/09 section 11). Stdlib only.

    import content
    db = content.load("content")          # verifies manifest.json first (E900-E902, same text as the C# loader)
    db.constants["HP_G"]                  # constants: key -> value
    db.curves["enemy_hp"]                 # curves: id -> (base, growth, x_offset)
    db.table("stats")                     # any other table: list of row dicts straight from `rows`
    db.table("research", None)            # an optional table the build does not ship returns the default
    db.columns("banners")                 # column names from export_content.SCHEMA (folded columns by their fold name)

`add_content_arg(parser)` adds the shared `--content <dir>` flag (default: the repo's content/).
"""
import hashlib
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
DEFAULT_DIR = os.path.join(REPO, "content")

sys.path.insert(0, HERE)
from export_content import SCHEMA, SCHEMA_VERSION  # noqa: E402

_MISSING = object()


class ContentError(Exception):
    """The bundle failed verification. `errors` holds every finding as 'CONTENT <code> <loc>: <message>' (09 8.1)."""

    def __init__(self, errors):
        self.errors = list(errors)
        super().__init__("\n".join(self.errors))


def _err(code, loc, msg):
    return f"CONTENT {code} {loc}: {msg}"


class Content:
    def __init__(self, content_dir, manifest, tables):
        self.dir = content_dir
        self.manifest = manifest
        self._tables = tables
        self.version = manifest.get("content_version")
        self.content_hash = manifest.get("content_hash")
        self.constants = {r["key"]: r["value"] for r in tables.get("constants", [])}
        self.curves = {r["id"]: (r["base"], r["growth"], r["x_offset"]) for r in tables.get("curves", [])}

    def has(self, name):
        return name in self._tables

    def table(self, name, default=_MISSING):
        if name in self._tables:
            return self._tables[name]
        if default is not _MISSING:
            return default
        raise KeyError(f"content table '{name}' is not in {self.dir}")

    @staticmethod
    def columns(name):
        schema = SCHEMA[name]
        folded = {col for cols, _ in schema["folds"].values() for col in cols}
        return [c[0] for c in schema["columns"] if c[0] not in folded] + list(schema["folds"])


def load(content_dir=DEFAULT_DIR):
    """Verify manifest.json (hashes, listed and unlisted files) and return the parsed tables. Raises ContentError."""
    mpath = os.path.join(content_dir, "manifest.json")
    if not os.path.exists(mpath):
        raise ContentError([_err("E902", "manifest.json", "manifest.json listed in manifest but missing")])
    with open(mpath, "rb") as f:
        manifest = json.loads(f.read().decode("utf-8"))
    if manifest.get("schema_version") != SCHEMA_VERSION:
        raise ContentError([_err("E901", "manifest.json", f"manifest schema_version {manifest.get('schema_version')} "
                                                          f"not supported (supports {SCHEMA_VERSION})")])
    errors = []
    listed = {}
    for group in ("tables", "localization"):
        for name, entry in (manifest.get(group) or {}).items():
            listed[entry["file"]] = (group, name, entry)
    raw = {}
    for fname, (group, name, entry) in listed.items():
        path = os.path.join(content_dir, fname)
        if not os.path.exists(path):
            errors.append(_err("E902", fname, f"{fname} listed in manifest but missing"))
            continue
        with open(path, "rb") as f:
            data = f.read()
        actual = hashlib.sha256(data).hexdigest()
        if actual != entry["sha256"]:
            errors.append(_err("E900", fname, f"{fname}: sha256 {actual} does not match manifest {entry['sha256']}"))
        if group == "tables":
            raw[name] = data
    for fname in sorted(os.listdir(content_dir)):
        if fname.endswith(".json") and fname != "manifest.json" and fname not in listed:
            errors.append(_err("E902", fname, f"{fname} present but not listed"))
    if errors:
        raise ContentError(errors)
    tables = {}
    for name, data in raw.items():
        try:
            tables[name] = json.loads(data.decode("utf-8"))["rows"]
        except (ValueError, KeyError) as e:
            raise ContentError([_err("E010", listed_file(manifest, name), str(e))])
    return Content(content_dir, manifest, tables)


def listed_file(manifest, table):
    return manifest["tables"][table]["file"]


def add_content_arg(parser):
    parser.add_argument("--content", default=DEFAULT_DIR, metavar="DIR",
                        help="built content directory (default: the repo's content/)")
    return parser


def load_or_exit(content_dir):
    try:
        return load(content_dir)
    except ContentError as e:
        sys.exit(str(e))
