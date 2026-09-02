#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import struct
import sys
from dataclasses import dataclass
from pathlib import Path


MAGIC = b"GDPC"
PCK_PADDING = 32
PACK_REL_FILEBASE = 1 << 1


@dataclass
class FileEntry:
    source_path: Path
    pack_path: str
    size: int
    md5: bytes
    offset: int = 0


def pad_to(value: int, alignment: int) -> int:
    remainder = value % alignment
    return 0 if remainder == 0 else alignment - remainder


def collect_files(root_dir: Path) -> list[FileEntry]:
    files: list[FileEntry] = []
    for source_path in sorted(path for path in root_dir.rglob("*") if path.is_file()):
        data = source_path.read_bytes()
        files.append(
            FileEntry(
                source_path=source_path,
                pack_path=source_path.relative_to(root_dir).as_posix(),
                size=len(data),
                md5=hashlib.md5(data).digest(),
            )
        )
    if not files:
        raise ValueError("The input directory is empty; no files can be written to the PCK")
    return files


def compute_offsets(
    files: list[FileEntry],
    pack_version: int,
    engine_major: int,
    engine_minor: int,
    engine_patch: int,
) -> tuple[int, int, int]:
    del engine_major, engine_minor, engine_patch
    header_size = 4 + 4 * 4
    if pack_version >= 2:
        header_size += 4 + 8
        if pack_version >= 3:
            header_size += 8
    header_size += 16 * 4

    file_base = header_size
    if pack_version >= 3:
        file_base += pad_to(file_base, PCK_PADDING)

    cursor = file_base
    for entry in files:
        entry.offset = cursor
        cursor += entry.size
        cursor += pad_to(cursor, PCK_PADDING)

    dir_offset = cursor + pad_to(cursor, PCK_PADDING) if pack_version >= 3 else 0
    return header_size, file_base, dir_offset


def write_directory(
    handle,
    files: list[FileEntry],
    pack_version: int,
    engine_major: int,
    engine_minor: int,
    file_base: int,
) -> None:
    handle.write(struct.pack("<I", len(files)))
    add_res_prefix = not (engine_major == 4 and engine_minor >= 4)
    for entry in files:
        pack_path = entry.pack_path
        if add_res_prefix and not pack_path.startswith("res://"):
            pack_path = f"res://{pack_path}"
        path_bytes = pack_path.encode("utf-8")
        padded_len = len(path_bytes) + pad_to(len(path_bytes), 4)
        handle.write(struct.pack("<I", padded_len))
        handle.write(path_bytes)
        handle.write(b"\x00" * (padded_len - len(path_bytes)))
        stored_offset = entry.offset - file_base if pack_version >= 2 else entry.offset
        handle.write(struct.pack("<Q", stored_offset))
        handle.write(struct.pack("<Q", entry.size))
        handle.write(entry.md5)
        if pack_version >= 2:
            handle.write(struct.pack("<I", 0))


def create_pck(
    input_dir: Path,
    output_file: Path,
    pack_version: int,
    engine_major: int,
    engine_minor: int,
    engine_patch: int,
) -> None:
    files = collect_files(input_dir)
    _, file_base, dir_offset = compute_offsets(
        files, pack_version, engine_major, engine_minor, engine_patch
    )

    output_file.parent.mkdir(parents=True, exist_ok=True)
    with output_file.open("wb") as handle:
        handle.write(MAGIC)
        handle.write(struct.pack("<I", pack_version))
        handle.write(struct.pack("<I", engine_major))
        handle.write(struct.pack("<I", engine_minor))
        handle.write(struct.pack("<I", engine_patch))

        if pack_version >= 2:
            handle.write(struct.pack("<I", PACK_REL_FILEBASE))
            handle.write(struct.pack("<Q", file_base))
            if pack_version >= 3:
                handle.write(struct.pack("<Q", dir_offset))

        for _ in range(16):
            handle.write(struct.pack("<I", 0))

        if pack_version >= 3:
            handle.write(b"\x00" * pad_to(handle.tell(), PCK_PADDING))

        for entry in files:
            current = handle.tell()
            if current != entry.offset:
                raise ValueError(
                    f"File offset mismatch for {entry.pack_path}: expected {entry.offset}, got {current}"
                )
            handle.write(entry.source_path.read_bytes())
            handle.write(b"\x00" * pad_to(handle.tell(), PCK_PADDING))

        if pack_version >= 3:
            handle.write(b"\x00" * pad_to(handle.tell(), PCK_PADDING))
        write_directory(handle, files, pack_version, engine_major, engine_minor, file_base)


def parse_engine_version(version: str) -> tuple[int, int, int]:
    parts = version.split(".")
    if len(parts) < 2:
        raise ValueError("The engine version must contain at least major.minor")
    major = int(parts[0])
    minor = int(parts[1])
    patch = int(parts[2]) if len(parts) >= 3 else 0
    return major, minor, patch


def main() -> int:
    parser = argparse.ArgumentParser(description="Minimal Godot 4 PCK packer")
    parser.add_argument("input_dir", type=Path, help="Directory to package")
    parser.add_argument("-o", "--output", type=Path, required=True, help="Output PCK file")
    parser.add_argument("--engine-version", default="4.5.1", help="Godot engine version")
    parser.add_argument("--pack-version", type=int, default=3, help="PCK format version")
    args = parser.parse_args()

    input_dir = args.input_dir.resolve()
    if not input_dir.is_dir():
        print(f"[ERROR] Input directory does not exist: {input_dir}", file=sys.stderr)
        return 1

    try:
        engine_major, engine_minor, engine_patch = parse_engine_version(args.engine_version)
        create_pck(
            input_dir=input_dir,
            output_file=args.output.resolve(),
            pack_version=args.pack_version,
            engine_major=engine_major,
            engine_minor=engine_minor,
            engine_patch=engine_patch,
        )
        print(f"[DONE] Packed: {args.output}")
        return 0
    except Exception as exc:
        print(f"[ERROR] {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
