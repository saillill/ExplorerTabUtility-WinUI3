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
* It verifies, per architecture, that the published shell and Core are the *same* architecture. A
  mismatched pair is otherwise silent until a user runs it: the build only raises CS8012, publish
  does not run with -warnaserror, and every other check here passes because the zip really does match
  the publish directory. It surfaces as BadImageFormatException — "the window never opens and nothing
  is logged".
* Shipping (`--ship`) is opt-in and never happens on a failed verification.

Architectures
-------------
`--arch` selects what to build. x64 is the default and is the only one the rest of this script, the
installer defaults and the CI satellite check assume; arm64 is built exactly the same way and gets its
own publish directory, zip name and installer (`_arm64` suffix), so adding it cannot rename or move
the x64 assets. `--arch all` builds both.

Usage
-----
    python tools/release.py                     # build + pack + verify x64 (no network, no git mutation)
    python tools/release.py --arch arm64        # same for arm64
    python tools/release.py --arch all          # both
    python tools/release.py --allow-dirty       # same, for a local trial build of uncommitted work
    python tools/release.py --ship              # also push, move the tag, replace the release assets
    python tools/release.py --skip-tests        # skip the unit tests (not recommended before shipping)
    python tools/release.py --skip-chocolatey   # skip the nupkg (needs the choco CLI)

Formats
-------
One run produces every distributable this project has: the portable zip and the Inno Setup installer
for each architecture, plus the Chocolatey package. The nupkg is not a GitHub release asset — it
carries no binaries, only an install script pointing at the published installer, and it is pushed to
the community feed separately — so `--ship` never uploads it, and it is skipped with a warning when
the choco CLI is not available.
"""

from __future__ import annotations

import argparse
import hashlib
import os
import re
import shutil
import struct
import subprocess
import sys
import zipfile
from dataclasses import dataclass
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
ARTIFACTS_DIR = REPO / "artifacts"
CHOCOLATEY_DIR = REPO / "packages" / "chocolatey"
PROJECTS = ("ExplorerTabUtility.App.WinUI", "ExplorerTabUtility.Core", "ExplorerTabUtility.Tests")
APP_DLL = "ExplorerTabUtility.dll"
CORE_DLL = "ExplorerTabUtility.Core.dll"

# The Chocolatey package id is this name lowercased, and it is also the base name of the installer
# (ExplorerTabUtility_v1.0.1_Setup_x64.exe). The GitHub *repository* is a third name — release URLs
# are built from it — so the two are kept apart; conflating them is what made the previous attempt to
# publish this package 404.
PUBLISHER = "saillill"
PACKAGE_NAME = "ExplorerTabUtility"
REPOSITORY = "ExplorerTabUtility-WinUI3"

# The Chocolatey feed page shows these two and nothing else, so build.ps1's fallback (the package
# name) would leave the page calling the app "ExplorerTabUtility" and explaining nothing. Kept here
# rather than in the templates: the templates are generic, this text is this package's.
CHOCOLATEY_SUMMARY = (
    "Tabs for Windows 11 File Explorer - a WinUI 3 rewrite built on the Windows App SDK."
)
CHOCOLATEY_DESCRIPTION = (
    "ExplorerTabUtility brings tabs to Windows 11 File Explorer: new folder windows open as tabs, "
    "tabs can be duplicated, detached into a window of their own, or folded back together, the last "
    "closed tab reopens, and a picker searches everything that is open.\n\n"
    "This is the WinUI 3 rewrite, built on the Windows App SDK, with nine UI languages, light and "
    "dark themes, and hotkey profiles.\n\n"
    "The package installs the x64 build (which also runs on ARM64 under emulation) and, when they "
    "are missing, downloads the .NET 10 Desktop Runtime and the Windows App Runtime. Requires "
    "Windows 11 22H2, build 22621 or later.\n\n"
    "The install is silent by default; pass --params=/interactive to run the installer wizard."
)
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

# PE machine types, as they appear in the COFF header.
IMAGE_FILE_MACHINE_AMD64 = 0x8664
IMAGE_FILE_MACHINE_ARM64 = 0xAA64
MACHINE_NAMES = {
    0x014C: "x86",
    0x01C0: "ARM",
    IMAGE_FILE_MACHINE_AMD64: "x64",
    IMAGE_FILE_MACHINE_ARM64: "ARM64",
}


@dataclass(frozen=True)
class Architecture:
    """Everything that differs between the shipped architectures, in one place.

    `platform` is None for x64 on purpose: x64 is built with no `-p:Platform` at all, which is the
    AnyCPU→x64 default path this repository has always used. Spelling out `x64` there would move every
    output into `bin\\x64\\Release\\` and break the path checks that depend on the current layout.

    `zip_suffix`/`setup_suffix` must stay aligned with pack-portable.py's ARCHITECTURES and
    installer.iss's OutputBaseFilename suffix; the "expected artifact not produced" check below is
    what turns that drift into a failure instead of a shipped file nobody can find.
    """

    name: str
    runtime: str
    platform: str | None
    publish_dir: Path
    zip_suffix: str
    setup_suffix: str
    iscc_define: str | None
    pe_machine: int


ARCHITECTURES = {
    "x64": Architecture(
        name="x64",
        runtime="win-x64",
        platform=None,
        publish_dir=REPO / "publish" / "win-x64-fd",
        zip_suffix="x64",
        setup_suffix="_x64",
        iscc_define=None,
        pe_machine=IMAGE_FILE_MACHINE_AMD64,
    ),
    "arm64": Architecture(
        name="arm64",
        runtime="win-arm64",
        platform="ARM64",
        publish_dir=REPO / "publish" / "win-arm64-fd",
        zip_suffix="arm64",
        setup_suffix="_arm64",
        iscc_define="/DMyAppArch=arm64",
        pe_machine=IMAGE_FILE_MACHINE_ARM64,
    ),
}


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


def pe_machine(path: Path) -> int:
    """The PE machine type of an assembly, straight from the COFF header."""
    with path.open("rb") as handle:
        if handle.read(2) != b"MZ":
            raise SystemExit(f"{path} is not a PE image")
        handle.seek(0x3C)
        offset = struct.unpack("<I", handle.read(4))[0]
        handle.seek(offset + 4)
        return struct.unpack("<H", handle.read(2))[0]


def artifact_paths(version: str, arch: Architecture) -> tuple[Path, Path]:
    return (
        ARTIFACTS_DIR / f"ExplorerTabUtility_v{version}_Portable_{arch.zip_suffix}.zip",
        ARTIFACTS_DIR / f"ExplorerTabUtility_v{version}_Setup{arch.setup_suffix}.exe",
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


def pack_chocolatey(version: str) -> Path | None:
    """Build the Chocolatey package, or return None when it cannot be built on this machine.

    The nupkg holds no binaries: chocolateyinstall.ps1 downloads the published installer and verifies
    its SHA256, so the package is meaningless without a published setup next to it. It is hashed from
    the *local* setup file — the same bytes `--ship` uploads — which is both faster than the script's
    default re-download and immune to hashing the previous release's artifact mid-release.

    x64 only, on purpose: the package has one install script, and the x64 build running under
    emulation is still the documented experience on ARM64 machines, so an arm64 nupkg would add a
    second package id for no functional gain yet.
    """
    interpreter = shutil.which("pwsh") or shutil.which("powershell")
    if interpreter is None:
        log("WARNING: no PowerShell interpreter found — skipping the Chocolatey package")
        return None

    setup_path = artifact_paths(version, ARCHITECTURES["x64"])[1]
    log("packing the Chocolatey package")
    run([interpreter, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
         "-File", "build.ps1",
         "-Publisher", PUBLISHER,
         "-Name", PACKAGE_NAME,
         "-Repository", REPOSITORY,
         "-Version", version,
         "-InstallerPath", str(setup_path),
         "-Summary", CHOCOLATEY_SUMMARY,
         "-Description", CHOCOLATEY_DESCRIPTION],
        cwd=CHOCOLATEY_DIR)

    nupkg = CHOCOLATEY_DIR / f"{PACKAGE_NAME.lower()}.{version}.nupkg"
    if not nupkg.is_file():
        raise SystemExit(f"build.ps1 produced no {nupkg.name} (expected in {CHOCOLATEY_DIR})")

    verify_chocolatey(nupkg)
    return nupkg


def verify_chocolatey(nupkg: Path) -> None:
    """The nupkg holds no binaries, so its only load-bearing values are two strings in the generated
    install script: the URL it downloads and the SHA256 it checks. Both are rendered from command-line
    names, and getting either wrong ships a package that fails on a *user's* machine — a 404 during
    install, or a checksum mismatch — which is precisely how the previous "Publish to Chocolatey"
    workflow died (`ExplorerTabUtility-WinUI3_v1.0.1_Setup.exe`, an asset that has never existed).

    Both are checked here against this repository's actual asset instead of trusting the templates.
    """
    version = read_app_version()
    setup = artifact_paths(version, ARCHITECTURES["x64"])[1]

    with zipfile.ZipFile(nupkg) as archive:
        script = archive.read("tools/chocolateyinstall.ps1").decode("utf-8-sig")

    expected_url = (f"https://github.com/{PUBLISHER}/{REPOSITORY}/releases/download/"
                    f"v{version}/{setup.name}")
    found_url = re.search(r"\$url\s*=\s*'([^']+)'", script)
    if found_url is None or found_url.group(1) != expected_url:
        raise SystemExit(
            f"the Chocolatey install script points at "
            f"{found_url.group(1) if found_url else 'no URL at all'}, expected {expected_url}. "
            f"A wrong repository or asset name here 404s on the user's machine."
        )

    expected_hash = hashlib.sha256(setup.read_bytes()).hexdigest()
    found_hash = re.search(r"\$checksum\s*=\s*'([0-9a-fA-F]+)'", script)
    if found_hash is None or found_hash.group(1).lower() != expected_hash:
        raise SystemExit(
            f"the Chocolatey package checksum does not describe {setup.name}: "
            f"{found_hash.group(1) if found_hash else 'none'} != {expected_hash}"
        )

    log(f"Chocolatey package: {nupkg.name} -> {expected_url}")


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


def assert_matching_architectures(arch: Architecture) -> None:
    """The shell and Core must be the same architecture, and the one this build claims.

    This is the check whose absence let a mixed pair through: `dotnet publish -r win-x64` still exits
    0 when Core is pinned to another architecture, emitting an x64 shell next to an ARM64 Core, with
    CS8012 as the only signal. Every other verification here compares files to each other, so they all
    pass on a package that cannot start.
    """
    shell = pe_machine(arch.publish_dir / APP_DLL)
    core = pe_machine(arch.publish_dir / CORE_DLL)
    described = {shell: MACHINE_NAMES.get(shell, hex(shell)), core: MACHINE_NAMES.get(core, hex(core))}

    if shell != core:
        raise SystemExit(
            f"{arch.name}: the published {APP_DLL} is {described[shell]} but {CORE_DLL} is "
            f"{described[core]}. The shell and Core must be built for the same architecture — check "
            f"that no csproj pins PlatformTarget/RuntimeIdentifier without a $(Platform) condition. "
            f"Shipping this would fail at run time as BadImageFormatException, with nothing in the log."
        )

    if shell != arch.pe_machine:
        raise SystemExit(
            f"{arch.name}: the published {APP_DLL} is {described[shell]}, expected "
            f"{MACHINE_NAMES[arch.pe_machine]} for this architecture."
        )

    log(f"{arch.name}: {APP_DLL} and {CORE_DLL} are both {described[shell]}")


def verify(arch: Architecture, version: str) -> tuple[Path, Path, str]:
    """Everything that must hold before an architecture's artifacts may be shipped.

    Returns the zip, the installer and the source revision baked into the published assembly.
    """
    published_dll = arch.publish_dir / APP_DLL
    if not published_dll.is_file():
        raise SystemExit(f"publish produced no {APP_DLL} for {arch.name}")

    missing = [lang for lang in SATELLITE_LANGUAGES
               if not (arch.publish_dir / lang / CORE_SATELLITE_DLL).is_file()]
    if missing:
        raise SystemExit(
            f"{arch.name}: satellite resources missing from the publish output: " + ", ".join(missing) +
            " (check SatelliteResourceLanguages in Directory.Build.props)"
        )

    zip_path, setup_path = artifact_paths(version, arch)
    for path in (zip_path, setup_path):
        if not path.is_file():
            raise SystemExit(f"{arch.name}: expected artifact not produced: {path}")

    published_md5 = md5(published_dll)
    zipped_md5 = read_zip_md5(zip_path, f"ExplorerTabUtility/{APP_DLL}")
    if published_md5 != zipped_md5:
        raise SystemExit(
            f"{arch.name}: the portable zip does not contain the published assembly:\n"
            f"  publish: {published_md5}\n  zip:     {zipped_md5}"
        )

    assert_matching_architectures(arch)

    stamp = read_build_stamp(published_dll, version)
    if not stamp:
        raise SystemExit(
            f"{arch.name}: no build stamp found in the published assembly (BuildStamp target missing?)"
        )
    log(f"{arch.name} build stamp: {version}+{stamp}")
    assert_documentation_only(stamp)

    return zip_path, setup_path, stamp


def main() -> int:
    parser = argparse.ArgumentParser(description="Build, pack and optionally publish ExplorerTabUtility.")
    parser.add_argument("--ship", action="store_true",
                        help="push, move the release tag and replace the published assets")
    parser.add_argument("--tag", default="v1.0.1", help="release tag to move/update (default: v1.0.1)")
    parser.add_argument("--arch", choices=("x64", "arm64", "all"), default="x64",
                        help="architecture(s) to build and pack (default: x64)")
    parser.add_argument("--skip-tests", action="store_true", help="skip the unit test run")
    parser.add_argument("--skip-chocolatey", action="store_true",
                        help="skip the Chocolatey package (it needs the choco CLI)")
    parser.add_argument("--allow-dirty", action="store_true",
                        help="build a local trial from uncommitted changes (cannot be combined with --ship)")
    parser.add_argument("--iscc", default=None, help="path to ISCC.exe")
    args = parser.parse_args()

    if args.ship and args.allow_dirty:
        raise SystemExit("--ship and --allow-dirty contradict each other: a shipped binary must come from a commit.")

    arches = [ARCHITECTURES["x64"], ARCHITECTURES["arm64"]] if args.arch == "all" \
        else [ARCHITECTURES[args.arch]]

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

    # The gate runs on the default platform on purpose: that is the x64 build this repository ships,
    # and the layout the path checks below depend on. The arm64 equivalent lives in build.yml on a
    # windows-11-arm runner — an ARM64 test assembly cannot start on this x64 host.
    log("building (Release, warnings as errors)")
    run(["dotnet", "build", "ExplorerTabUtility.slnx", "-c", "Release", "-warnaserror", "-v:m"])

    if not args.skip_tests:
        log("running unit tests")
        run(["dotnet", "test", "ExplorerTabUtility.Tests/ExplorerTabUtility.Tests.csproj",
             "-c", "Release", "--no-build", "-v:m"])
    else:
        log("WARNING: tests skipped")

    # 3. Publish and pack, once per architecture. Each one gets its own publish directory, zip name and
    #    installer, so building arm64 cannot rename or move anything the x64 assets already rely on.
    version = read_app_version()
    artifacts: list[Path] = []
    stamps: dict[str, str] = {}

    for arch in arches:
        log(f"publishing {arch.name} (framework-dependent)")
        publish = [
            "dotnet", "publish", "ExplorerTabUtility.App.WinUI/ExplorerTabUtility.App.WinUI.csproj",
            "-c", "Release", "-r", arch.runtime,
        ]
        if arch.platform:
            publish.append(f"-p:Platform={arch.platform}")
        publish += [*PUBLISH_ARGS, "-o", str(arch.publish_dir.relative_to(REPO)), "-v:m"]
        run(publish)

        log(f"compiling the portable zip ({arch.name})")
        run([sys.executable, "pack-portable.py", "--arch", arch.name])

        log(f"compiling the installer ({arch.name})")
        iscc = [str(find_iscc(args.iscc)), "installers/installer.iss"]
        if arch.iscc_define:
            # ISPP honours /D on the command line; installer.iss keeps an x64 default so a hand-run
            # ISCC without it still produces exactly the historical package.
            iscc.append(arch.iscc_define)
        run(iscc)

        log(f"verifying {arch.name}")
        zip_path, setup_path, stamp = verify(arch, version)
        artifacts += [zip_path, setup_path]
        stamps[arch.name] = stamp

    # 4. The Chocolatey package. It is not a release asset — it only points at the published installer
    #    — so it lives outside `artifacts` and `--ship` never uploads it. It needs the x64 setup, which
    #    is why an arm64-only run skips it.
    chocolatey: Path | None = None
    if args.skip_chocolatey:
        log("WARNING: the Chocolatey package was skipped on request")
    elif "x64" in stamps:
        chocolatey = pack_chocolatey(version)
    else:
        log("no x64 build in this run — skipping the Chocolatey package (it installs the x64 setup)")

    print()
    print("  artifact                             size         md5")
    for arch in arches:
        zip_path, setup_path = artifact_paths(version, arch)
        published_dll = arch.publish_dir / APP_DLL
        print(f"  [{arch.name}]")
        print(f"  {zip_path.name:36s} {zip_path.stat().st_size:>10,}  —")
        print(f"  {setup_path.name:36s} {setup_path.stat().st_size:>10,}  —")
        print(f"  {APP_DLL:36s} {published_dll.stat().st_size:>10,}  {md5(published_dll)}")
        print(f"  {CORE_DLL:36s} {(arch.publish_dir / CORE_DLL).stat().st_size:>10,}  "
              f"{md5(arch.publish_dir / CORE_DLL)}")
    if chocolatey is not None:
        print(f"  {chocolatey.name:36s} {chocolatey.stat().st_size:>10,}  —  (Chocolatey, not a release asset)")
    print()

    if chocolatey is not None:
        if args.ship:
            log(f"the Chocolatey package is not a release asset — push it once this run has finished: "
                f"choco push {chocolatey.name} --source=https://push.chocolatey.org/ --api-key=<key>")
        else:
            log(f"WARNING: {chocolatey.name} carries the checksum of the *locally* built setup. Push it "
                f"only after those exact bytes are on the release (i.e. after a --ship run); otherwise "
                f"every install fails its checksum on the user's machine.")

    if not args.ship:
        log("local build complete; nothing was pushed. Re-run with --ship to publish.")
        return 0

    if dirty:
        raise SystemExit("refusing to ship from a dirty tree")

    # 5. Ship: the commit, the tag, the assets and the notes all move together.
    #
    # Every step is recorded, and a failure reports how far it got. Without this a rejected push (the
    # remote being ahead is normal here — the repository owner edits files in the GitHub web UI) surfaced
    # as a bare traceback, leaving it unclear whether the tag had moved and whether the assets were
    # replaced.
    # One commit built every architecture, so the tag can move to exactly one revision.
    distinct = set(stamps.values())
    if len(distinct) != 1:
        raise SystemExit(f"refusing to ship: the architectures were built from different commits: {stamps}")
    stamp = distinct.pop()

    completed: list[str] = []
    steps: list[tuple[str, list[str]]] = [
        ("push master", ["git", "push", "origin", "master"]),
        (f"move {args.tag} to {stamp}", ["git", "tag", "-f", args.tag, stamp]),
        (f"push {args.tag}", ["git", "push", "--force", "origin", args.tag]),
        # gh's --clobber deletes *every* existing asset before uploading, it does not merge by name
        # (see `gh release upload --help`: "existing assets are deleted before new assets are
        # uploaded. If the upload fails, the original assets will be lost"). So this step defines the
        # release's asset set exactly: a renamed artifact leaves nothing stale behind, and anything
        # added to the release by hand disappears on the next --ship.
        ("replace the release assets",
         ["gh", "release", "upload", args.tag, *[str(path) for path in artifacts], "--clobber"]),
        ("update the release notes", ["gh", "release", "edit", args.tag, "--notes-file", "release-notes.md"]),
    ]

    for index, (description, command) in enumerate(steps):
        log(description)
        try:
            run(command)
        except subprocess.CalledProcessError as ex:
            remaining = [name for name, _ in steps[index:]]
            raise SystemExit(
                f"\nshipping stopped at '{description}' (exit {ex.returncode})\n"
                f"  completed : {', '.join(completed) or 'nothing'}\n"
                f"  NOT done  : {', '.join(remaining)}\n"
                f"  the local artifacts are complete — fix the cause and re-run with --ship; the steps "
                f"above are idempotent. `gh release view {args.tag}` shows what is published right now."
            ) from ex

        completed.append(description)

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

    log(f"done: {args.tag} now points at {stamp} with freshly built assets "
        f"({', '.join(arch.name for arch in arches)})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
