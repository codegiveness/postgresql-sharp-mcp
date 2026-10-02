#!/usr/bin/env python3
"""Install generated npm/NuGet packages offline and exercise their real CLI and MCP surface."""
import argparse
import asyncio
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile
import tarfile

ROOT = Path(__file__).resolve().parents[1]


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def run(command, env, expected=0):
    result = subprocess.run(command, env=env, capture_output=True, text=True, timeout=45)
    require(result.returncode == expected, f"Command exit status {result.returncode}; expected {expected}")
    return result


async def mcp(command, env, expected_targets, terminate=False, query=False):
    process = await asyncio.create_subprocess_exec(*command, env=env, stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
    errors = asyncio.create_task(process.stderr.read())
    next_id = 0

    async def send(message):
        process.stdin.write((json.dumps(message) + "\n").encode())
        await process.stdin.drain()

    async def request(method, params):
        nonlocal next_id
        next_id += 1
        await send({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params})
        while True:
            line = await asyncio.wait_for(process.stdout.readline(), 20)
            require(bool(line), "MCP process exited before responding")
            response = json.loads(line)
            require(response.get("jsonrpc") == "2.0", "Non-protocol stdout from packaged application")
            if response.get("id") == next_id:
                require("result" in response, f"MCP request failed: {method}")
                return response["result"]

    try:
        initialized = await request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "package-smoke", "version": "1"}})
        require("serverInfo" in initialized, "Initialization did not return server information")
        await send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        tools = await request("tools/list", {})
        require("list_databases" in {tool["name"] for tool in tools["tools"]}, "Missing list_databases tool")
        result = await request("tools/call", {"name": "list_databases", "arguments": {}})
        require(not result.get("isError"), "list_databases failed")
        data = result["structuredContent"]
        require(sorted(data["targets"]) == sorted(expected_targets), "Installed package returned incorrect target aliases")
        require(data["access_mode"] == "restricted", "Installed package did not retain restricted access")
        require(json.loads(result["content"][0]["text"]) == data, "MCP text and structured results disagree")
        if query:
            for target in expected_targets:
                result = await request("tools/call", {"name": "execute_sql", "arguments": {
                    "database": target, "sql": "SELECT current_database() AS database, 42 AS answer", "limit": 1}})
                require(not result.get("isError"), "Installed package database query failed")
                page = result["structuredContent"]
                require(page["database"] == target and [column["name"] for column in page["columns"]] == ["database", "answer"],
                        "Installed package query returned incorrect target or columns")
                require(len(page["rows"]) == 1 and isinstance(page["rows"][0][0], str) and page["rows"][0][1] == 42,
                        "Installed package database query returned incorrect values")
        if terminate:
            process.send_signal(signal.SIGTERM)
        else:
            process.stdin.close()
        await asyncio.wait_for(process.wait(), 10)
        # An orphaned child would keep inherited protocol pipes open after its launcher exits.
        remainder = await asyncio.wait_for(process.stdout.read(), 5)
        for line in remainder.splitlines():
            require(json.loads(line).get("jsonrpc") == "2.0", "Non-protocol stdout during shutdown")
        require(process.returncode == 0 or (terminate and process.returncode == -signal.SIGTERM), "Packaged application failed during shutdown")
        await asyncio.wait_for(errors, 5)
    finally:
        if process.returncode is None:
            process.terminate()
            try:
                await asyncio.wait_for(process.wait(), 5)
            except asyncio.TimeoutError:
                process.kill()
                await process.wait()
        if not errors.done():
            errors.cancel()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", type=Path, default=ROOT / "artifacts/packages")
    parser.add_argument("--targets-file", type=Path, help="Optional disposable fixture: validate and execute SELECT current_database() on every target")
    args = parser.parse_args()
    metadata = json.loads((ROOT / "npm/package.json").read_text())
    project = ET.parse(ROOT / "src/PostgreSqlMcp/PostgreSqlMcp.csproj")
    version = project.findtext("./PropertyGroup/Version")
    require(metadata["version"] == version, "npm and NuGet versions disagree")
    artifacts = args.artifacts.resolve()
    tarball = artifacts / f"{metadata['name'].replace('@', '').replace('/', '-')}-{version}.tgz"
    nupkg = artifacts / f"{project.findtext('./PropertyGroup/PackageId')}.{version}.nupkg"
    require(tarball.is_file() and nupkg.is_file(), "Build both artifacts with scripts/package.py first")
    legal = {"LICENSE", "THIRD-PARTY-NOTICES.md"} | {f"LICENSES/{path.name}" for path in (ROOT / "LICENSES").glob("*.txt")}
    workspace_paths = {str(ROOT).encode(), ROOT.as_posix().encode(), str(ROOT).replace("/", "\\").encode()}
    with tarfile.open(tarball, "r:gz") as archive:
        require({"package/" + name for name in legal} <= set(archive.getnames()), "npm artifact lost legal notices")
        for member in archive.getmembers():
            if member.isfile():
                content = archive.extractfile(member).read()
                require(not any(path in content for path in workspace_paths),
                        "npm artifact exposes a build-machine workspace path")
    with zipfile.ZipFile(nupkg) as archive:
        require(legal <= set(archive.namelist()), "NuGet artifact lost legal notices")
        for member in archive.namelist():
            content = archive.read(member)
            require(not any(path in content for path in workspace_paths),
                    "NuGet artifact exposes a build-machine workspace path")
    npm, node, dotnet = (shutil.which(name) for name in ("npm", "node", "dotnet"))
    require(all((npm, node, dotnet)), "npm, Node >=22 and .NET 10 SDK are required")
    env = {key: value for key, value in os.environ.items() if not key.upper().startswith("POSTGRES_")}
    env.update({"DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1"})
    with tempfile.TemporaryDirectory(prefix="postgresql-mcp-package-smoke-") as temporary:
        stage = Path(temporary)
        env.update({"NUGET_PACKAGES": str(stage / "nuget-cache"), "npm_config_cache": str(stage / "npm-cache")})
        npm_home, tool_home = stage / "npm", stage / "tools"
        npm_home.mkdir()
        (npm_home / "package.json").write_text('{"private":true}')
        run([npm, "install", "--prefix", str(npm_home), "--offline", "--ignore-scripts", "--no-audit", "--no-fund",
             "--package-lock=false", str(tarball)], env)
        config = artifacts / "NuGet.Config"
        require(config.is_file(), "Build the local-only NuGet.Config with scripts/package.py first")
        run([dotnet, "tool", "install", project.findtext("./PropertyGroup/PackageId"), "--version", version,
             "--tool-path", str(tool_home), "--configfile", str(config), "--no-cache"], env)
        launcher = npm_home / "node_modules" / metadata["name"] / "bin/postgresql-sharp-mcp.js"
        npm_bin = npm_home / "node_modules/.bin" / ("postgresql-sharp-mcp.cmd" if os.name == "nt" else "postgresql-sharp-mcp")
        require(npm_bin.is_file(), "npm did not install the command")
        commands = {"npm": [node, str(launcher)], "NuGet": [str(tool_home / ("postgresql-sharp-mcp.exe" if os.name == "nt" else "postgresql-sharp-mcp"))]}
        missing_runtime = dict(env, PATH=str(stage / "missing-path"))
        missing = run(commands["npm"] + ["--help"], missing_runtime, expected=1)
        require(not missing.stdout and bool(missing.stderr), "Unavailable runtime must fail cleanly on stderr only")
        # A bound, non-listening local port gives deterministic connection failure without an external service.
        with socket.socket() as unavailable:
            unavailable.bind(("127.0.0.1", 0))
            configured = dict(env, POSTGRES_TARGETS=json.dumps({"package_smoke":
                f"Host=127.0.0.1;Port={unavailable.getsockname()[1]};Database=package_smoke;Username=package_smoke;Password=disposable;Timeout=1"}))
            for name, command in commands.items():
                run(command + ["--help"], env)
                version_result = run(command + ["--version"], env)
                require(version_result.stdout.strip().split()[-1] in (version, version + ".0") and not version_result.stderr, f"{name} version mismatch")
                invalid = run(command, env, expected=1)
                require(not invalid.stdout and bool(invalid.stderr), f"{name} configuration failure contaminated stdout")
                validation = run(command + ["--validate"], configured, expected=1)
                require(not validation.stdout, f"{name} validation contaminated stdout")
                reports = [json.loads(line) for line in validation.stderr.splitlines()]
                require(any(report.get("error", {}).get("code") in ("connection_error", "timeout") for report in reports), f"{name} validation did not diagnose unavailable target")
                asyncio.run(mcp(command, configured, ["package_smoke"]))
                if name == "npm" and os.name != "nt":
                    asyncio.run(mcp(command, configured, ["package_smoke"], terminate=True))
                if args.targets_file:
                    fixture_env = dict(env, POSTGRES_TARGETS_FILE=str(args.targets_file.resolve()))
                    aliases = list(json.loads(args.targets_file.read_text()))
                    validated = run(command + ["--validate"], fixture_env)
                    require(not validated.stdout, f"{name} successful validation contaminated stdout")
                    reports = [json.loads(line) for line in validated.stderr.splitlines()]
                    require(len(reports) == len(aliases) and all("error" not in report for report in reports), f"{name} did not validate every fixture target")
                    asyncio.run(mcp(command, fixture_env, aliases, query=True))
                    print(f"{name}: every fixture target validated and execute_sql SELECT current_database() passed")
                print(f"{name}: installed CLI, validation, MCP initialize/tools/list/list_databases and shutdown passed")
        print("Package smoke passed; installation used only local artifacts, and missing runtime failed on stderr.")


if __name__ == "__main__":
    main()
