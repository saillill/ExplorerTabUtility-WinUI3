"""Packages the published output into the portable zip the installer expects.

The Inno Setup script unzips this archive into {app} and then runs
{app}\\\\ExplorerTabUtility\\\\ExplorerTabUtility.exe, so the archive must contain a single
top-level `ExplorerTabUtility` folder rather than the publish contents at the root.

The MIT licence is added next to the executable: the licence text has to travel with every
copy of the binaries, and the publish folder does not contain it.
"""

import os
import time
import xml.etree.ElementTree as ET
import zipfile

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
BUILD_PROPS = os.path.join(SCRIPT_DIR, "Directory.Build.props")

SOURCE = "publish/win-x64-fd"
ROOT_IN_ARCHIVE = "ExplorerTabUtility"


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


APP_VERSION = read_app_version()
TARGET = f"artifacts/ExplorerTabUtility_v{APP_VERSION}_Portable_x64.zip"
# (path on disk, path inside the archive) — extras the publish folder does not carry.
EXTRA_FILES = [("LICENSE", f"{ROOT_IN_ARCHIVE}/LICENSE")]

if not os.path.isdir(SOURCE):
    print(f"error: publish directory '{SOURCE}' does not exist.")
    print("       Publish the app as framework-dependent first, then re-run this script, e.g.:")
    print("       dotnet publish ExplorerTabUtility.App.WinUI/ExplorerTabUtility.App.WinUI.csproj \\")
    print("         -c Release -r win-x64 -p:SelfContained=false -p:WindowsAppSDKSelfContained=false \\")
    print("         -o publish/win-x64-fd")
    raise SystemExit(1)

os.makedirs(os.path.dirname(TARGET), exist_ok=True)

count = 0
started = time.time()

with zipfile.ZipFile(TARGET, "w", zipfile.ZIP_DEFLATED, compresslevel=1) as archive:
    for folder, _, files in os.walk(SOURCE):
        for name in files:
            full = os.path.join(folder, name)
            relative = os.path.relpath(full, SOURCE).replace(os.sep, "/")
            archive.write(full, f"{ROOT_IN_ARCHIVE}/{relative}")
            count += 1

    for path, name_in_archive in EXTRA_FILES:
        archive.write(path, name_in_archive)
        count += 1

size_mb = os.path.getsize(TARGET) / 1024 / 1024
print(f"packed {count} files -> {TARGET} ({size_mb:.1f} MB, {time.time() - started:.0f}s)")
