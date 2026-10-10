using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

return await PackageBuilder.RunAsync(args);

internal static class PackageBuilder
{
    private static readonly string[] RuntimeIdentifiers = ["win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];
    private static readonly string[] Documents = ["README.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "SECURITY.md", "CONTRIBUTING.md"];
    private static readonly string[] ServerProjects = ["PostgreSqlMcp", "PostgreSqlMcp.Core", "PostgreSqlMcp.Tools"];
    private const string InstallerName = "PostgreSqlMcp.NpmInstall";
    private const string RunnerUrl = "https://registry.npmjs.org/run-script-os/-/run-script-os-1.1.6.tgz";
    private const string RunnerSha512 = "aa5e8fd8bce10534c37f32adb3e428e07f785542a9c4a0c5cfa431c7069464dd26c2f8bb2f7969388ec1a8f0aaee58038775cb974769797c1f715222b65ad8af";

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
            var tarballName = $"{packageName.Replace("@", "", StringComparison.Ordinal).Replace('/', '-')}-{version}.tgz";
            var nugetName = $"{packageId}.{version}.nupkg";
            if (Path.GetFileName(tarballName) != tarballName || Path.GetFileName(nugetName) != nugetName)
                throw new InvalidDataException("Package names must not contain filesystem paths.");

            temporary = Directory.CreateTempSubdirectory("postgresql-mcp-package-").FullName;
            if (IsWithin(temporary, root))
                throw new InvalidOperationException("Temporary staging must be outside the source tree; configure TMPDIR/TEMP accordingly.");
            var stage = Path.Combine(temporary, "source");
            StageSources(root, stage);
            // Resolve project identities from the child working directory; macOS temp paths can alias /private/var.
            var server = Path.Combine("src", "PostgreSqlMcp", "PostgreSqlMcp.csproj");
            var installer = Path.Combine("tools", InstallerName, $"{InstallerName}.csproj");
            var package = Path.Combine(temporary, "npm");
            Directory.CreateDirectory(package);
            CopyFile(Path.Combine(root, "npm", "package.json"), Path.Combine(package, "package.json"));
            foreach (var document in Documents)
                CopyFile(Path.Combine(stage, document), Path.Combine(package, document));
            foreach (var notice in Directory.EnumerateFiles(Path.Combine(stage, "LICENSES"), "*.txt"))
                CopyFile(notice, Path.Combine(package, "LICENSES", Path.GetFileName(notice)));

            await StageRunnerAsync(package);
            foreach (var rid in RuntimeIdentifiers)
            {
                // uname supplies these architecture names to the Unix npm scripts.
                var folder = rid.Replace("x64", "x86_64", StringComparison.Ordinal)
                    .Replace("linux-arm64", "linux-aarch64", StringComparison.Ordinal);
                if (rid == "win-x64") folder = rid;
                var native = Path.Combine(package, "runtimes", folder);
                await DotnetAsync(stage, "publish", server, "-c", "Release", "--self-contained", "true",
                    $"-p:RuntimeIdentifier={rid}", $"-p:RuntimeIdentifiers={rid}", "-p:UseAppHost=true", "-o", native);
                // Both applications share the same packaged runtime, not a system installation.
                await DotnetAsync(stage, "publish", installer, "-c", "Release", "--self-contained", "true",
                    $"-p:RuntimeIdentifier={rid}", $"-p:RuntimeIdentifiers={rid}", "-p:UseAppHost=true", "-o", native);
            }

            // npm creates bin links before postinstall. The seed is a real PE
            // apphost so Windows cmd-shim chooses native invocation (no shebang).
            CopyFile(Path.Combine(package, "runtimes", "win-x64", "PostgreSqlMcp.exe"),
                Path.Combine(package, "bin", "postgresql-sharp-mcp.exe"));
            var tarball = Path.Combine(temporary, tarballName);
            CreateTarball(package, tarball);

            var packed = Path.Combine(temporary, "nuget");
            await DotnetAsync(stage, "pack", server, "-c", "Release", "-o", packed);
            var nupkg = Path.Combine(packed, nugetName);
            CheckNuget(nupkg);
            Directory.CreateDirectory(output);
            foreach (var artifact in new[] { tarball, nupkg })
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
    private static async Task StageRunnerAsync(string package)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        byte[] bytes = await client.GetByteArrayAsync(RunnerUrl);
        if (!Convert.ToHexString(SHA512.HashData(bytes)).Equals(RunnerSha512, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The pinned run-script-os archive checksum does not match.");
        using var stream = new MemoryStream(bytes);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var required = new HashSet<string>(["index.js", "functions.js", "package.json", "LICENSE"], StringComparer.Ordinal);
        while (await tar.GetNextEntryAsync() is { } entry)
        {
            if (entry.Name == "package/README.md") continue;
            var name = entry.Name.StartsWith("package/", StringComparison.Ordinal) ? entry.Name["package/".Length..] : "";
            if (!required.Remove(name) || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
                throw new InvalidDataException("Unexpected entry in the pinned run-script-os archive.");
            var destination = Path.Combine(package, "installer", "run-script-os", name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var output = File.Create(destination);
            await entry.DataStream.CopyToAsync(output);
        }
        if (required.Count != 0)
            throw new InvalidDataException("The pinned run-script-os archive is incomplete.");
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
        using var tar = new TarWriter(gzip, TarEntryFormat.Pax);
        foreach (var file in Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(package, file).Replace(Path.DirectorySeparatorChar, '/');
            var entry = new PaxTarEntry(TarEntryType.RegularFile, $"package/{relative}")
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
