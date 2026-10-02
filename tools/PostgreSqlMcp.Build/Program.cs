using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

return await PackageBuilder.RunAsync(args);

internal static class PackageBuilder
{
    private static readonly string[] RuntimeIdentifiers = ["win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];
    private static readonly string[] Documents = ["README.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "SECURITY.md", "CONTRIBUTING.md"];
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
            var tarballName = $"{packageName.Replace("@", "", StringComparison.Ordinal).Replace('/', '-')}-{version}.tgz";
            var nugetName = $"{packageId}.{version}.nupkg";
            if (Path.GetFileName(tarballName) != tarballName || Path.GetFileName(nugetName) != nugetName)
                throw new InvalidDataException("Package names must not contain filesystem paths.");

            temporary = Directory.CreateTempSubdirectory("postgresql-mcp-package-").FullName;
            if (IsWithin(temporary, root))
                throw new InvalidOperationException("Temporary staging must be outside the source tree; configure TMPDIR/TEMP accordingly.");
            var stage = Path.Combine(temporary, "source");
            StageSources(root, stage);
            var server = Path.Combine(stage, "src", "PostgreSqlMcp", "PostgreSqlMcp.csproj");
            var installer = Path.Combine(stage, "tools", InstallerName, $"{InstallerName}.csproj");
            var package = Path.Combine(temporary, "npm");
            Directory.CreateDirectory(package);
            CopyFile(Path.Combine(root, "npm", "package.json"), Path.Combine(package, "package.json"));
            foreach (var document in Documents)
                CopyFile(Path.Combine(stage, document), Path.Combine(package, document));
            foreach (var notice in Directory.EnumerateFiles(Path.Combine(stage, "LICENSES"), "*.txt"))
                CopyFile(notice, Path.Combine(package, "LICENSES", Path.GetFileName(notice)));

            var published = Path.Combine(temporary, "portable");
            await DotnetAsync(stage, "publish", server, "-c", "Release", "--self-contained", "false", "-p:UseAppHost=false", "-o", published);
            CopyManagedPayload(published, Path.Combine(package, "payload"), "PostgreSqlMcp");
            var installed = Path.Combine(temporary, "installer");
            await DotnetAsync(stage, "publish", installer, "-c", "Release", "--self-contained", "false", "-p:UseAppHost=false", "-o", installed);
            CopyManagedPayload(installed, Path.Combine(package, "installer"), InstallerName);

            foreach (var rid in RuntimeIdentifiers)
            {
                var native = Path.Combine(temporary, "published", rid);
                await DotnetAsync(stage, "publish", server, "-c", "Release", "--self-contained", "false", "-r", rid,
                    "-p:UseAppHost=true", "-o", native);
                var name = rid.StartsWith("win-", StringComparison.Ordinal) ? "PostgreSqlMcp.exe" : "PostgreSqlMcp";
                var apphost = Path.Combine(native, name);
                CopyFile(apphost, Path.Combine(package, "apphosts", rid, name));
            }

            // npm creates bin links before postinstall. The seed is a real PE
            // apphost so Windows cmd-shim chooses native invocation (no shebang).
            CopyFile(Path.Combine(package, "apphosts", "win-x64", "PostgreSqlMcp.exe"),
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
        foreach (var relative in ServerProjects.Select(name => Path.Combine("src", name)).Append(Path.Combine("tools", InstallerName)))
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

    private static void CopyManagedPayload(string source, string destination, string assembly)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*.dll"))
            CopyFile(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var name in new[] { $"{assembly}.deps.json", $"{assembly}.runtimeconfig.json" })
            CopyFile(Path.Combine(source, name), Path.Combine(destination, name));
        if (!File.Exists(Path.Combine(destination, $"{assembly}.dll")))
            throw new InvalidDataException($"Published payload is missing {assembly}.dll.");
    }

    private static async Task DotnetAsync(string stage, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = stage, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (var property in new[]
        {
            "RestoreLockedMode=true", "EnableSourceControlManagerQueries=false", "EnableSourceLink=false", "EmbedUntrackedSources=false",
            "DebugType=None", "DebugSymbols=false", "IncludeSymbols=false", $"PathMap={stage}=/_/"
        })
            start.ArgumentList.Add($"-p:{property}");
        using var process = Process.Start(start) ?? throw new IOException("Unable to start dotnet.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"dotnet {arguments[0]} failed with exit code {process.ExitCode}.");
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
                Mode = relative.StartsWith("apphosts/", StringComparison.Ordinal) || relative == "bin/postgresql-sharp-mcp.exe"
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
