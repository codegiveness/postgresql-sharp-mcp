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
    public static async Task RunAsync(string root, string artifacts, string? targetsFile)
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
        environment["npm_config_cache"] = Path.Combine(stage, "npm-cache");
        string npmHome = Path.Combine(stage, "npm");
        string toolHome = Path.Combine(stage, "tools");
        Directory.CreateDirectory(npmHome);
        await File.WriteAllTextAsync(Path.Combine(npmHome, "package.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["private"] = true,
            ["allowScripts"] = new Dictionary<string, bool> { ["file:" + tarball] = true }
        }));
        await VerifyMissingDotnetAsync(npm, node, tarball, stage, environment);
        // --ignore-scripts is intentionally absent: the maintained .NET postinstall selects the native apphost.
        await Processes.RunAsync(NpmInstall(npm, npmHome, tarball), environment, timeout: 120);
        await Processes.RunAsync(new(dotnet, "tool", "install", packageId, "--version", version,
            "--tool-path", toolHome, "--configfile", config, "--no-cache"), environment, timeout: 120);
        string native = Path.Combine(npmHome, "node_modules", npmName.Replace('/', Path.DirectorySeparatorChar), "bin", "postgresql-sharp-mcp.exe");
        string npmBin = Path.Combine(npmHome, "node_modules", ".bin", OperatingSystem.IsWindows() ? "postgresql-sharp-mcp.cmd" : "postgresql-sharp-mcp");
        Check.That(File.Exists(native) && File.Exists(npmBin), "npm postinstall did not create the native application and command bin.");
        if (!OperatingSystem.IsWindows())
        {
            FileSystemInfo? destination = File.ResolveLinkTarget(npmBin, returnFinalTarget: true);
            Check.That(destination is not null && Path.GetFullPath(destination.FullName) == Path.GetFullPath(native), "npm bin is not linked to the native apphost.");
            Check.That((File.GetUnixFileMode(native) & UnixFileMode.UserExecute) != 0, "npm native apphost is not executable.");
        }
        var commands = new Dictionary<string, Command>
        {
            ["npm"] = new(npmBin),
            ["NuGet"] = new(Path.Combine(toolHome, OperatingSystem.IsWindows() ? "postgresql-sharp-mcp.exe" : "postgresql-sharp-mcp"))
        };
        // Exercise the actual apphost too, rather than accepting only cmd-shim success on Windows.
        await Processes.RunAsync(new(native, "--help"), environment);
        await using var fixture = targetsFile is null ? new PostgresFixture() : null;
        string? fixtureSeed = null;
        if (fixture is not null)
        {
            await fixture.StartAsync();
            fixtureSeed = Path.Combine(stage, "disposable-seed.json");
            await File.WriteAllTextAsync(fixtureSeed, JsonSerializer.Serialize(new
            {
                package_seed = $"Host=127.0.0.1;Port={fixture.Port};Database=tenant_a;Username=mcp_writer;Password=writer-disposable"
            }));
        }
        await using (var unavailable = new UnavailableEndpoint())
        {
            var configured = new Dictionary<string, string>(environment)
            {
                ["POSTGRES_TARGETS"] = JsonSerializer.Serialize(new { package_smoke = $"Host=127.0.0.1;Port={unavailable.Port};Database=package_smoke;Username=package_smoke;Password=disposable-package-secret;Timeout=1" }),
                ["POSTGRES_MAX_RESULT_BYTES"] = "4096"
            };
            foreach (var (name, command) in commands)
            {
                await VerifyCliAsync(name, command, version, environment, configured);
                await VerifyMcpAsync(command, configured, ["package_smoke"]);
                if (!OperatingSystem.IsWindows()) await VerifyMcpAsync(command, configured, ["package_smoke"], terminate: true);
                if (targetsFile is not null)
                {
                    string[] aliases = await SafeFixtureAliasesAsync(targetsFile);
                    var fixtureEnvironment = new Dictionary<string, string>(environment)
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
                else
                {
                    var fixtureEnvironment = new Dictionary<string, string>(environment)
                    {
                        ["POSTGRES_TARGETS_FILE"] = fixtureSeed!, ["POSTGRES_MAX_RESULT_BYTES"] = "4096"
                    };
                    await VerifyMcpAsync(command, fixtureEnvironment, ["package_seed"], query: true, fixture: fixture);
                    Console.WriteLine($"{name}: disposable single-seed live discovery, physical selection and default unrestricted writes passed");
                }
                Console.WriteLine($"{name}: installed CLI, validation, MCP initialize/tools/list/list_databases and shutdown passed");
            }
            if (OperatingSystem.IsWindows()) await VerifyMcpAsync(new(native), configured, ["package_smoke"]);
        }
        Console.WriteLine("Package verification passed; offline local installs, real native commands, missing .NET prerequisite and shutdown verified.");
    }

    private static Command NpmInstall(string npm, string home, string tarball) => new(npm, "install", "--prefix", home,
        "--offline", "--no-audit", "--no-fund", "--package-lock=false", "--ignore-scripts=false", tarball);

    private static async Task VerifyMissingDotnetAsync(string npm, string node, string tarball, string stage, Dictionary<string, string> environment)
    {
        string path = Path.Combine(stage, "missing-dotnet-path");
        string home = Path.Combine(stage, "missing-dotnet-install");
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(home);
        await File.WriteAllTextAsync(Path.Combine(home, "package.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["private"] = true,
            ["allowScripts"] = new Dictionary<string, bool> { ["file:" + tarball] = true }
        }));
        string isolatedNode = Path.Combine(path, OperatingSystem.IsWindows() ? "node.exe" : "node");
        if (OperatingSystem.IsWindows()) File.Copy(node, isolatedNode);
        else File.CreateSymbolicLink(isolatedNode, node);
        // Invoke the npm-distributed CLI, not owned JS. npm's postinstall still runs with a PATH that has no dotnet.
        string realNpm = File.ResolveLinkTarget(npm, returnFinalTarget: true)?.FullName ?? npm;
        string npmCli = realNpm.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? realNpm
            : Path.Combine(Path.GetDirectoryName(npm)!, "node_modules", "npm", "bin", "npm-cli.js");
        Check.That(File.Exists(npmCli), "Cannot locate npm's installed CLI for isolated prerequisite verification.");
        var missing = new Dictionary<string, string>(environment)
        {
            ["PATH"] = path,
            ["DOTNET_ROOT"] = Path.Combine(stage, "missing-dotnet-root"),
            ["DOTNET_ROOT_X64"] = Path.Combine(stage, "missing-dotnet-root"),
            ["DOTNET_ROOT_ARM64"] = Path.Combine(stage, "missing-dotnet-root"),
            ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
            ["npm_config_cache"] = Path.Combine(stage, "missing-dotnet-cache"),
            ["npm_config_script_shell"] = OperatingSystem.IsWindows()
                ? Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
                : "/bin/sh"
        };
        Command install = NpmInstall(npm, home, tarball);
        ProcessResult result = await Processes.RunAsync(new(isolatedNode, [npmCli, .. install.Arguments]), missing, expected: null, timeout: 120);
        Check.That(result.ExitCode != 0 && result.Error.Contains("dotnet", StringComparison.OrdinalIgnoreCase), "npm postinstall did not diagnose the unavailable .NET installation prerequisite.");
        Check.Confidential(result.Output + result.Error, "disposable-package-secret");
        Console.WriteLine("PASS npm postinstall rejects missing dotnet in an isolated offline install");
    }

    private static async Task VerifyCliAsync(string name, Command command, string version, Dictionary<string, string> environment, Dictionary<string, string> configured)
    {
        await Processes.RunAsync(command.With("--help"), environment);
        ProcessResult actualVersion = await Processes.RunAsync(command.With("--version"), environment);
        string reported = actualVersion.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Last();
        Check.That((reported == version || reported == version + ".0") && actualVersion.Error.Length == 0, $"{name} package version mismatch.");
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

    private static JsonNode[] Reports(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();

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
                string seed = await File.ReadAllTextAsync(environment["POSTGRES_TARGETS_FILE"]);
                Check.Equal((await client.OkAsync("execute_sql", new { database = "tenant_b", sql = "SELECT current_database(),value FROM marker" }))["rows"],
                    new[] { new[] { "tenant_b", "B_ONLY" } }, "Installed single-seed package did not select a discovered physical database.");
                string database = "package_after_start_" + Guid.NewGuid().ToString("N");
                await fixture.CreateDatabaseAsync(database);
                Check.That(Check.Rows((await client.OkAsync("list_databases"))["databases"]!).Any(row => row["name"].Text() == database),
                    "Installed package cached database discovery across catalog changes.");
                Check.Equal((await client.OkAsync("execute_sql", new { database, sql = "SELECT current_database(),value FROM marker" }))["rows"],
                    new[] { new[] { database, "DYNAMIC_ONLY" } }, "Installed package could not select a database created after startup.");
                await client.OkAsync("execute_sql", new { database = "tenant_b", sql = "INSERT INTO marker VALUES ('PACKAGE_WRITE')", read_only = false });
                Check.Equal((await client.OkAsync("execute_sql", new { database = "tenant_b", sql = "SELECT value FROM marker ORDER BY value" }))["rows"],
                    new[] { new[] { "B_ONLY" }, new[] { "PACKAGE_WRITE" } }, "Installed package omitted-mode write did not commit.");
                await client.OkAsync("execute_sql", new { database = "tenant_b", sql = "DELETE FROM marker WHERE value='PACKAGE_WRITE'", read_only = false });
                Check.That(await File.ReadAllTextAsync(environment["POSTGRES_TARGETS_FILE"]) == seed, "Installed discovery rewrote its seed file.");
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
        public int Port { get; }
        public UnavailableEndpoint()
        {
            try
            {
                // Bound, deliberately non-listening loopback socket prevents a race with another service.
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        public ValueTask DisposeAsync() { socket.Dispose(); return ValueTask.CompletedTask; }
    }
}
