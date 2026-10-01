"""Release chain for ExplorerTabUtility — one command, one source of truth.

Why this exists
---------------
The chain used to be a sequence of commands retyped from notes: clean, build, publish, zip, compile
the installer, hash-check, push, tag, upload. Every one of those steps has a documented pitfall (the
publish parameters differ from the default `dotnet publish`; the installed binary once turned out not
to come from the repository at all), and retyping them is how those pitfalls came back. This script is
that chain in executable form, plus the checks that make the result trustworthy.

What it guarantees
------------------
* It refuses to build a release from a dirty working tree, so the build stamp written into the
  binaries (`<version>+<sha>`, see Directory.Build.props) is always a real commit.
* It verifies, after packing, that the DLL inside the portable zip is byte-identical to the published
  one, and that the build stamp really names a commit whose diff against HEAD touches documentation
  only — otherwise it refuses to ship. That is the check that would have caught the "deployed binary
  does not match the repository" incident.
* Shipping (`--ship`) is opt-in and never happens on a failed verification.

Usage
-----
    python tools/release.py                     # build + pack + verify (no network, no git mutation)
    python tools/release.py --allow-dirty       # same, for a local trial build of uncommitted work
    python tools/release.py --ship              # also push, move the tag, replace the release assets
    python tools/release.py --skip-tests        # skip the unit tests (not recommended before shipping)
"""

from __future__ import annotations

import argparse
import hashlib
import os
import re
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
PUBLISH_DIR = REPO / "publish" / "win-x64-fd"
ARTIFACTS_DIR = REPO / "artifacts"
PROJECTS = ("ExplorerTabUtility.App.WinUI", "ExplorerTabUtility.Core", "ExplorerTabUtility.Tests")
APP_DLL = "ExplorerTabUtility.dll"
CORE_DLL = "ExplorerTabUtility.Core.dll"
# Inside the per-language folder, the satellite assembly is the *resources* one (the neutral Core dll
# lives at the publish root) — checking for the plain name there reports every language as missing.
CORE_SATELLITE_DLL = "ExplorerTabUtility.Core.resources.dll"
SATELLITE_LANGUAGES = ("zh-CN", "zh-Hant", "ja", "ko", "de", "fr", "es", "ru")
DOC_SUFFIXES = (".md", ".txt", ".rst", ".adoc")

# The publish parameters are load-bearing: `--self-contained false` produces a different binary than
# `-p:SelfContained=false -p:WindowsAppSDKSelfContained=false`, so they are spelled out once here
# (matching the hint printed by pack-portable.py).
PUBLISH_ARGS = [
    "-p:SelfContained=false",
    "-p:WindowsAppSDKSelfContained=false",
]


def log(message: str) -> None:
    print(f"==> {message}", flush=True)


def run(command: list[str], cwd: Path = REPO, check: bool = True) -> subprocess.CompletedProcess:
    print(f"    $ {' '.join(str(c) for c in command)}", flush=True)
    return subprocess.run(command, cwd=cwd, check=check, text=True)


def output(command: list[str], cwd: Path = REPO) -> str:
    result = subprocess.run(command, cwd=cwd, check=True, text=True, capture_output=True)
    return result.stdout.strip()


def md5(path: Path) -> str:
    return hashlib.md5(path.read_bytes()).hexdigest()


def read_zip_md5(archive: Path, member: str) -> str:
    with zipfile.ZipFile(archive) as zf:
        return hashlib.md5(zf.read(member)).hexdigest()


def read_app_version() -> str:
    """The product version, from its single source of truth."""
    props = (REPO / "Directory.Build.props").read_text(encoding="utf-8")
    match = re.search(r"<AppVersion>([^<]+)</AppVersion>", props)
    if match is None:
        raise SystemExit("Directory.Build.props declares no <AppVersion>")
    return match.group(1).strip()


def read_build_stamp(dll: Path, version: str) -> str:
    """The source revision baked into the assembly, e.g. '0796d98' from '1.0.1+0796d98'."""
    data = dll.read_bytes()

    # Plain byte search, not a regex: encoding a regular expression to UTF-16 would look for literal
    # backslashes in the image (a real first version of this check did exactly that and reported every
    # assembly as un-stamped).
    index = data.find(f"{version}+".encode("utf-16-le"))
    if index < 0:
        return ""

    tail = data[index:index + 80].decode("utf-16-le", errors="ignore")
    match = re.match(r"\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.\-]+)?\+([0-9a-fA-F]{7,40})", tail)
    return match.group(1) if match else ""


def artifact_paths(version: str) -> tuple[Path, Path]:
    return (
        ARTIFACTS_DIR / f"ExplorerTabUtility_v{version}_Portable_x64.zip",
        ARTIFACTS_DIR / f"ExplorerTabUtility_v{version}_Setup.exe",
    )


def find_iscc(explicit: str | None) -> Path:
    candidates = [
        Path(explicit) if explicit else None,
        Path(os.environ.get("LOCALAPPDATA", "")) / "Programs/Inno Setup 6/ISCC.exe",
        Path(r"C:\Program Files (x86)\Inno Setup 6\ISCC.exe"),
        Path(r"C:\Program Files\Inno Setup 6\ISCC.exe"),
    ]
    for candidate in candidates:
        if candidate and candidate.is_file():
            return candidate
    raise SystemExit(
        "ISCC.exe (Inno Setup 6) not found. Install it or pass --iscc <path>."
    )


def assert_documentation_only(stamp: str) -> None:
    """The stamped commit may lag HEAD, but only by changes that cannot affect a binary."""
    head = output(["git", "rev-parse", "--short", "HEAD"])
    if stamp == head:
        return

    changed = output(["git", "diff", "--name-only", f"{stamp}..HEAD"]).splitlines()
    offending = [name for name in changed if not name.lower().endswith(DOC_SUFFIXES)]

    if offending:
        raise SystemExit(
            f"refusing to ship: the binaries were built from {stamp} but HEAD is {head}, and the "
            f"difference includes non-documentation files:\n  " + "\n  ".join(offending) +
            "\nRebuild (re-run this script) so the shipped binaries match the commit they claim."
        )

    log(f"build stamp {stamp} is behind HEAD {head}, but only documentation differs — accepted")


def main() -> int:
    parser = argparse.ArgumentParser(description="Build, pack and optionally publish ExplorerTabUtility.")
    parser.add_argument("--ship", action="store_true",
                        help="push, move the release tag and replace the published assets")
    parser.add_argument("--tag", default="v1.0.1", help="release tag to move/update (default: v1.0.1)")
    parser.add_argument("--skip-tests", action="store_true", help="skip the unit test run")
    parser.add_argument("--allow-dirty", action="store_true",
                        help="build a local trial from uncommitted changes (cannot be combined with --ship)")
    parser.add_argument("--iscc", default=None, help="path to ISCC.exe")
    args = parser.parse_args()

    if args.ship and args.allow_dirty:
        raise SystemExit("--ship and --allow-dirty contradict each other: a shipped binary must come from a commit.")

    os.chdir(REPO)
    head = output(["git", "rev-parse", "--short", "HEAD"])
    dirty = output(["git", "status", "--porcelain"])

    if dirty and not args.allow_dirty:
        raise SystemExit(
            "the working tree is not clean, so the build stamp would name the previous commit and not "
            "describe the binaries. Commit first, or pass --allow-dirty for a local trial build.\n" + dirty
        )

    if dirty:
        log(f"WARNING: building from a dirty tree (HEAD {head}) — the build stamp will not describe "
            f"these binaries, and --ship is refused in this state")
    else:
        log(f"clean tree at {head}")

    # 1. The running app holds its own files open; the installer and the publish directory both need them free.
    log("stopping any running instance")
    run(["taskkill", "/F", "/IM", "ExplorerTabUtility.exe"], check=False)

    # 2. Clean build.
    log("cleaning build output")
    for project in PROJECTS:
        for folder in ("bin", "obj"):
            shutil.rmtree(REPO / project / folder, ignore_errors=True)
    shutil.rmtree(REPO / "publish", ignore_errors=True)
    shutil.rmtree(ARTIFACTS_DIR, ignore_errors=True)

    log("building (Release, warnings as errors)")
    run(["dotnet", "build", "ExplorerTabUtility.slnx", "-c", "Release", "-warnaserror", "-v:m"])

    if not args.skip_tests:
        log("running unit tests")
        run(["dotnet", "test", "ExplorerTabUtility.Tests/ExplorerTabUtility.Tests.csproj",
             "-c", "Release", "--no-build", "-v:m"])
    else:
        log("WARNING: tests skipped")

    # 3. Publish and pack.
    log("publishing (framework-dependent)")
    run(["dotnet", "publish", "ExplorerTabUtility.App.WinUI/ExplorerTabUtility.App.WinUI.csproj",
         "-c", "Release", "-r", "win-x64", *PUBLISH_ARGS, "-o", "publish/win-x64-fd", "-v:m"])

    log("compiling the portable zip")
    run([sys.executable, "pack-portable.py"])

    log("compiling the installer")
    run([str(find_iscc(args.iscc)), "installers/installer.iss"])

    # 4. Verify what was produced.
    log("verifying")
    published_dll = PUBLISH_DIR / APP_DLL
    if not published_dll.is_file():
        raise SystemExit(f"publish produced no {APP_DLL}")

    missing = [lang for lang in SATELLITE_LANGUAGES
               if not (PUBLISH_DIR / lang / CORE_SATELLITE_DLL).is_file()]
    if missing:
        raise SystemExit(
            "satellite resources missing from the publish output: " + ", ".join(missing) +
            " (check SatelliteResourceLanguages in Directory.Build.props)"
        )

    zip_path, setup_path = artifact_paths(read_app_version())
    for path in (zip_path, setup_path):
        if not path.is_file():
            raise SystemExit(f"expected artifact not produced: {path}")

    published_md5 = md5(published_dll)
    zipped_md5 = read_zip_md5(zip_path, f"ExplorerTabUtility/{APP_DLL}")
    if published_md5 != zipped_md5:
        raise SystemExit(
            "the portable zip does not contain the published assembly:\n"
            f"  publish: {published_md5}\n  zip:     {zipped_md5}"
        )

    stamp = read_build_stamp(published_dll, read_app_version())
    if not stamp:
        raise SystemExit("no build stamp found in the published assembly (BuildStamp target missing?)")
    log(f"build stamp: {read_app_version()}+{stamp}")
    assert_documentation_only(stamp)

    print()
    print("  artifact                             size         md5")
    print(f"  {zip_path.name:36s} {zip_path.stat().st_size:>10,}  —")
    print(f"  {setup_path.name:36s} {setup_path.stat().st_size:>10,}  —")
    print(f"  {APP_DLL:36s} {published_dll.stat().st_size:>10,}  {published_md5}")
    print(f"  {CORE_DLL:36s} {(PUBLISH_DIR / CORE_DLL).stat().st_size:>10,}  {md5(PUBLISH_DIR / CORE_DLL)}")
    print()

    if not args.ship:
        log("local build complete; nothing was pushed. Re-run with --ship to publish.")
        return 0

    if dirty:
        raise SystemExit("refusing to ship from a dirty tree")

    # 5. Ship: the commit, the tag, the assets and the notes all move together.
    log(f"pushing master and moving {args.tag} to {stamp}")
    run(["git", "push", "origin", "master"])
    run(["git", "tag", "-f", args.tag, stamp])
    run(["git", "push", "--force", "origin", args.tag])

    log("replacing the release assets")
    run(["gh", "release", "upload", args.tag, str(zip_path), str(setup_path), "--clobber"])
    run(["gh", "release", "edit", args.tag, "--notes-file", "release-notes.md"])

    log("verifying the published release")
    view = output(["gh", "release", "view", args.tag, "--json",
                   "tagName,assets,body", "--jq",
                   '{tag:.tagName, assets:[.assets[]|{name,size}], bodylen:(.body|length)}'])
    print(view)

    # Content, not length. The body keeps the file's line endings, so a length comparison counts the
    # "\r" of every CRLF as a character and reports a mismatch for notes that landed word for word
    # (which is exactly what it did: 2533 vs 2499 for identical text). Comparing the text itself also
    # catches a partially-updated body, which a length check would happily accept.
    published_body = output(["gh", "release", "view", args.tag, "--json", "body", "--jq", ".body"])
    expected_body = (REPO / "release-notes.md").read_text(encoding="utf-8")

    normalize = lambda text: text.replace("\r\n", "\n").strip()   # noqa: E731 — one use, keeps the check readable
    if normalize(published_body) != normalize(expected_body):
        raise SystemExit(
            "the published release body does not match release-notes.md — `gh release edit` did not land, "
            "which is how the notes have gone stale before. Re-run it by hand and check with "
            "`gh release view`."
        )

    log(f"done: {args.tag} now points at {stamp} with freshly built assets")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
