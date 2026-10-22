"""Package the learning fork on macOS; never reads installed client/user configuration."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import plistlib
import shutil
import struct
import subprocess
import sys
import tempfile
import urllib.request
import urllib.error
import zipfile

SOURCES = {
    "Xray-macos-arm64-v8a.zip": ("https://github.com/XTLS/Xray-core/releases/download/v26.10.10/Xray-macos-arm64-v8a.zip", "7b1711eaeefd90f27082cf49c311aa57dad44ac13146faab3f0c1601158ea6c8"),
    "geoip.dat": ("https://github.com/Loyalsoldier/v2ray-rules-dat/releases/download/202610202206/geoip.dat", "4a1b1eddabf5614fa51dd93ac0498856cb82dd65933cd6d6d5890d8c7bb5e902"),
    "geosite.dat": ("https://github.com/Loyalsoldier/v2ray-rules-dat/releases/download/202610202206/geosite.dat", "9a4f9f84cf6201cf135afbc9d67190359cdc28e4e02e2e9f3e2b359cac317ed2"),
}

# The upstream release can disappear. Reuse exactly the already published core,
# never an unpinned latest release. The prior bundle has an ad-hoc signed Xray.
FALLBACK_NAME = "v2rayN-learning-macos-arm64-977fc6e1.zip"
FALLBACK_URL = "https://github.com/ternary-chen/v2rayN/releases/download/v7.25.6-learning.4/" + FALLBACK_NAME
FALLBACK_SHA = "ad406d0fa974025f6c1c2fbeb1f9c72b0d4dba6d8b966339ac3170725dffd9eb"
FALLBACK_FILES = {"xray": "xray/xray", "geoip.dat": "geoip.dat", "geosite.dat": "geosite.dat"}

def check_fallback(folder, extract=False):
    archive = folder / FALLBACK_NAME
    if digest(archive) != FALLBACK_SHA:
        raise ValueError("Prior release bundle checksum mismatch")
    with zipfile.ZipFile(archive) as z:
        for name, relative in FALLBACK_FILES.items():
            data = z.read("v2rayN Learning.app/Contents/Resources/bin/" + relative)
            if extract:
                (folder / name).write_bytes(data)
            elif hashlib.sha256(data).hexdigest() != digest(folder / name):
                raise ValueError(f"Prior release dependency mismatch: {name}")
    for name in ("geoip.dat", "geosite.dat"):
        if digest(folder / name) != SOURCES[name][1]:
            raise ValueError(f"Pinned geo data mismatch: {name}")


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()

def check_sources(folder):
    if (folder / FALLBACK_NAME).exists():
        check_fallback(folder)
        return
    for name, (_, expected) in SOURCES.items():
        if digest(folder / name) != expected:
            raise ValueError(f"Checksum mismatch: {name}")

def download_original(folder):
    folder.mkdir(parents=True, exist_ok=True)
    for name, (url, expected) in SOURCES.items():
        path = folder / name
        if not path.exists() or digest(path) != expected:
            request = urllib.request.Request(url, headers={"User-Agent": "v2rayN-learning-build"})
            with urllib.request.urlopen(request, timeout=120) as response, path.open("wb") as target:
                shutil.copyfileobj(response, target)
    check_sources(folder)
    with zipfile.ZipFile(folder / "Xray-macos-arm64-v8a.zip") as archive:
        # Extract only the expected executable, never arbitrary paths from an archive.
        (folder / "xray").write_bytes(archive.read("xray"))
    (folder / "xray").chmod(0o755)

def download(folder):
    try:
        download_original(folder)
    except urllib.error.HTTPError as error:
        if error.code != 404:
            raise
        print("Pinned upstream dependency is unavailable; verifying prior release dependencies.")
        archive = folder / FALLBACK_NAME
        if not archive.exists() or digest(archive) != FALLBACK_SHA:
            request = urllib.request.Request(FALLBACK_URL, headers={"User-Agent": "v2rayN-learning-build"})
            with urllib.request.urlopen(request, timeout=120) as response, archive.open("wb") as target:
                shutil.copyfileobj(response, target)
        check_fallback(folder, extract=True)
        (folder / "xray").chmod(0o755)


def ensure_arm64(path):
    with path.open("rb") as stream:
        magic, cpu = struct.unpack("<II", stream.read(8))
    if magic != 0xFEEDFACF or cpu != 0x0100000C:
        raise ValueError(f"Not an ARM64 Mach-O executable: {path.name}")

def package(publish, dependencies, output, commit, helper):
    if sys.platform != "darwin":
        raise RuntimeError("Final packaging requires macOS codesign; do not distribute an unvalidated Windows-created app bundle")
    check_sources(dependencies)
    if not (dependencies / FALLBACK_NAME).exists():
        with zipfile.ZipFile(dependencies / "Xray-macos-arm64-v8a.zip") as archive:
            if hashlib.sha256(archive.read("xray")).hexdigest() != digest(dependencies / "xray"):
                raise ValueError("Extracted Xray does not match verified archive")
    ensure_arm64(publish / "v2rayN")
    ensure_arm64(helper)
    ensure_arm64(dependencies / "xray")
    output.mkdir(parents=True, exist_ok=True)
    repo = Path(__file__).resolve().parents[1]
    with tempfile.TemporaryDirectory(prefix="learning-package-", dir=output) as temporary:
        stage = Path(temporary)
        app = stage / "v2rayN Learning.app"
        contents = app / "Contents"
        macos = contents / "MacOS"
        shutil.copytree(publish, macos)
        shutil.copy2(helper, macos / "CustomUpdater")
        for path in macos.rglob("*"):
            if path.name.lower() in {"guiconfigs", "guilogs", "guibackups", "binconfigs", "auth.json", ".env"}:
                raise ValueError("Private runtime data in publish directory")
        resources = contents / "Resources"
        resources.mkdir()
        shutil.copy2(macos / "v2rayN.icns", resources / "AppIcon.icns")
        (macos / "NotStoreConfigHere.txt").write_text("Store configuration outside the application bundle.\n", encoding="utf-8")
        # Keep data out of Contents/MacOS, where codesign treats subdirectories as code.
        core_bin = resources / "bin"
        (core_bin / "xray").mkdir(parents=True, exist_ok=True)
        (macos / "bin").symlink_to("../Resources/bin", target_is_directory=True)
        shutil.copy2(dependencies / "xray", core_bin / "xray" / "xray")
        for name in ("geoip.dat", "geosite.dat"):
            shutil.copy2(dependencies / name, core_bin / name)
        frameworks = contents / "Frameworks"
        frameworks.mkdir()
        # Use the documented bundle layout, retaining relative links for .NET probing.
        for file in list(macos.iterdir()):
            if not file.is_file() or file.is_symlink() or file.name in {"v2rayN", "CustomUpdater"}:
                continue
            destination = frameworks if file.suffix == ".dylib" else resources
            shutil.move(file, destination / file.name)
            file.symlink_to(Path("..") / destination.name / file.name)
        info = {
            "CFBundleName": "v2rayN Learning", "CFBundleDisplayName": "v2rayN Learning",
            "CFBundleIdentifier": "com.ternarychen.v2rayn.learning", "CFBundleExecutable": "v2rayN",
            "CFBundlePackageType": "APPL", "CFBundleVersion": "7.25.6", "CFBundleShortVersionString": "7.25.6",
            "CFBundleIconFile": "AppIcon.icns", "NSHighResolutionCapable": True,
            "LSMinimumSystemVersion": "15.0", "CFBundleLocalizations": ["zh-Hans", "en"],
        }
        (contents / "Info.plist").write_bytes(plistlib.dumps(info))
        entitlements = stage / "entitlements.plist"
        entitlements.write_bytes(plistlib.dumps({"com.apple.security.cs.allow-jit": True}))
        # Ad-hoc signing ensures ARM64 code integrity; this is not Developer ID/notarization.
        for binary in sorted(contents.rglob("*"), key=lambda p: p == macos / "v2rayN"):
            if not binary.is_file() or binary.is_symlink():
                continue
            with binary.open("rb") as stream:
                magic = stream.read(4)
            if magic in (b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf", b"\xca\xfe\xba\xbe", b"\xca\xfe\xba\xbf"):
                binary.chmod(0o755)
                subprocess.run(["codesign", "--force", "--sign", "-", "--entitlements", str(entitlements), str(binary)], check=True)
        subprocess.run(["codesign", "--force", "--sign", "-", "--entitlements", str(entitlements), str(app)], check=True)
        subprocess.run(["codesign", "--verify", "--deep", "--strict", "--verbose=2", str(app)], check=True)
        shutil.copytree(repo / ".agents", stage / ".agents")
        shutil.copytree(repo / "docs", stage / "docs")
        shutil.copy2(repo / "LICENSE", stage / "LICENSE")
        shutil.copy2(repo / "docs" / "macos-learning.md", stage / "READ-ME-FIRST.md")
        manifest = {"commit": commit, "runtime": "osx-arm64", "minimum_macos": "15.0", "signing": "ad-hoc; not notarized", "dependencies": {k: {"url": v[0], "sha256": v[1]} for k, v in SOURCES.items()}}
        if (dependencies / FALLBACK_NAME).exists():
            manifest["dependency_fallback"] = {"url": FALLBACK_URL, "sha256": FALLBACK_SHA, "files": {name: digest(dependencies / name) for name in FALLBACK_FILES}}
        (stage / "build-info.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
        entitlements.unlink()
        archive = output / f"v2rayN-learning-macos-arm64-{commit[:8]}.zip"
        subprocess.run(["ditto", "-c", "-k", "--sequesterRsrc", str(stage), str(archive)], check=True)
        with zipfile.ZipFile(archive) as bundle:
            if bundle.testzip():
                raise ValueError("Packaged ZIP failed CRC validation")
            assert "v2rayN Learning.app/Contents/MacOS/v2rayN" in bundle.namelist()
            assert (bundle.getinfo("v2rayN Learning.app/Contents/MacOS/v2rayN").external_attr >> 16) & 0o111
        (output / "SHA256SUMS-macos.txt").write_text(f"{digest(archive)}  {archive.name}\n", encoding="ascii")
        print(archive)

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    dl = commands.add_parser("download"); dl.add_argument("--directory", type=Path, required=True)
    pack = commands.add_parser("package")
    for name in ("publish", "dependencies", "output"):
        pack.add_argument("--"+name, type=Path, required=True)
    pack.add_argument("--commit", required=True)
    pack.add_argument("--helper", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "download":
        download(args.directory)
    else:
        package(args.publish, args.dependencies, args.output, args.commit, args.helper)
