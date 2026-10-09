"""Packages the published output into the portable zip shipped as a release asset.

The archive must contain a single top-level `ExplorerTabUtility` folder rather than the publish
contents at the root, so it can be unzipped straight into a folder of its own.

One archive per architecture: `--arch` (default x64) selects both the publish directory and the
name suffix, so x64 and arm64 can be built in the same tree without overwriting each other.
The table below has to stay aligned with ARCHITECTURES in tools/release.py — that script verifies
the artifact it expects really appeared, so a drift fails loudly there instead of shipping the
wrong file.

The MIT licence is added next to the executable: the licence text has to travel with every copy of
the binaries, and the publish folder does not contain it.
"""

import argparse
import os
import time
import xml.etree.ElementTree as ET
import zipfile

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
BUILD_PROPS = os.path.join(SCRIPT_DIR, "Directory.Build.props")

ROOT_IN_ARCHIVE = "ExplorerTabUtility"

# arch -> (publish directory, suffix in the artifact name). The publish directory name must match
# what tools/release.py passes to `dotnet publish -o`; the suffix must match installer.iss's
# OutputBaseFilename suffix and ARCHITECTURES in tools/release.py.
ARCHITECTURES = {
    "x64": ("publish/win-x64-fd", "x64"),
    "arm64": ("publish/win-arm64-fd", "arm64"),
}

# Kept separate from the table above so the "how do I produce this directory by hand" hint below can
# spell out the right runtime identifier.
RUNTIME_IDENTIFIERS = {"x64": "win-x64", "arm64": "win-arm64"}

# (path on disk, path inside the archive) — extras the publish folder does not carry.
EXTRA_FILES = [("LICENSE", f"{ROOT_IN_ARCHIVE}/LICENSE")]


def read_app_version(default="1.0.1"):
    """产品版本号的单一来源：根 Directory.Build.props 的 <AppVersion>。

    产物名带版本号，因此这里读取同一来源而不是再硬编码一份，避免与 App 程序集
    版本漂移。解析失败时回退 default，保证打包脚本不因 props 临时缺失而中断。
    """
    try:
        root = ET.parse(BUILD_PROPS).getroot()
        for element in root.iter("AppVersion"):
            if element.text and element.text.strip():
                return element.text.strip()
    except (OSError, ET.ParseError):
        pass
    return default


def main() -> int:
    parser = argparse.ArgumentParser(description="Pack the published output into the portable zip.")
    parser.add_argument("--arch", choices=sorted(ARCHITECTURES), default="x64",
                        help="architecture to pack (default: x64)")
    args = parser.parse_args()

    source, suffix = ARCHITECTURES[args.arch]
    target = f"artifacts/ExplorerTabUtility_v{read_app_version()}_Portable_{suffix}.zip"

    if not os.path.isdir(source):
        print(f"error: publish directory '{source}' does not exist.")
        print("       Publish the app as framework-dependent first, then re-run this script, e.g.:")
        print("       dotnet publish ExplorerTabUtility.App.WinUI/ExplorerTabUtility.App.WinUI.csproj \\")
        print(f"         -c Release -r {RUNTIME_IDENTIFIERS[args.arch]} "
              f"-p:SelfContained=false -p:WindowsAppSDKSelfContained=false \\")
        print(f"         -o {source}")
        return 1

    os.makedirs(os.path.dirname(target), exist_ok=True)

    count = 0
    started = time.time()

    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, compresslevel=1) as archive:
        for folder, _, files in os.walk(source):
            for name in files:
                full = os.path.join(folder, name)
                relative = os.path.relpath(full, source).replace(os.sep, "/")
                archive.write(full, f"{ROOT_IN_ARCHIVE}/{relative}")
                count += 1

        for path, name_in_archive in EXTRA_FILES:
            archive.write(path, name_in_archive)
            count += 1

    size_mb = os.path.getsize(target) / 1024 / 1024
    print(f"packed {count} files -> {target} ({size_mb:.1f} MB, {time.time() - started:.0f}s)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
