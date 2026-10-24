#!/usr/bin/env python3
"""Package a fresh win-x64 publish. No user configuration or live access is read."""
import hashlib
import json
from pathlib import Path
import shutil
import struct
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parent.parent


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def package(payload):
    assets = json.loads((ROOT / "src/BucketBus.Desktop/obj/project.assets.json").read_text())
    graph = assets["targets"]["net10.0/win-x64"]
    options = json.loads((payload / "BucketBus.Desktop.runtimeconfig.json").read_text())["runtimeOptions"]
    frameworks = options.get("includedFrameworks", [])
    require(frameworks == [{"name": "Microsoft.NETCore.App", "version": "10.0.13"}],
            "Unexpected self-contained runtime; review servicing and packaging evidence.")
    # Confirm x64 PE headers, not merely filenames or a successful cross-build.
    for name in ("BucketBus.Desktop.exe", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll",
                 "libSkiaSharp.dll", "libHarfBuzzSharp.dll", "av_libglesv2.dll"):
        data = (payload / name).read_bytes()
        offset = struct.unpack_from("<I", data, 0x3C)[0]
        require(data[:2] == b"MZ" and data[offset:offset + 4] == b"PE\0\0"
                and struct.unpack_from("<H", data, offset + 4)[0] == 0x8664,
                "Required native component is not x64 PE: " + name)
    # Refuse unexpected data before adding allowlisted documentation and notices.
    for path in payload.rglob("*"):
        require(not path.is_symlink(), "Publish payload contains a symlink.")
        if not path.is_file():
            continue
        # Native NuGet packages can copy PDBs even when managed symbols are disabled.
        if path.suffix.lower() == ".pdb":
            path.unlink()
            continue
        name = path.name.lower()
        require(path.parent == payload and path.suffix.lower() in (".dll", ".exe", ".json", ".txt"),
                "Unexpected publish file: " + path.name)
        require(not any(token in name for token in ("test", "credential", "profile")),
                "Unexpected test or user data in publish output.")
        if path.suffix == ".json":
            require(path.name in ("BucketBus.Desktop.runtimeconfig.json", "BucketBus.Desktop.deps.json"),
                    "Unexpected configuration file in publish output.")

    shutil.copyfile(ROOT / "LICENSE", payload / "LICENSE.BucketBus.txt")
    for name in ("WINDOWS_VALIDATION.md", "DEPENDENCIES.md"):
        shutil.copyfile(ROOT / "docs" / name, payload / name)
    shutil.copyfile(ROOT / "docs/WINDOWS_PORTABLE_README.md", payload / "README.md")
    notices = payload / "notices"
    shutil.copytree(ROOT / "docs/third-party", notices / "supplemental")
    records = []
    supplemental = {
        "Avalonia": ["Avalonia.LICENSE.txt"],
        "AWSSDK.Core": ["AWSSDK.LICENSE.txt", "AWSSDK.NOTICE.txt"],
        "AWSSDK.S3": ["AWSSDK.LICENSE.txt", "AWSSDK.NOTICE.txt"],
        "MicroCom.Runtime": ["MicroCom.LICENSE.txt"],
        "Tmds.DBus": ["Tmds.DBus.LICENSE.txt"],
        "Tmds.DBus.Protocol": ["Tmds.DBus.Protocol.LICENSE.txt"],
    }
    package_paths = {key: value["path"] for key, value in assets["libraries"].items()
                     if value["type"] == "package"}
    runtime_id = "Microsoft.NETCore.App.Runtime.win-x64/10.0.13"
    package_paths[runtime_id] = runtime_id.lower()
    for key, relative in sorted(package_paths.items()):
        folder = next((Path(base) / relative for base in assets["packageFolders"]
                       if (Path(base) / relative).is_dir()), None)
        require(folder is not None, "Restored package missing: " + key)
        spec = ET.parse(next(folder.glob("*.nuspec"))).getroot()
        metadata = {node.tag.split("}")[-1]: node for node in spec.iter()}
        license_node = metadata.get("license")
        package_id, version = key.split("/")
        supplied = []
        for path in sorted(folder.rglob("*")):
            if path.is_file() and any(word in path.name.lower() for word in
                                      ("license", "licence", "notice", "copying", "copyright")):
                target = notices / (package_id + "-" + version) / path.relative_to(folder)
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(path, target)
                supplied.append(target.relative_to(payload).as_posix())
        extra = supplemental.get(package_id, [])
        if package_id.startswith("Avalonia.") and version == "12.1.4" and not supplied:
            extra = supplemental["Avalonia"]
        # BuildServices contributes no runtime asset; retain its metadata in the inventory.
        runtime_assets = any(graph.get(key, {}).get(kind) for kind in ("runtime", "native", "runtimeTargets"))
        require(supplied or extra or not runtime_assets, "Missing runtime package license: " + key)
        records.append({"package": package_id, "version": version,
                        "license": license_node.text if license_node is not None else "See included files",
                        "copyright": metadata["copyright"].text if "copyright" in metadata else None,
                        "notices": supplied + ["notices/supplemental/" + name for name in extra],
                        "nugetContentHash": assets["libraries"].get(key, {}).get("sha512")
                        or (folder / (relative.replace("/", ".") + ".nupkg.sha512")).read_text().strip()})
    write_json(payload / "DEPENDENCY_NOTICES.json", records)

    source_paths = [p for p in (ROOT / "src").rglob("*") if p.is_file()
                    and not {"bin", "obj"}.intersection(p.relative_to(ROOT).parts)
                    and p.suffix in (".cs", ".csproj", ".axaml")]
    source_paths += [ROOT / name for name in ("global.json", "Directory.Build.props", "Directory.Packages.props", "NuGet.Config")]
    write_json(payload / "BUILD_MANIFEST.json", {
        "sdk": json.loads((ROOT / "global.json").read_text())["sdk"]["version"],
        "runtime": "10.0.13", "rid": "win-x64", "configuration": "Release",
        "selfContained": True, "singleFile": False, "trimmed": False, "aot": False,
        "qualification": "Unsigned evaluation artifact; native Windows validation required",
        "sourcesSha256": {p.relative_to(ROOT).as_posix(): digest(p) for p in sorted(source_paths)},
        "lockfilesSha256": {p.relative_to(ROOT).as_posix(): digest(p)
                             for p in sorted((ROOT / "src").glob("*/packages.win-x64.lock.json"))}})
    files = sorted(p for p in payload.rglob("*") if p.is_file())
    # Fixed ZIP metadata and sorted entries make identical payloads byte-repeatable
    # with the same Python/zlib toolchain. No wall clock or local absolute paths.
    output = ROOT / "artifacts/session6/BucketBus-win-x64.zip"
    with tempfile.NamedTemporaryFile(dir=output.parent, suffix=".zip", delete=False) as temp:
        temporary = Path(temp.name)
    try:
        with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for path in files:
                info = zipfile.ZipInfo("BucketBus/" + path.relative_to(payload).as_posix(), (1980, 1, 1, 0, 0, 0))
                info.create_system = 3
                info.external_attr = 0o100644 << 16
                archive.writestr(info, path.read_bytes(), compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
        with zipfile.ZipFile(temporary) as archive:
            require(archive.testzip() is None, "ZIP integrity check failed.")
        summary = {"sha256": digest(temporary), "zipBytes": temporary.stat().st_size,
                   "expandedBytes": sum(p.stat().st_size for p in files), "fileCount": len(files)}
        temporary.replace(output)
        write_json(output.with_suffix(".manifest.json"), summary)
        output.with_suffix(".zip.sha256").write_text(summary["sha256"] + "  " + output.name + "\n")
        print(json.dumps(summary, indent=2))
    finally:
        temporary.unlink(missing_ok=True)


if __name__ == "__main__":
    package(Path(sys.argv[1]).resolve())
