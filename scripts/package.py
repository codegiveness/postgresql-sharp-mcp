#!/usr/bin/env python3
"""Build local npm and NuGet artifacts; never publish or stage inside the source tree."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "src/PostgreSqlMcp/PostgreSqlMcp.csproj"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/packages")
    args = parser.parse_args()
    output = args.output.resolve()
    metadata = json.loads((ROOT / "npm/package.json").read_text())
    version = ET.parse(PROJECT).findtext("./PropertyGroup/Version")
    package_id = ET.parse(PROJECT).findtext("./PropertyGroup/PackageId")
    if metadata["version"] != version:
        parser.error("npm/package.json version must match PostgreSqlMcp.csproj Version")
    if output == ROOT or ROOT / "src" == output or (ROOT / "src") in output.parents or ROOT / "npm" == output or (ROOT / "npm") in output.parents:
        parser.error("output must not be the source root or a source/package directory")
    npm = shutil.which("npm")
    if not npm:
        parser.error("npm is required")
    with tempfile.TemporaryDirectory(prefix="postgresql-mcp-package-") as temporary:
        stage = Path(temporary)
        if stage == ROOT or ROOT in stage.parents:
            raise RuntimeError("Temporary staging must be outside the source tree; set TMPDIR/TEMP accordingly")
        package = stage / "npm"
        package.mkdir()
        (package / "bin").mkdir()
        shutil.copy2(ROOT / "npm/package.json", package / "package.json")
        shutil.copy2(ROOT / "npm/bin/postgresql-sharp-mcp.js", package / "bin")
        (package / "bin/postgresql-sharp-mcp.js").chmod(0o755)
        published = stage / "published"
        subprocess.run(["dotnet", "publish", str(PROJECT), "-c", "Release", "--self-contained", "false",
                        "-p:UseAppHost=false", "-o", str(published)], cwd=ROOT, check=True)
        payload = package / "payload"
        payload.mkdir()
        for source in published.glob("*.dll"):
            shutil.copy2(source, payload / source.name)
        for name in ("PostgreSqlMcp.deps.json", "PostgreSqlMcp.runtimeconfig.json"):
            shutil.copy2(published / name, payload / name)
        for name in ("README.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "SECURITY.md", "CONTRIBUTING.md"):
            shutil.copy2(ROOT / name, package / name)
        shutil.copytree(ROOT / "LICENSES", package / "LICENSES")
        subprocess.run([npm, "pack", "--ignore-scripts", "--offline", "--pack-destination", str(stage)],
                       cwd=package, check=True)
        tarball = stage / f"{metadata['name'].replace('@', '').replace('/', '-')}-{version}.tgz"
        expected = {"package/" + str(path.relative_to(package)).replace(os.sep, "/")
                    for path in package.rglob("*") if path.is_file()}
        with tarfile.open(tarball, "r:gz") as archive:
            members = archive.getmembers()
            if any(not member.isfile() for member in members) or {member.name for member in members} != expected:
                raise RuntimeError("npm pack did not retain exactly the staged package allowlist")
        nuget = stage / "nuget"
        subprocess.run(["dotnet", "pack", str(PROJECT), "-c", "Release", "--no-restore", "-o", str(nuget)],
                       cwd=ROOT, check=True)
        nupkg = nuget / f"{package_id}.{version}.nupkg"
        if not nupkg.is_file():
            raise RuntimeError("NuGet package was not produced")
        output.mkdir(parents=True, exist_ok=True)
        for artifact in (tarball, nupkg):
            destination = output / artifact.name
            shutil.copy2(artifact, destination)
            print(destination)
        config = ET.Element("configuration")
        sources = ET.SubElement(config, "packageSources")
        ET.SubElement(sources, "clear")
        ET.SubElement(sources, "add", key="local", value=str(output))
        ET.ElementTree(config).write(output / "NuGet.Config", encoding="unicode")


if __name__ == "__main__":
    main()
