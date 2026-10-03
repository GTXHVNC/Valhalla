#!/usr/bin/env python3
"""Prepare Valhalla panel external runtime dependencies for the clean repository layout."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import struct
import sys
import tempfile
import zlib

STATUS_SUCCESS = 0
STATUS_FAILURE = 2

COSTURA_PATTERN = re.compile(r"^costura[._].+\.dll(?:\.compressed)?$", re.IGNORECASE)


def locate_project_root(script_path: Path, launch_root: Path) -> Path | None:
    """Return the canonical panel directory for the cleaned repository layout."""
    script_dir = script_path.resolve().parent
    candidates = [script_dir.parent]
    if launch_root.name.lower() == "panel":
        candidates.append(launch_root)
    candidates.append(launch_root / "panel")
    seen: set[Path] = set()
    for candidate in candidates:
        resolved = candidate.resolve()
        if resolved in seen:
            continue
        seen.add(resolved)
        if (resolved / "Valhalla.csproj").is_file():
            return resolved
    return None

def validate_launch_layout(launch_root: Path, project_root: Path) -> tuple[bool, str]:
    """Require the panel project to live under the launch/search root."""
    if launch_root == project_root:
        return False, "run the script from the repository directory, not from inside Valhalla/panel"
    try:
        project_root.relative_to(launch_root)
    except ValueError:
        return False, f"Valhalla must be located under the launch directory: {launch_root}"
    return True, ""


def digest(path: Path, algorithm: str) -> str:
    hasher = hashlib.new(algorithm)
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            hasher.update(chunk)
    return hasher.hexdigest()


def load_manifest(path: Path) -> dict:
    with path.open("r", encoding="utf-8") as handle:
        data = json.load(handle)

    required = data.get("required_files")
    if not isinstance(required, list) or not required:
        raise ValueError("manifest contains no required_files")

    for entry in required:
        for key in ("filename", "input_costura", "sha1", "size"):
            if key not in entry:
                raise ValueError(f"manifest entry is missing '{key}': {entry}")
        if Path(str(entry["filename"])).name != str(entry["filename"]):
            raise ValueError(f"manifest filename is not a simple filename: {entry['filename']}")
        if Path(str(entry["input_costura"])).name != str(entry["input_costura"]):
            raise ValueError(f"manifest input_costura is not a simple filename: {entry['input_costura']}")

    return data


def raw_deflate_decompress(data: bytes) -> bytes:
    """Decompress Costura's raw DEFLATE payload (wbits=-15)."""
    decompressor = zlib.decompressobj(wbits=-15)
    output = decompressor.decompress(data) + decompressor.flush()
    if not decompressor.eof:
        raise ValueError("raw DEFLATE stream did not reach EOF")
    if decompressor.unused_data:
        raise ValueError("unexpected trailing bytes after raw DEFLATE stream")
    return output


def pe_kind(path: Path) -> str:
    """Return a compact PE/architecture description or a failure marker."""
    with path.open("rb") as handle:
        dos = handle.read(64)
        if len(dos) < 64 or dos[:2] != b"MZ":
            return "not-pe"

        pe_offset = struct.unpack_from("<I", dos, 0x3C)[0]
        if pe_offset < 64:
            return "bad-pe-offset"

        handle.seek(pe_offset)
        if handle.read(4) != b"PE\x00\x00":
            return "bad-pe-signature"

        coff = handle.read(20)
        if len(coff) != 20:
            return "truncated-coff"

        machine = struct.unpack_from("<H", coff, 0)[0]
        optional_header_size = struct.unpack_from("<H", coff, 16)[0]
        optional = handle.read(optional_header_size)
        if len(optional) != optional_header_size or optional_header_size < 2:
            return "truncated-optional-header"

        magic = struct.unpack_from("<H", optional, 0)[0]
        architecture = {
            0x014C: "x86",
            0x8664: "x64",
            0x01C4: "arm",
            0xAA64: "arm64",
        }.get(machine, f"machine-0x{machine:04x}")
        return f"pe/{architecture}/optional-0x{magic:04x}"


def validate_binary(path: Path, entry: dict) -> tuple[bool, str]:
    try:
        actual_size = path.stat().st_size
        expected_size = int(entry["size"])
        if actual_size != expected_size:
            return False, f"size mismatch (expected {expected_size}, got {actual_size})"

        actual_sha1 = digest(path, "sha1")
        expected_sha1 = str(entry["sha1"]).lower()
        if actual_sha1.lower() != expected_sha1:
            return False, f"SHA-1 mismatch (expected {expected_sha1}, got {actual_sha1})"

        actual_sha256 = digest(path, "sha256")
        kind = pe_kind(path)
        if not kind.startswith("pe/"):
            return False, f"invalid PE ({kind})"

        return True, (
            f"{actual_size} bytes; SHA-1 {actual_sha1}; "
            f"SHA-256 {actual_sha256}; {kind}"
        )
    except (OSError, ValueError) as exc:
        return False, f"validation error: {exc}"


def normalize_costura_name(path: Path) -> str:
    """Turn an observed Costura filename into its external assembly filename."""
    name = path.name
    if name.lower().endswith(".compressed"):
        name = name[:-len(".compressed")]
    if name.lower().startswith("costura."):
        return name[len("costura."):]
    if name.lower().startswith("costura_"):
        return name[len("costura_"):]
    return name


def walk_inputs(search_root: Path, project_root: Path) -> list[Path]:
    """Recursively enumerate files from cwd, excluding the entire Valhalla tree."""
    project_root = project_root.resolve()
    results: list[Path] = []

    for root, dirs, files in os.walk(search_root):
        root_path = Path(root).resolve()
        kept_dirs: list[str] = []
        for directory in dirs:
            candidate = (root_path / directory).resolve()
            try:
                candidate.relative_to(project_root)
            except ValueError:
                kept_dirs.append(directory)
        dirs[:] = kept_dirs

        for filename in files:
            candidate = (root_path / filename).resolve()
            try:
                candidate.relative_to(project_root)
            except ValueError:
                results.append(candidate)

    return results


def index_paths(paths: list[Path]) -> dict[str, list[Path]]:
    indexed: dict[str, list[Path]] = {}
    for path in paths:
        indexed.setdefault(path.name.lower(), []).append(path)
    return indexed


def _materialized_source_hash(path: Path, entry: dict) -> tuple[bool, str]:
    """Validate a source candidate without modifying the project tree.

    Direct DLLs are validated as-is. Costura .compressed candidates are
    decompressed in memory and validated against the manifest. The returned
    detail is intentionally concise because this is used only for selection.
    """
    try:
        if path.name.lower().endswith(".compressed"):
            raw = raw_deflate_decompress(path.read_bytes())
            expected_size = int(entry["size"])
            if len(raw) != expected_size:
                return False, f"size mismatch (expected {expected_size}, got {len(raw)})"
            actual_sha1 = hashlib.sha1(raw).hexdigest()
            if actual_sha1.lower() != str(entry["sha1"]).lower():
                return False, f"SHA-1 mismatch (expected {entry['sha1']}, got {actual_sha1})"
            return True, f"decompressed SHA-1 {actual_sha1}"

        expected_size = int(entry["size"])
        actual_size = path.stat().st_size
        if actual_size != expected_size:
            return False, f"size mismatch (expected {expected_size}, got {actual_size})"
        actual_sha1 = digest(path, "sha1")
        if actual_sha1.lower() != str(entry["sha1"]).lower():
            return False, f"SHA-1 mismatch (expected {entry['sha1']}, got {actual_sha1})"
        kind = pe_kind(path)
        if not kind.startswith("pe/"):
            return False, f"invalid PE ({kind})"
        return True, f"SHA-1 {actual_sha1}; {kind}"
    except (OSError, ValueError, zlib.error) as exc:
        return False, f"validation error: {type(exc).__name__}: {exc}"


def choose_input(entry: dict, by_name: dict[str, list[Path]]) -> tuple[Path | None, str]:
    """Choose the best manifest-approved source deterministically.

    Preference order:
      1. An exact, uncompressed DLL matching the manifest hash.
      2. A Costura-compressed candidate matching the manifest hash.

    This deliberately resolves the common situation where the launch tree
    contains both a prepared DLL (for example artifacts/dependencies/) and the
    original Costura representation. A
    duplicate is not ambiguous when the manifest proves which bytes are valid.
    A corrupt direct copy automatically falls back to the valid compressed
    representation. If several candidates are valid, the shortest relative
    path and then lexical path order provide deterministic selection.
    """
    expected = str(entry["filename"]).lower()
    declared_costura = str(entry["input_costura"]).lower()

    direct = {path.resolve() for path in by_name.get(expected, []) if not path.name.lower().endswith(".compressed")}
    compressed = {path.resolve() for path in by_name.get(declared_costura, [])}

    # Also discover correctly normalized Costura names without relying only on
    # the exact declared filename spelling.
    for candidates in by_name.values():
        for candidate in candidates:
            if COSTURA_PATTERN.match(candidate.name) and normalize_costura_name(candidate).lower() == expected:
                compressed.add(candidate.resolve())

    valid_direct: list[Path] = []
    for candidate in sorted(direct, key=lambda p: (len(p.parts), str(p).lower())):
        ok, _ = _materialized_source_hash(candidate, entry)
        if ok:
            valid_direct.append(candidate)

    if valid_direct:
        return valid_direct[0], "ok-direct"

    valid_compressed: list[Path] = []
    for candidate in sorted(compressed, key=lambda p: (len(p.parts), str(p).lower())):
        ok, _ = _materialized_source_hash(candidate, entry)
        if ok:
            valid_compressed.append(candidate)

    if valid_compressed:
        return valid_compressed[0], "ok-compressed"

    if direct or compressed:
        return None, "invalid: no candidate matched the manifest integrity data"
    return None, "missing"


def install(entry: dict, by_name: dict[str, list[Path]], destination_dir: Path) -> tuple[str, str, bool]:
    """Validate and atomically install one dependency, replacing old files every run."""
    expected = str(entry["filename"])
    destination = destination_dir / expected

    destination_existed = destination.exists()
    source, reason = choose_input(entry, by_name)
    if source is None:
        if reason.startswith("invalid:"):
            return "error", f"{expected}: {reason}", destination_existed
        return "missing", f"{expected}: manifest-approved input not found", destination_existed
    temporary: Path | None = None
    try:
        if source.name.lower().endswith(".compressed"):
            raw = raw_deflate_decompress(source.read_bytes())
            with tempfile.NamedTemporaryFile(
                prefix=f".{expected}.",
                suffix=".tmp",
                dir=str(destination_dir),
                delete=False,
            ) as temp_handle:
                temporary = Path(temp_handle.name)
                temp_handle.write(raw)
                temp_handle.flush()
                os.fsync(temp_handle.fileno())

            valid, detail = validate_binary(temporary, entry)
            if not valid:
                return "error", f"{expected}: decompressed validation failed ({detail})", destination_existed

            # Always replace, even if an existing destination is identical.
            os.replace(temporary, destination)
            temporary = None
            return "decompressed", f"{expected}: normalized from {source} ({detail})", destination_existed

        valid, detail = validate_binary(source, entry)
        if not valid:
            return "error", f"{expected}: source validation failed ({detail})", destination_existed

        with tempfile.NamedTemporaryFile(
            prefix=f".{expected}.",
            suffix=".tmp",
            dir=str(destination_dir),
            delete=False,
        ) as temp_handle:
            temporary = Path(temp_handle.name)

        shutil.copy2(source, temporary)
        valid, detail = validate_binary(temporary, entry)
        if not valid:
            return "error", f"{expected}: post-copy validation failed ({detail})", destination_existed

        # Always replace, even if an existing destination is identical.
        os.replace(temporary, destination)
        temporary = None
        return "copied", f"{expected}: installed from {source} ({detail})", destination_existed

    except (OSError, ValueError, zlib.error) as exc:
        return "error", f"{expected}: {type(exc).__name__}: {exc}", destination_existed
    finally:
        if temporary is not None:
            try:
                temporary.unlink(missing_ok=True)
            except OSError:
                pass



def install_runtime_file(entry: dict, by_name: dict[str, list[Path]], destination_root: Path) -> tuple[str, str, bool]:
    """Install one manifest-declared non-DLL runtime asset atomically."""
    expected = str(entry["filename"])
    destination = destination_root / expected
    existed = destination.exists()

    candidates = [p for p in by_name.get(expected.lower(), []) if p.resolve() != destination.resolve()]
    if not candidates:
        return "missing", f"{expected}: manifest-approved runtime input not found", existed

    required_size = int(entry.get("size", -1))
    required_sha256 = str(entry.get("sha256", "")).lower()
    valid = []
    for candidate in sorted(candidates, key=lambda p: (len(p.parts), str(p).lower())):
        try:
            size = candidate.stat().st_size
            if required_size >= 0 and size != required_size:
                continue
            if required_sha256 and digest(candidate, "sha256").lower() != required_sha256:
                continue
            valid.append(candidate)
        except OSError:
            continue

    if not valid:
        return "error", f"{expected}: no runtime input matched manifest size/SHA-256", existed

    source = valid[0]
    temporary: Path | None = None
    try:
        with tempfile.NamedTemporaryFile(
            prefix=f".{expected}.",
            suffix=".tmp",
            dir=str(destination_root),
            delete=False,
        ) as temp_handle:
            temporary = Path(temp_handle.name)
        shutil.copy2(source, temporary)
        if required_size >= 0 and temporary.stat().st_size != required_size:
            return "error", f"{expected}: post-copy size mismatch", existed
        if required_sha256 and digest(temporary, "sha256").lower() != required_sha256:
            return "error", f"{expected}: post-copy SHA-256 mismatch", existed
        os.replace(temporary, destination)
        temporary = None
        return "runtime-copied", f"{expected}: installed from {source}", existed
    except OSError as exc:
        return "error", f"{expected}: runtime copy failed ({type(exc).__name__}: {exc})", existed
    finally:
        if temporary is not None:
            try:
                temporary.unlink(missing_ok=True)
            except OSError:
                pass


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Prepare Valhalla external runtime dependencies."
    )
    parser.add_argument(
        "--manifest",
        type=Path,
        default=None,
        help="override the project dependency manifest path",
    )
    parser.add_argument(
        "--quiet",
        action="store_true",
        help="suppress per-file success output",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    launch_root = Path.cwd().resolve()
    script_path = Path(__file__).resolve()
    project_root = locate_project_root(script_path, launch_root)

    if project_root is None:
        print(
            "ERROR: could not locate the Valhalla panel. Expected Valhalla/panel/ under the launch directory with this script in panel/build/.",
            file=sys.stderr,
        )
        return STATUS_FAILURE

    valid_layout, reason = validate_launch_layout(launch_root, project_root)
    if not valid_layout:
        print(f"ERROR: {reason}", file=sys.stderr)
        return STATUS_FAILURE

    manifest_path = (
        args.manifest.resolve()
        if args.manifest is not None
        else project_root / "build" / "dependency_manifest.json"
    )
    destination_dir = project_root / "build" / "dependencies"

    try:
        manifest = load_manifest(manifest_path)
        destination_dir.mkdir(parents=True, exist_ok=True)
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(f"ERROR: project dependency setup failed: {exc}", file=sys.stderr)
        return STATUS_FAILURE

    files = walk_inputs(launch_root, project_root)
    by_name = index_paths(files)
    required_entries = manifest["required_files"]
    required_names = {str(entry["filename"]).lower() for entry in required_entries}
    known_inputs = {str(entry["input_costura"]).lower() for entry in required_entries}

    # Only manifest-approved inputs are candidates. All other Costura-looking
    # artifacts are intentionally ignored without warnings because they are not
    # runtime dependencies (for example Costura itself or Mono.Cecil symbols).
    candidates: list[Path] = []
    for path in files:
        low = path.name.lower()
        if low in known_inputs or low in required_names:
            candidates.append(path)
            continue
        if COSTURA_PATTERN.match(path.name) and normalize_costura_name(path).lower() in required_names:
            candidates.append(path)

    counters = {
        "discovered": len(candidates),
        "decompressed": 0,
        "copied": 0,
        "missing": 0,
        "error": 0,
        "installed": 0,
        "replaced": 0,
    }

    print("=" * 72)
    print("Valhalla External Dependency Preparation")
    print("=" * 72)
    print(f"Launch/search root:     {launch_root}")
    print(f"Script:                 {script_path}")
    print(f"Panel root:             {project_root}")
    print(f"Manifest:               {manifest_path}")
    print(f"Dependency destination: {destination_dir}")
    print(f"Manifest entries:       {len(required_entries)}")
    print(f"Candidate inputs:       {len(candidates)}")
    print("------------------------------------------------------------------------")

    for entry in required_entries:
        status, message, existed = install(entry, by_name, destination_dir)
        counters[status] = counters.get(status, 0) + 1
        if status in {"decompressed", "copied"}:
            counters["installed"] += 1
            if existed:
                counters["replaced"] += 1
        if not args.quiet or status in {"error", "missing"}:
            print(f"[{status.upper():10}] {message}")

    runtime_entries = manifest.get("runtime_files", []) or []
    runtime_counters = {"installed": 0, "replaced": 0, "missing": 0, "error": 0}

    for entry in runtime_entries:
        status, message, existed = install_runtime_file(entry, by_name, project_root)
        print(f"[{status.upper():10}] {message}")
        if status == "runtime-copied":
            runtime_counters["installed"] += 1
            if existed:
                runtime_counters["replaced"] += 1
        elif status == "missing":
            runtime_counters["missing"] += 1
        elif status == "error":
            runtime_counters["error"] += 1

    print("------------------------------------------------------------------------")
    print(f"Discovered:             {counters['discovered']}")
    print(f"Decompressed:           {counters['decompressed']}")
    print(f"Installed:              {counters['installed']}")
    print(f"Replaced existing:      {counters['replaced']}")
    print(f"Missing:                {counters['missing']}")
    print(f"Errors:                 {counters['error']}")
    print(f"Runtime assets installed:{runtime_counters['installed']}")
    print(f"Runtime assets replaced: {runtime_counters['replaced']}")
    print(f"Runtime assets missing:  {runtime_counters['missing']}")
    print(f"Runtime asset errors:    {runtime_counters['error']}")
    print(f"Destination:            {destination_dir}")

    if counters["error"] or counters["missing"] or runtime_counters["error"] or runtime_counters["missing"]:
        print("STATUS: FAILED")
        print("=" * 72)
        return STATUS_FAILURE

    print("STATUS: SUCCESS")
    print("All manifest-approved dependencies were validated and installed.")
    print("Existing destination files were replaced where a source was available.")
    print("=" * 72)
    return STATUS_SUCCESS


if __name__ == "__main__":
    raise SystemExit(main())
