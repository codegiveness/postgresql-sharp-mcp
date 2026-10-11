// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

return await PackageBuilder.RunAsync(args);

internal static class PackageBuilder
{
    private static readonly string[] Documents = ["README.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "SECURITY.md", "CONTRIBUTING.md"];
    private static readonly string[] Notices = ["LICENSE", "THIRD-PARTY-NOTICES.md"];
    private static readonly string[] ServerProjects = ["PostgreSqlMcp", "PostgreSqlMcp.Core", "PostgreSqlMcp.Tools"];
    private const string InstallerName = "PostgreSqlMcp.NpmInstall";

    internal static async Task<int> RunAsync(string[] args)
    {
        string? temporary = null;
        try
        {
            if (args.Length is not (1 or 3) || args[0] != "package" || (args.Length == 3 && args[1] != "--output"))
                throw new ArgumentException("Usage: dotnet run --project tools/PostgreSqlMcp.Build -c Release -- package [--output artifacts/packages]");
            var root = FindRoot();
            var output = Path.GetFullPath(args.Length == 3 ? args[2] : Path.Combine(root, "artifacts", "packages"));
            if (output == root || new[] { "src", "npm", "tools" }.Any(name => IsWithin(output, Path.Combine(root, name))))
                throw new ArgumentException("Output must not be the source root or a source/package/tooling directory.");

            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "npm", "package.json")));
            var projectMetadata = XDocument.Load(Path.Combine(root, "src", "PostgreSqlMcp", "PostgreSqlMcp.csproj"));
            var version = projectMetadata.Descendants("Version").Single().Value;
            var packageId = projectMetadata.Descendants("PackageId").Single().Value;
            var packageName = metadata.RootElement.GetProperty("name").GetString() ?? throw new InvalidDataException("npm package name is missing.");
            if (metadata.RootElement.GetProperty("version").GetString() != version)
                throw new InvalidDataException("npm/package.json version must match PostgreSqlMcp.csproj Version.");
            CheckPlatformDependencies(metadata.RootElement, packageName, version);
            var nugetName = $"{packageId}.{version}.nupkg";
            if (Path.GetFileName(NpmPlatform.TarballName(packageName, version)) != NpmPlatform.TarballName(packageName, version) || Path.GetFileName(nugetName) != nugetName)
                throw new InvalidDataException("Package names must not contain filesystem paths.");

            temporary = Directory.CreateTempSubdirectory("postgresql-mcp-package-").FullName;
            if (IsWithin(temporary, root))
                throw new InvalidOperationException("Temporary staging must be outside the source tree; configure TMPDIR/TEMP accordingly.");
            var stage = Path.Combine(temporary, "source");
            StageSources(root, stage);
            // Resolve project identities from the child working directory; macOS temp paths can alias /private/var.
            var server = Path.Combine("src", "PostgreSqlMcp", "PostgreSqlMcp.csproj");
            var installer = Path.Combine("tools", InstallerName, $"{InstallerName}.csproj");
            var tarballs = new List<string>();
            foreach (var platform in NpmPlatform.All)
            {
                // One package per platform: npm installs only the one matching os/cpu/libc, so a machine downloads one runtime.
                var name = platform.PackageName(packageName);
                var package = Path.Combine(temporary, "npm-" + platform.Rid);
                var runtime = Path.Combine(package, NpmPlatform.RuntimeDirectory);
                await DotnetAsync(stage, "publish", server, "-c", "Release", "--self-contained", "true",
                    $"-p:RuntimeIdentifier={platform.Rid}", $"-p:RuntimeIdentifiers={platform.Rid}", "-p:UseAppHost=true", "-o", runtime);
                // Both applications share the same packaged runtime, not a system installation.
                await DotnetAsync(stage, "publish", installer, "-c", "Release", "--self-contained", "true",
                    $"-p:RuntimeIdentifier={platform.Rid}", $"-p:RuntimeIdentifiers={platform.Rid}", "-p:UseAppHost=true", "-o", runtime);
                StageNotices(stage, package);
                await File.WriteAllTextAsync(Path.Combine(package, "package.json"), PlatformManifest(metadata.RootElement, name, version, platform));
                await File.WriteAllTextAsync(Path.Combine(package, "README.md"),
                    $"# {name}\n\nThe {platform.Os}-{platform.Cpu} server and bundled .NET runtime of [{packageName}](https://www.npmjs.com/package/{packageName}). " +
                    $"Install `{packageName}` instead; npm selects this package as an optional dependency on matching machines.\n");
                var tarball = Path.Combine(temporary, NpmPlatform.TarballName(name, version));
                CreateTarball(package, tarball);
                tarballs.Add(tarball);
            }

            var main = Path.Combine(temporary, "npm");
            CopyFile(Path.Combine(root, "npm", "package.json"), Path.Combine(main, "package.json"));
            foreach (var document in Documents)
                CopyFile(Path.Combine(stage, document), Path.Combine(main, document));
            StageNotices(stage, main);
            // npm creates bin links before postinstall. The seed is a real PE
            // apphost so Windows cmd-shim chooses native invocation (no shebang).
            CopyFile(Path.Combine(temporary, "npm-win-x64", NpmPlatform.RuntimeDirectory, "PostgreSqlMcp.exe"),
                Path.Combine(main, "bin", "postgresql-sharp-mcp.exe"));
            var mainTarball = Path.Combine(temporary, NpmPlatform.TarballName(packageName, version));
            CreateTarball(main, mainTarball);

            var packed = Path.Combine(temporary, "nuget");
            await DotnetAsync(stage, "pack", server, "-c", "Release", "-o", packed);
            var nupkg = Path.Combine(packed, nugetName);
            CheckNuget(nupkg);
            Directory.CreateDirectory(output);
            foreach (var artifact in tarballs.Prepend(mainTarball).Append(nupkg))
            {
                var destination = Path.Combine(output, Path.GetFileName(artifact));
                CopyFile(artifact, destination);
                Console.WriteLine(destination);
            }
            new XDocument(new XElement("configuration", new XElement("packageSources",
                new XElement("clear"), new XElement("add", new XAttribute("key", "local"), new XAttribute("value", output)))))
                .Save(Path.Combine(output, "NuGet.Config"));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"package: {exception.Message}");
            return 1;
        }
        finally
        {
            if (temporary is not null)
                Directory.Delete(temporary, recursive: true);
        }
    }

    private static string FindRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "npm", "package.json")) &&
                    File.Exists(Path.Combine(directory.FullName, "src", "PostgreSqlMcp", "PostgreSqlMcp.csproj")))
                    return directory.FullName;
        throw new DirectoryNotFoundException("Cannot find the repository root.");
    }

    private static bool IsWithin(string path, string parent)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(path, parent, comparison) || path.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
    }

    private static void StageSources(string root, string stage)
    {
        foreach (var name in new[] { "Directory.Build.props", "Directory.Packages.props", "global.json" }.Concat(Documents))
            CopyFile(Path.Combine(root, name), Path.Combine(stage, name));
        foreach (var notice in Directory.EnumerateFiles(Path.Combine(root, "LICENSES"), "*.txt"))
            CopyFile(notice, Path.Combine(stage, "LICENSES", Path.GetFileName(notice)));
        // The installer links tools/Shared/ProcessRunner.cs, so staged builds need it at the same relative path.
        foreach (var relative in ServerProjects.Select(name => Path.Combine("src", name)).Append(Path.Combine("tools", InstallerName)).Append(Path.Combine("tools", "Shared")))
            StageProject(Path.Combine(root, relative), Path.Combine(stage, relative));
    }

    private static void StageProject(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            if (Path.GetExtension(file) is ".cs" or ".csproj" || (name.StartsWith("packages", StringComparison.Ordinal) && name.EndsWith(".lock.json", StringComparison.Ordinal)))
                CopyFile(file, Path.Combine(destination, name));
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (name is "bin" or "obj" || name.StartsWith('.'))
                continue;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Source staging does not accept directory links.");
            StageProject(directory, Path.Combine(destination, name));
        }
    }

    private static void CopyFile(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Package staging does not accept file links.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    // Every npm package redistributes .NET or ships with it, so each carries the license and dependency notices.
    private static void StageNotices(string stage, string package)
    {
        foreach (var notice in Notices)
            CopyFile(Path.Combine(stage, notice), Path.Combine(package, notice));
        foreach (var notice in Directory.EnumerateFiles(Path.Combine(stage, "LICENSES"), "*.txt"))
            CopyFile(notice, Path.Combine(package, "LICENSES", Path.GetFileName(notice)));
    }

    // npm/package.json is the reviewed source: it must depend on exactly the built platform packages at this version.
    private static void CheckPlatformDependencies(JsonElement metadata, string packageName, string version)
    {
        var expected = NpmPlatform.All.Select(platform => platform.PackageName(packageName) + "@" + version).ToHashSet(StringComparer.Ordinal);
        var declared = metadata.TryGetProperty("optionalDependencies", out var dependencies)
            ? dependencies.EnumerateObject().Select(dependency => dependency.Name + "@" + dependency.Value.GetString()).ToHashSet(StringComparer.Ordinal)
            : [];
        if (!declared.SetEquals(expected))
            throw new InvalidDataException($"npm/package.json optionalDependencies must be exactly {string.Join(", ", expected.Order(StringComparer.Ordinal))}.");
        if (metadata.TryGetProperty("dependencies", out _))
            throw new InvalidDataException("npm/package.json must not declare dependencies; platform packages are optionalDependencies.");
        if (!metadata.TryGetProperty("scripts", out var scripts) || scripts.EnumerateObject().Count() != 1 ||
            !scripts.TryGetProperty("postinstall", out var postinstall) || postinstall.GetString() != NpmPlatform.InstallerCommand)
            throw new InvalidDataException($"npm/package.json scripts must be exactly postinstall: {NpmPlatform.InstallerCommand}.");
    }

    private static string PlatformManifest(JsonElement main, string name, string version, NpmPlatform platform)
    {
        var manifest = new JsonObject
        {
            ["name"] = name,
            ["version"] = version,
            ["description"] = $"{platform.Os}-{platform.Cpu} server and bundled .NET runtime for {main.GetProperty("name").GetString()}; install that package instead.",
            ["license"] = main.GetProperty("license").GetString(),
            ["author"] = main.GetProperty("author").GetString(),
            ["homepage"] = main.GetProperty("homepage").GetString(),
            ["repository"] = JsonNode.Parse(main.GetProperty("repository").GetRawText()),
            ["os"] = new JsonArray(platform.Os),
            ["cpu"] = new JsonArray(platform.Cpu)
        };
        if (platform.Libc is not null) manifest["libc"] = new JsonArray(platform.Libc);
        manifest["bin"] = new JsonObject { [NpmPlatform.InstallerCommand] = $"{NpmPlatform.RuntimeDirectory}/{platform.Executable(InstallerName)}" };
        manifest["publishConfig"] = JsonNode.Parse(main.GetProperty("publishConfig").GetRawText());
        return manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }
    private static async Task DotnetAsync(string stage, params string[] arguments)
    {
        var properties = new[]
        {
            "RestoreLockedMode=true", "EnableSourceControlManagerQueries=false", "EnableSourceLink=false", "EmbedUntrackedSources=false",
            "DebugType=None", "DebugSymbols=false", "IncludeSymbols=false", $"PathMap={stage}=/_/"
        };
        var result = await ProcessRunner.RunAsync(new ProcessSpec("dotnet", [.. arguments, .. properties.Select(property => $"-p:{property}")])
        {
            WorkingDirectory = stage,
            Timeout = Timeout.InfiniteTimeSpan,
            Output = OutputPolicy.Inherit
        });
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"dotnet {arguments[0]} failed with exit code {result.ExitCode}.");
    }

    private static void CreateTarball(string package, string destination)
    {
        using var stream = File.Create(destination);
        using var gzip = new GZipStream(stream, CompressionLevel.Optimal);
        // GNU format: .NET's PAX writer names each extended header after the process ID, which makes identical content hash differently per build.
        using var tar = new TarWriter(gzip, TarEntryFormat.Gnu);
        foreach (var file in Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(package, file).Replace(Path.DirectorySeparatorChar, '/');
            var entry = new GnuTarEntry(TarEntryType.RegularFile, $"package/{relative}")
            {
                Uid = 0,
                Gid = 0,
                UserName = "",
                GroupName = "",
                ModificationTime = DateTimeOffset.UnixEpoch,
                Mode = Path.GetFileName(relative) is "PostgreSqlMcp" or "PostgreSqlMcp.exe" or "PostgreSqlMcp.NpmInstall" or "PostgreSqlMcp.NpmInstall.exe" || relative == "bin/postgresql-sharp-mcp.exe"
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
            };
            using var data = File.OpenRead(file);
            entry.DataStream = data;
            tar.WriteEntry(entry);
        }
    }

    private static void CheckNuget(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        foreach (var document in Documents.Concat(["LICENSES/DotNet.txt", "LICENSES/DotNet-ThirdParty.txt", "LICENSES/Npgsql.txt", "LICENSES/ModelContextProtocol.txt"]))
            if (archive.GetEntry(document) is null)
                throw new InvalidDataException($"NuGet package is missing required notice {document}.");
        if (archive.Entries.Any(entry => entry.FullName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
            entry.FullName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains("PostgreSqlMcp.NpmInstall", StringComparison.Ordinal) ||
            entry.FullName.Contains("PostgreSqlMcp.Build", StringComparison.Ordinal)))
            throw new InvalidDataException("NuGet package contains source/debug files or packaging tooling.");
    }
}
