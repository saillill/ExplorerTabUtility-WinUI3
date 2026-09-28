"""Packages the published output into the portable zip the installer expects.

The Inno Setup script unzips this archive into {app} and then runs
{app}\\\\ExplorerTabUtility\\\\ExplorerTabUtility.exe, so the archive must contain a single
top-level `ExplorerTabUtility` folder rather than the publish contents at the root.
"""

import os
import time
import zipfile

SOURCE = "publish/win-x64-fd"
TARGET = "artifacts/ExplorerTabUtility_v1.0.1_Portable_x64.zip"
ROOT_IN_ARCHIVE = "ExplorerTabUtility"

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

size_mb = os.path.getsize(TARGET) / 1024 / 1024
print(f"packed {count} files -> {TARGET} ({size_mb:.1f} MB, {time.time() - started:.0f}s)")
