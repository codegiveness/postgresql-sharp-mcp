using System.Data.Common;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace PostgreSqlMcp.Verify;

internal static class Packages
{
    public static async Task RunAsync(string root, string artifacts, string? targetsFile, bool installationOnly)
    {
        JsonNode metadata = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "npm", "package.json")))!;
        XDocument project = XDocument.Load(Path.Combine(root, "src", "PostgreSqlMcp", "PostgreSqlMcp.csproj"));
        string version = project.Descendants("Version").Single().Value;
        string packageId = project.Descendants("PackageId").Single().Value;
        string npmName = metadata["name"].Text();
        Check.That(metadata["version"].Text() == version, "npm and NuGet package versions disagree.");
        string tarball = Path.Combine(artifacts, npmName.Replace("@", "", StringComparison.Ordinal).Replace('/', '-') + "-" + version + ".tgz");
        string nupkg = Path.Combine(artifacts, packageId + "." + version + ".nupkg");
        string config = Path.Combine(artifacts, "NuGet.Config");
        Check.That(File.Exists(tarball) && File.Exists(nupkg) && File.Exists(config), "Generate both packages and local-only NuGet.Config before package verification.");
        await VerifyArchivesAsync(root, tarball, nupkg);
        VerifyLocalSource(config, artifacts);
        string npm = Processes.FindExecutable("npm");
        string npx = Processes.FindExecutable("npx");
        string node = Processes.FindExecutable("node");
        string dotnet = Processes.FindExecutable("dotnet");
        ProcessResult nodeVersion = await Processes.RunAsync(new(node, "--version"));
        Check.That(int.Parse(nodeVersion.Output.Trim().TrimStart('v').Split('.')[0], System.Globalization.CultureInfo.InvariantCulture) >= 22, "Node >=22 is required for package verification.");
        using var temporary = new TemporaryDirectory();
        string stage = temporary.Path;
        var environment = Processes.CleanEnvironment();
        string dotnetHost = File.ResolveLinkTarget(dotnet, returnFinalTarget: true)?.FullName ?? dotnet;
        environment["DOTNET_ROOT"] = environment.GetValueOrDefault("DOTNET_ROOT") ?? Path.GetDirectoryName(dotnetHost)!;
        environment["DOTNET_NOLOGO"] = "1";
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        environment["NUGET_PACKAGES"] = Path.Combine(stage, "nuget-cache");
        string npmHome = Path.Combine(stage, "npm");
        string toolHome = Path.Combine(stage, "tools");
        string globalHome = Path.Combine(stage, "npm-global");
        string npxHome = Path.Combine(stage, "npx");
        var (isolatedNpm, isolatedNpx, noDotnet) = CreateNpmEnvironment(npm, npx, node, stage, environment);
        Dictionary<string, string> localEnvironment = ConsumerEnvironment(noDotnet, stage, "local");
        Dictionary<string, string> globalEnvironment = ConsumerEnvironment(noDotnet, stage, "global");
        Dictionary<string, string> npxEnvironment = ConsumerEnvironment(noDotnet, stage, "npx");
        Directory.CreateDirectory(npmHome);
        Directory.CreateDirectory(globalHome);
        Directory.CreateDirectory(npxHome);
        await File.WriteAllTextAsync(Path.Combine(npmHome, "package.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["private"] = true,
            ["allowScripts"] = new Dictionary<string, bool> { ["file:" + tarball] = true }
        }));
        // Installation scripts must run: selecting the self-contained apphost is part of the consumer contract.
        await Processes.RunAsync(NpmInstall(isolatedNpm, npmHome, tarball), localEnvironment, timeout: 600);
        await Processes.RunAsync(isolatedNpm.With("install", "--global", "--prefix", globalHome,
            "--allow-scripts=file:" + tarball, "--offline", "--no-audit", "--no-fund", "--ignore-scripts=false", tarball),
            globalEnvironment, timeout: 600, directory: globalHome);
        await Processes.RunAsync(new(dotnet, "tool", "install", packageId, "--version", version,
            "--tool-path", toolHome, "--configfile", config, "--no-cache"), environment, timeout: 120);
        string native = InstalledNpmCommand(npmHome, npmName, global: false, out string npmBin);
        string globalNative = InstalledNpmCommand(globalHome, npmName, global: true, out string globalBin);
        Command npxCommand = isolatedNpx.With("-y", "--allow-scripts=file:" + tarball, "--offline",
            "--package=" + tarball, "--", "postgresql-sharp-mcp")
            // npm re-reads a local tarball even with a warm cache; allow archive resolution before initialization.
            with { WorkingDirectory = npxHome, InitializationTimeoutSeconds = 60 };
        var commands = new (string Name, Command Command, Dictionary<string, string> Environment, string? Native)[]
        {
            ("npm local (no .NET)", new(npmBin) { WorkingDirectory = npmHome }, localEnvironment, native),
            ("npm global (no .NET)", new(globalBin) { WorkingDirectory = globalHome }, globalEnvironment, globalNative),
            ("npx offline tarball (no .NET)", npxCommand, npxEnvironment, null),
            ("NuGet", new(Path.Combine(toolHome, OperatingSystem.IsWindows() ? "postgresql-sharp-mcp.exe" : "postgresql-sharp-mcp")), environment, null)
        };
        // Exercise actual apphosts too, rather than accepting only cmd-shim success on Windows.
        await Processes.RunAsync(new(native, "--help"), localEnvironment);
        await Processes.RunAsync(new(globalNative, "--help"), globalEnvironment);
        await using var fixture = targetsFile is null && !installationOnly ? new PostgresFixture() : null;
        if (fixture is not null)
            await fixture.StartAsync();
        await using (var unavailable = new UnavailableEndpoint())
        {
            foreach (var (name, command, commandEnvironment, nativeCommand) in commands)
            {
                var configured = new Dictionary<string, string>(commandEnvironment)
                {
                    ["POSTGRES_CONNECTION_STRING"] = $"Host=127.0.0.1;Port={unavailable.Port};Database=package_smoke;Username=package_smoke;Password=disposable-package-secret;Timeout=1",
                    ["POSTGRES_MAX_RESULT_BYTES"] = "4096"
                };
                Console.WriteLine($"{name}: starting CLI verification");
                await VerifyCliAsync(name, command, version, commandEnvironment, configured);
                string? installedNative = nativeCommand;
                if (command == npxCommand)
                {
                    string packagePath = Path.Combine("node_modules", npmName.Replace('/', Path.DirectorySeparatorChar), "bin", "postgresql-sharp-mcp.exe");
                    string npxInstall = Directory.EnumerateDirectories(Path.Combine(npxEnvironment["npm_config_cache"], "_npx"))
                        .Single(directory => File.Exists(Path.Combine(directory, packagePath)));
                    installedNative = InstalledNpmCommand(npxInstall, npmName, global: false, out _);
                }
                Console.WriteLine($"{name}: starting MCP verification");
                await VerifyMcpAsync(command, configured, ["primary"]);
                // SIGTERM the installed server, not npm's wrapper, which does not forward a root-only signal.
                // The real npx command above must still shut down cleanly on stdin EOF.
                if (!OperatingSystem.IsWindows())
                    await VerifyMcpAsync(installedNative is null ? command : new(installedNative), configured, ["primary"], terminate: true);
                if (targetsFile is not null)
                {
                    string[] aliases = await SafeFixtureAliasesAsync(targetsFile);
                    var fixtureEnvironment = new Dictionary<string, string>(commandEnvironment)
                    {
                        ["POSTGRES_TARGETS_FILE"] = targetsFile,
                        ["POSTGRES_MAX_RESULT_BYTES"] = "4096"
                    };
                    ProcessResult validated = await Processes.RunAsync(command.With("--validate"), fixtureEnvironment);
                    Check.That(validated.Output.Length == 0, "Successful package preflight contaminated stdout.");
                    JsonNode[] reports = Reports(validated.Error);
                    Check.That(reports.Length == aliases.Length && reports.All(report => report["error"] is null)
                        && reports.Select(report => report["database"].Text()).ToHashSet().SetEquals(aliases), "Installed package did not validate every fixture target.");
                    await VerifyMcpAsync(command, fixtureEnvironment, aliases, query: true);
                    Console.WriteLine($"{name}: all explicit loopback fixture targets validated and SELECT current_database() passed");
                }
                else if (fixture is not null)
                {
                    var fixtureEnvironment = new Dictionary<string, string>(commandEnvironment)
                    {
                        ["POSTGRES_CONNECTION_STRING"] = $"Host=127.0.0.1;Port={fixture.Port};Database=tenant_a;Username=mcp_writer;Password=writer-disposable",
                        ["POSTGRES_MAX_RESULT_BYTES"] = "4096"
                    };
                    await VerifyMcpAsync(command, fixtureEnvironment, ["primary"], query: true, fixture: fixture);
                    Console.WriteLine($"{name}: environment-only primary bootstrap, live discovery, physical selection, read-only default, write opt-in and cleanup passed");
                }
                Console.WriteLine($"{name}: installed CLI, validation, MCP initialize/tools/list/list_databases and shutdown passed");
                if (OperatingSystem.IsWindows() && installedNative is not null)
                    await VerifyMcpAsync(new(installedNative), configured, ["primary"]);
            }
        }
        Console.WriteLine("Package verification passed; offline local and global npm installs, fresh-cache npx tarball CLI/MCP without .NET, NuGet, native commands and shutdown verified.");
        if (installationOnly)
            Console.WriteLine("Installation-only mode: live database discovery/query/write scenarios were not run; use default package verification for that coverage.");
    }

    private static Command NpmInstall(Command npm, string home, string tarball) => npm.With("install", "--prefix", home,
        "--offline", "--no-audit", "--no-fund", "--package-lock=false", "--ignore-scripts=false", tarball);

    private static (Command Npm, Command Npx, Dictionary<string, string> Environment) CreateNpmEnvironment(
        string npm, string npx, string node, string stage, Dictionary<string, string> environment)
    {
        string path = Path.Combine(stage, "no-dotnet-path");
        Directory.CreateDirectory(path);
        string isolatedNode = Path.Combine(path, OperatingSystem.IsWindows() ? "node.exe" : "node");
        Command isolatedNpm;
        Command isolatedNpx;
        if (OperatingSystem.IsWindows())
        {
            File.Copy(node, isolatedNode);
            string distribution = Path.Combine(Path.GetDirectoryName(npm)!, "node_modules", "npm");
            Check.That(File.Exists(Path.Combine(distribution, "bin", "npm-cli.js"))
                && File.Exists(Path.Combine(distribution, "bin", "npx-cli.js")), "Cannot locate npm's installed distribution for isolated consumer verification.");
            string isolatedDistribution = Path.Combine(path, "node_modules", "npm");
            foreach (string directory in Directory.EnumerateDirectories(distribution, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(isolatedDistribution, Path.GetRelativePath(distribution, directory)));
            Directory.CreateDirectory(isolatedDistribution);
            foreach (string file in Directory.EnumerateFiles(distribution, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(isolatedDistribution, Path.GetRelativePath(distribution, file)));
            // Preserve upstream wrappers: the bundled OS runner launches npm again from PATH.
            File.Copy(Path.Combine(distribution, "bin", "npm.cmd"), Path.Combine(path, "npm.cmd"));
            File.Copy(Path.Combine(distribution, "bin", "npx.cmd"), Path.Combine(path, "npx.cmd"));
            isolatedNpm = new(isolatedNode, Path.Combine(isolatedDistribution, "bin", "npm-cli.js"));
            isolatedNpx = new(isolatedNode, Path.Combine(isolatedDistribution, "bin", "npx-cli.js"));
        }
        else
        {
            File.CreateSymbolicLink(isolatedNode, node);
            string isolatedNpmPath = Path.Combine(path, "npm");
            string isolatedNpxPath = Path.Combine(path, "npx");
            File.CreateSymbolicLink(isolatedNpmPath, npm);
            File.CreateSymbolicLink(isolatedNpxPath, npx);
            File.CreateSymbolicLink(Path.Combine(path, "uname"), Processes.FindExecutable("uname"));
            File.CreateSymbolicLink(Path.Combine(path, "sh"), "/bin/sh");
            isolatedNpm = new(isolatedNpmPath);
            isolatedNpx = new(isolatedNpxPath);
        }
        var missing = new Dictionary<string, string>(environment)
        {
            ["PATH"] = path,
            ["DOTNET_ROOT"] = Path.Combine(stage, "missing-dotnet-root"),
            ["DOTNET_ROOT_X64"] = Path.Combine(stage, "missing-dotnet-root"),
            ["DOTNET_ROOT_ARM64"] = Path.Combine(stage, "missing-dotnet-root"),
            ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
            ["npm_config_script_shell"] = OperatingSystem.IsWindows()
                ? Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
                : "/bin/sh"
        };
        return (isolatedNpm, isolatedNpx, missing);
    }

    private static Dictionary<string, string> ConsumerEnvironment(Dictionary<string, string> environment, string stage, string consumer) =>
        new(environment)
        {
            ["npm_config_cache"] = Path.Combine(stage, consumer + "-cache"),
            ["npm_config_prefix"] = Path.Combine(stage, consumer + "-prefix"),
            // Test the server's diagnostics, not npm's notice-level echo of synthetic CLI arguments.
            // Warnings and errors remain visible; the server's stderr is unaffected.
            ["npm_config_loglevel"] = "warn"
        };

    private static string InstalledNpmCommand(string home, string npmName, bool global, out string bin)
    {
        string modules = Path.Combine(home, global && !OperatingSystem.IsWindows() ? "lib" : "", "node_modules");
        string native = Path.Combine(modules, npmName.Replace('/', Path.DirectorySeparatorChar), "bin", "postgresql-sharp-mcp.exe");
        string binHome = global ? (OperatingSystem.IsWindows() ? home : Path.Combine(home, "bin")) : Path.Combine(modules, ".bin");
        bin = Path.Combine(binHome, OperatingSystem.IsWindows() ? "postgresql-sharp-mcp.cmd" : "postgresql-sharp-mcp");
        Check.That(File.Exists(native) && File.Exists(bin), "npm postinstall did not create the native application and command bin.");
        if (!OperatingSystem.IsWindows())
        {
            FileSystemInfo? destination = File.ResolveLinkTarget(bin, returnFinalTarget: true);
            Check.That(destination is not null && Path.GetFullPath(destination.FullName) == Path.GetFullPath(native), "npm bin is not linked to the native apphost.");
            Check.That((File.GetUnixFileMode(native) & UnixFileMode.UserExecute) != 0, "npm native apphost is not executable.");
        }
        return native;
    }

    private static async Task VerifyCliAsync(string name, Command command, string version, Dictionary<string, string> environment, Dictionary<string, string> configured)
    {
        ProcessResult actualVersion = await Processes.RunAsync(command.With("--version"), environment, timeout: 600);
        string reported = actualVersion.Output.Trim();
        Check.That(reported == "postgresql-sharp-mcp " + version || reported == "postgresql-sharp-mcp " + version + ".0",
            $"{name} package version mismatch or contaminated version output.");
        await Processes.RunAsync(command.With("--help"), environment, timeout: 600);
        ProcessResult invalid = await Processes.RunAsync(command, environment, expected: 1);
        Check.That(invalid.Output.Length == 0 && invalid.Error.Length > 0, $"{name} invalid configuration did not fail on stderr only.");
        ProcessResult unsafeCli = await Processes.RunAsync(command.With("--sensitive-package-cli-marker"), configured, expected: 1);
        Check.That(unsafeCli.Output.Length == 0, "Packaged CLI error contaminated stdout.");
        Check.Confidential(unsafeCli.Error, "sensitive-package-cli-marker", "disposable-package-secret");
        ProcessResult validation = await Processes.RunAsync(command.With("--validate"), configured, expected: 1);
        Check.That(validation.Output.Length == 0, $"{name} unavailable endpoint validation contaminated stdout.");
        JsonNode[] reports = Reports(validation.Error);
        Check.That(reports.Any(report => report["error"]?["code"]?.Text() is "connection_error" or "timeout"), $"{name} did not diagnose the unavailable endpoint.");
        Check.Confidential(validation.Error, "disposable-package-secret");
    }

    // npm may also emit its own notices on stderr; structured server reports remain JSON lines.
    private static JsonNode[] Reports(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Where(line => line.StartsWith('{')).Select(line => JsonNode.Parse(line)!).ToArray();

    private static async Task VerifyMcpAsync(Command command, Dictionary<string, string> environment, string[] aliases, bool terminate = false, bool query = false, PostgresFixture? fixture = null)
    {
        await using var client = await McpClient.StartAsync(command, environment);
        JsonArray tools = (await client.RequestAsync("tools/list", new { }))["result"]!["tools"].Array();
        Check.That(tools.Any(tool => tool!["name"].Text() == "list_databases"), "Installed package lost list_databases tool.");
        if (!query)
            await client.FailsAsync("list_databases", new { }, "connection_error");
        else
        {
            foreach (string alias in aliases)
            {
                JsonNode selected = await client.OkAsync("execute_sql", new { database = alias, sql = "SELECT current_database() AS database, 42 AS answer", limit = 1 });
                string physical = selected["rows"]![0]![0].Text();
                Check.That(selected["rows"]![0]![1].Int() == 42, "Installed package query returned incorrect values.");
                var discovered = new List<string>();
                int offset = 0;
                bool currentFound = false;
                while (true)
                {
                    JsonNode page = (await client.OkAsync("list_databases", new { target = alias, limit = 2, offset }))["databases"]!;
                    foreach (var row in Check.Rows(page))
                    {
                        discovered.Add(row["name"].Text());
                        if (row["is_current"].Flag())
                        {
                            Check.That(row["name"].Text() == physical && !currentFound, "Installed discovery reported an incorrect current database.");
                            currentFound = true;
                        }
                    }
                    if (page["next_offset"] is null) break;
                    int next = page["next_offset"].Int();
                    Check.That(next > offset, "Installed package catalog pagination did not advance.");
                    offset = next;
                }
                Check.That(currentFound && discovered.SequenceEqual(discovered.Distinct().Order(StringComparer.Ordinal)),
                    "Installed package catalog omitted the bootstrap database or repeated/reordered rows.");
            }
            if (fixture is not null)
            {
                Check.Equal((await client.OkAsync("execute_sql", new { database = "tenant_b", sql = "SELECT current_database(),value FROM marker" }))["rows"],
                    new[] { new[] { "tenant_b", "B_ONLY" } }, "Installed single-seed package did not select a discovered physical database.");
                string database = "package_after_start_" + Guid.NewGuid().ToString("N");
                await fixture.CreateDatabaseAsync(database);
                Check.That(Check.Rows((await client.OkAsync("list_databases"))["databases"]!).Any(row => row["name"].Text() == database),
                    "Installed package cached database discovery across catalog changes.");
                Check.Equal((await client.OkAsync("execute_sql", new { database, sql = "SELECT current_database(),value FROM marker" }))["rows"],
                    new[] { new[] { database, "DYNAMIC_ONLY" } }, "Installed package could not select a database created after startup.");
                await client.FailsAsync("execute_sql", new { database = "tenant_b", sql = "INSERT INTO marker VALUES ('PACKAGE_WRITE')" }, "postgresql_error", "25006");
                Check.Equal((await client.OkAsync("execute_sql", new { database = "tenant_b", sql = "SELECT value FROM marker" }))["rows"],
                    new[] { new[] { "B_ONLY" } }, "Installed environment-only package committed a default read-only mutation.");
                await client.OkAsync("execute_sql", new { database = "tenant_b", sql = "INSERT INTO marker VALUES ('PACKAGE_WRITE')", read_only = false });
                Check.Equal((await client.OkAsync("execute_sql", new { database = "tenant_b", sql = "SELECT value FROM marker ORDER BY value" }))["rows"],
                    new[] { new[] { "B_ONLY" }, new[] { "PACKAGE_WRITE" } }, "Installed environment-only package write opt-in did not commit.");
                await client.OkAsync("execute_sql", new { database = "tenant_b", sql = "DELETE FROM marker WHERE value='PACKAGE_WRITE'", read_only = false });
                Check.Equal((await client.OkAsync("execute_sql", new { database = "tenant_b", sql = "SELECT value FROM marker" }))["rows"],
                    new[] { new[] { "B_ONLY" } }, "Installed environment-only package write cleanup did not commit.");
            }
        }
        await client.StopAsync(terminate);
        Check.Confidential(client.StandardError, "disposable-package-secret", "reader-disposable", "writer-disposable");
    }

    private static async Task<string[]> SafeFixtureAliasesAsync(string path)
    {
        JsonNode targets = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Check.That(targets is JsonObject, "Optional package fixture targets must be an alias-to-connection-string object.");
        foreach (var target in targets.AsObject())
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = target.Value.Text() };
            string? host = builder.TryGetValue("Host", out object? configuredHost) ? Convert.ToString(configuredHost, System.Globalization.CultureInfo.InvariantCulture) : null;
            Check.That(host is "127.0.0.1" or "localhost" or "::1", "Optional package database-query smoke is limited to explicit loopback disposable fixtures.");
        }
        Check.That(targets.AsObject().Count > 0, "Optional fixture targets cannot be empty.");
        return targets.AsObject().Select(target => target.Key).ToArray();
    }

    private static void VerifyLocalSource(string config, string artifacts)
    {
        XDocument document = XDocument.Load(config);
        XElement? sources = document.Root?.Element("packageSources");
        Check.That(sources is not null && sources.Elements("clear").Any(), "NuGet verification configuration must clear inherited package sources.");
        XElement[] additions = sources!.Elements("add").ToArray();
        Check.That(additions.Length == 1, "NuGet verification must use exactly one local artifact source.");
        string source = additions[0].Attribute("value")?.Value ?? "";
        Check.That(!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) || uri.IsFile, "NuGet verification source must not access a network feed.");
        string resolved = Path.GetFullPath(source, Path.GetDirectoryName(config)!);
        Check.That(string.Equals(resolved.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(artifacts).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "NuGet source does not match the requested artifact directory.");
    }

    private static async Task VerifyArchivesAsync(string root, string tarball, string nupkg)
    {
        HashSet<string> legal = ["LICENSE", "THIRD-PARTY-NOTICES.md", .. Directory.EnumerateFiles(Path.Combine(root, "LICENSES"), "*.txt").Select(file => "LICENSES/" + Path.GetFileName(file))];
        byte[][] workspacePaths = new[] { root, root.Replace('\\', '/'), root.Replace('/', '\\') }.Distinct().Select(Encoding.UTF8.GetBytes).ToArray();
        var tarNames = new HashSet<string>();
        using (FileStream file = File.OpenRead(tarball))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        using (var archive = new TarReader(gzip))
        {
            while (await archive.GetNextEntryAsync() is { } entry)
            {
                tarNames.Add(entry.Name);
                if (entry.DataStream is not null) await VerifyNoWorkspacePathAsync(entry.DataStream, workspacePaths);
            }
        }
        Check.That(legal.All(name => tarNames.Contains("package/" + name)), "npm artifact lost legal notices.");
        using (ZipArchive archive = ZipFile.OpenRead(nupkg))
        {
            Check.That(legal.IsSubsetOf(archive.Entries.Select(entry => entry.FullName)), "NuGet artifact lost legal notices.");
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                using Stream stream = entry.Open();
                await VerifyNoWorkspacePathAsync(stream, workspacePaths);
            }
        }
        Console.WriteLine("PASS npm/NuGet legal notices and build-machine path confidentiality");
    }

    private static async Task VerifyNoWorkspacePathAsync(Stream stream, byte[][] paths)
    {
        using var content = new MemoryStream();
        await stream.CopyToAsync(content);
        ReadOnlyMemory<byte> bytes = content.GetBuffer().AsMemory(0, checked((int)content.Length));
        Check.That(paths.All(path => bytes.Span.IndexOf(path) < 0), "Package artifact exposes the build-machine workspace path.");
    }

    private sealed class UnavailableEndpoint : IAsyncDisposable
    {
        private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private readonly CancellationTokenSource stopping = new();
        private readonly Task rejecting;
        public int Port { get; }
        public UnavailableEndpoint()
        {
            try
            {
                // Own the port and reject handshakes without OS-specific non-listening-socket behavior.
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                socket.Listen(8);
                Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
                rejecting = RejectAsync();
            }
            catch
            {
                socket.Dispose();
                stopping.Dispose();
                throw;
            }
        }
        private async Task RejectAsync()
        {
            try
            {
                while (true)
                {
                    using Socket connection = await socket.AcceptAsync(stopping.Token);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stopping.CancelAsync();
            try { await rejecting; }
            finally
            {
                socket.Dispose();
                stopping.Dispose();
            }
        }
    }
}
