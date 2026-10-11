// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;

namespace PostgreSqlMcp.Verify;

/// <summary>
/// Database lock (POSTGRES_DATABASES) scenarios in both connection modes. Runs last: hardening revokes
/// PUBLIC CONNECT on every fixture database, as SECURITY.md instructs operators to do.
/// </summary>
internal static class DatabaseLockChecks
{
    private const string Password = "locked-disposable";
    private static readonly string[] Locked = ["lock_a", "lock_b"];
    private static readonly string LockList = JsonSerializer.Serialize(Locked);

    public static async Task RunAsync(Command command, PostgresFixture fixture)
    {
        await fixture.SqlAsync("postgres", $"""
            CREATE ROLE mcp_locked LOGIN PASSWORD '{Password}' NOSUPERUSER NOCREATEDB NOCREATEROLE;
            CREATE DATABASE lock_a;
            CREATE DATABASE lock_b;
            CREATE DATABASE lock_protected;
            DO $do$
            DECLARE name text;
            BEGIN
              FOR name IN SELECT datname FROM pg_catalog.pg_database WHERE datallowconn LOOP
                EXECUTE format('REVOKE CONNECT ON DATABASE %I FROM PUBLIC', name);
              END LOOP;
            END $do$;
            GRANT CONNECT ON DATABASE lock_a, lock_b TO mcp_locked;
            """);
        foreach (string database in new[] { "lock_a", "lock_b", "lock_protected" })
            await fixture.SqlAsync(database, $"CREATE TABLE marker(value text NOT NULL); INSERT INTO marker VALUES ('{database.ToUpperInvariant()}'); GRANT SELECT ON marker TO mcp_locked;");
        // PostgreSQL 13 has no per-database session counter, and its transaction counters also count autovacuum
        // workers, so stop autovacuum on this disposable fixture before taking the baseline.
        if (PostgresFixture.Major < 14)
            await fixture.SqlAsync("postgres", "ALTER SYSTEM SET autovacuum = off;\nSELECT pg_reload_conf();");
        // Let the seeding session's statistics flush before taking the baseline.
        long protectedSessions = await ProtectedSessionsAsync(fixture);
        for (long previous = -1; previous != protectedSessions; protectedSessions = await ProtectedSessionsAsync(fixture))
        {
            previous = protectedSessions;
            await Task.Delay(500);
        }

        string connection = $"Host=127.0.0.1;Port={fixture.Port};Username=mcp_locked;Password={Password};Database=";
        var connectionMode = Processes.CleanEnvironment();
        connectionMode["POSTGRES_CONNECTION_STRING"] = connection + "postgres";
        connectionMode["POSTGRES_DATABASES"] = LockList;
        connectionMode["POSTGRES_REQUIRE_DATABASE_LOCK"] = "true";
        // The existing refusal scenarios exercise strict mode; advisory (the default) is covered by removing this.
        connectionMode["POSTGRES_DATABASE_LOCK_MODE"] = "strict";
        var targetsMode = Processes.CleanEnvironment();
        targetsMode["POSTGRES_TARGETS"] = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            // Ordinal-first default profile bootstraps from a database outside the lock.
            ["admin"] = connection + "postgres", ["main"] = connection + "lock_a", ["protected"] = connection + "lock_protected"
        });
        targetsMode["POSTGRES_DATABASES"] = LockList;
        targetsMode["POSTGRES_REQUIRE_DATABASE_LOCK"] = "true";
        targetsMode["POSTGRES_DATABASE_LOCK_MODE"] = "strict";

        await VerifyRequireSwitchAsync(command, connectionMode, targetsMode);
        await VerifyLockModeSettingAsync(command, connectionMode);
        await VerifyConnectionModeAsync(command, connectionMode);
        await VerifyLargeLockAsync(command, connectionMode);
        await VerifyTargetsModeAsync(command, targetsMode);
        await VerifyValidateAsync(command, connectionMode, targetsMode);
        await VerifyRefusalsAsync(command, fixture, connectionMode, targetsMode);
        await VerifyAdvisorySessionAsync(command, fixture, connectionMode);
        // pg_stat_database.sessions counts every session established, including ones already closed.
        Check.That(await ProtectedSessionsAsync(fixture) == protectedSessions, "The server opened a session on a database outside the lock.");
        Console.WriteLine("PASS database lock in connection-string and targets modes, strict switch, advisory and strict lock modes, PostgreSQL grant verification and startup refusals");
    }

    private static async Task VerifyRequireSwitchAsync(Command command, Dictionary<string, string> connectionMode, Dictionary<string, string> targetsMode)
    {
        foreach (var mode in new[] { connectionMode, targetsMode })
        {
            var unlocked = new Dictionary<string, string>(mode);
            unlocked.Remove("POSTGRES_DATABASES");
            await ExpectRefusalAsync(command, unlocked, "POSTGRES_REQUIRE_DATABASE_LOCK");
            await ExpectRefusalAsync(command.With("--validate"), unlocked, "POSTGRES_REQUIRE_DATABASE_LOCK");
            unlocked.Remove("POSTGRES_REQUIRE_DATABASE_LOCK");
            await ExpectRefusalAsync(command.With("--require-database-lock"), unlocked, "--require-database-lock");
        }
        var invalid = new Dictionary<string, string>(connectionMode) { ["POSTGRES_REQUIRE_DATABASE_LOCK"] = "yes" };
        await ExpectRefusalAsync(command, invalid, "must be true or false");
    }

    private static async Task VerifyLockModeSettingAsync(Command command, Dictionary<string, string> connectionMode)
    {
        foreach (string invalid in new[] { "Strict", "off", "enforce" })
            await ExpectRefusalAsync(command, new Dictionary<string, string>(connectionMode) { ["POSTGRES_DATABASE_LOCK_MODE"] = invalid }, "must be advisory or strict");
        await ExpectRefusalAsync(command.With("--database-lock-mode", "none"), Advisory(connectionMode), "must be advisory or strict");
        // Strict has nothing to check without a lock: refused instead of silently meaning nothing.
        var unlocked = new Dictionary<string, string>(connectionMode);
        unlocked.Remove("POSTGRES_DATABASES");
        unlocked.Remove("POSTGRES_REQUIRE_DATABASE_LOCK");
        await ExpectRefusalAsync(command, unlocked, "is strict, but no POSTGRES_DATABASES");
        await ExpectRefusalAsync(command.With("--validate", "--database-lock-mode", "strict"), Advisory(unlocked), "is strict, but no POSTGRES_DATABASES");
    }

    private static async Task VerifyConnectionModeAsync(Command command, Dictionary<string, string> environment)
    {
        await using var client = await McpClient.StartAsync(command, environment);
        Check.Equal((await client.OkAsync("execute_sql", new { database = "lock_b", sql = "SELECT current_database(),value FROM marker" }))["rows"],
            new[] { new[] { "lock_b", "LOCK_B" } }, "Connection-string lock did not select an allowed database.");
        JsonNode listed = await client.OkAsync("list_databases");
        Check.That(ListedNames(listed).SequenceEqual(Locked) && listed["database_lock"].Flag() && listed["database_lock_mode"].Text() == "strict",
            "Connection-string list_databases exposed databases outside the lock or did not report the strict lock.");
        Check.That((await client.ToolAsync("list_databases"))["description"].Text().Contains("database lock", StringComparison.Ordinal)
            && (await client.ToolAsync("execute_sql"))["description"].Text().Contains("Database lock", StringComparison.Ordinal)
            && (await client.ToolAsync("execute_sql"))["description"].Text().Contains("Lock mode strict", StringComparison.Ordinal)
            && client.Initialization["instructions"].Text().Contains("Database lock:", StringComparison.Ordinal)
            && client.Initialization["instructions"].Text().Contains("Lock mode strict", StringComparison.Ordinal),
            "Tool descriptions or server instructions did not disclose the strict database lock.");
        foreach (string outside in new[] { "lock_protected", "postgres", "template1" })
        {
            await ExpectLockRejectionAsync(client, new { database = outside });
            await ExpectLockRejectionAsync(client, new { database = outside, target = "lock_a" });
        }
        await client.StopAsync();
        Check.That(client.StandardError.Contains("Database lock verified", StringComparison.Ordinal), "Startup did not report the PostgreSQL lock verification.");
        Check.Confidential(client.StandardError, Password);
    }

    // A lock is an allow-list, not a set of profiles: more than 32 names and a large pool must not hit profile limits.
    private static async Task VerifyLargeLockAsync(Command command, Dictionary<string, string> connectionMode)
    {
        string[] names = [.. Locked, .. Enumerable.Range(1, 40).Select(i => $"lock_absent_{i:D2}")];
        var environment = new Dictionary<string, string>(connectionMode)
        {
            ["POSTGRES_DATABASES"] = JsonSerializer.Serialize(names), ["POSTGRES_POOL_SIZE"] = "32"
        };
        await using var client = await McpClient.StartAsync(command, environment);
        Check.Equal((await client.OkAsync("execute_sql", new { database = "lock_b", sql = "SELECT current_database(),value FROM marker" }))["rows"],
            new[] { new[] { "lock_b", "LOCK_B" } }, "A 42-name lock did not select an allowed database.");
        Check.That(ListedNames(await client.OkAsync("list_databases")).SequenceEqual(Locked),
            "A 42-name lock listed databases that do not exist or are outside the lock.");
        await ExpectLockRejectionAsync(client, new { database = "lock_protected" });
        await client.StopAsync();
        Check.Confidential(client.StandardError, Password);
    }

    private static async Task VerifyTargetsModeAsync(Command command, Dictionary<string, string> environment)
    {
        await using var client = await McpClient.StartAsync(command, environment);
        Check.Equal((await client.OkAsync("execute_sql", new { database = "main", sql = "SELECT current_database(),value FROM marker" }))["rows"],
            new[] { new[] { "lock_a", "LOCK_A" } }, "Alias inside the lock did not resolve to its bootstrap database.");
        Check.Equal((await client.OkAsync("execute_sql", new { database = "lock_b", target = "protected", sql = "SELECT current_database(),value FROM marker" }))["rows"],
            new[] { new[] { "lock_b", "LOCK_B" } }, "A profile with an out-of-lock bootstrap database could not select an allowed database.");
        // Alias, physical name and explicit target routes to databases outside the lock.
        await ExpectLockRejectionAsync(client, new { database = "protected" });
        await ExpectLockRejectionAsync(client, new { database = "admin" });
        await ExpectLockRejectionAsync(client, new { database = "lock_protected" });
        await ExpectLockRejectionAsync(client, new { database = "lock_protected", target = "main" });
        await ExpectLockRejectionAsync(client, new { database = "postgres", target = "protected" });
        foreach (string target in new[] { "admin", "main", "protected" })
        {
            JsonNode listed = await client.OkAsync("list_databases", new { target });
            Check.That(ListedNames(listed).SequenceEqual(Locked), "Targets-mode list_databases exposed databases outside the lock.");
            string current = Check.Rows(listed["databases"]!).Single(row => row["is_current"].Flag())["name"].Text();
            Check.That(current == "lock_a", "list_databases bootstrapped through a database outside the lock.");
        }
        Check.That(ListedNames(await client.OkAsync("list_databases")).SequenceEqual(Locked), "Default-profile discovery exposed databases outside the lock.");
        await client.StopAsync();
        Check.Confidential(client.StandardError, Password);
    }

    private static async Task VerifyValidateAsync(Command command, Dictionary<string, string> connectionMode, Dictionary<string, string> targetsMode)
    {
        foreach (var mode in new[] { connectionMode, targetsMode })
        {
            ProcessResult validation = await Processes.RunAsync(command.With("--validate"), mode);
            Check.That(validation.Output.Length == 0, "Locked validation contaminated stdout.");
            Check.That(validation.Error.Contains("Database lock verified", StringComparison.Ordinal), "Validation did not run the PostgreSQL lock check.");
            JsonNode[] reports = validation.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith('{')).Select(line => JsonNode.Parse(line)!).ToArray();
            Check.That(reports.Length > 0 && reports.All(report => report["rows"]![0]![0].Text() is "lock_a" or "lock_b"),
                "Validation connected through a database outside the lock.");
            Check.Confidential(validation.Error, Password);
        }
    }

    private static async Task VerifyRefusalsAsync(Command command, PostgresFixture fixture,
        Dictionary<string, string> connectionMode, Dictionary<string, string> targetsMode)
    {
        (string Harden, string Restore, string Expected)[] cases =
        [
            ("GRANT CONNECT ON DATABASE lock_protected TO mcp_locked;", "REVOKE CONNECT ON DATABASE lock_protected FROM mcp_locked;",
                "can connect to database lock_protected"),
            // The CHANGELOG breaking case: default PUBLIC CONNECT on postgres.
            ("GRANT CONNECT ON DATABASE postgres TO PUBLIC;", "REVOKE CONNECT ON DATABASE postgres FROM PUBLIC;", "can connect to database postgres"),
            ("ALTER ROLE mcp_locked SUPERUSER;", "ALTER ROLE mcp_locked NOSUPERUSER;", "is a superuser"),
            ("ALTER ROLE mcp_locked CREATEDB;", "ALTER ROLE mcp_locked NOCREATEDB;", "has CREATEDB"),
            ("ALTER ROLE mcp_locked CREATEROLE;", "ALTER ROLE mcp_locked NOCREATEROLE;", "has CREATEROLE"),
            ("CREATE ROLE lock_escalation SUPERUSER; GRANT lock_escalation TO mcp_locked;", "DROP ROLE lock_escalation;", "is a superuser"),
            ("GRANT pg_execute_server_program TO mcp_locked;", "REVOKE pg_execute_server_program FROM mcp_locked;", "pg_execute_server_program"),
            ("ALTER DATABASE lock_protected OWNER TO mcp_locked;", "ALTER DATABASE lock_protected OWNER TO postgres;", "owns database lock_protected")
        ];
        foreach (var (harden, restore, expected) in cases)
        {
            await fixture.SqlAsync("postgres", harden);
            try
            {
                foreach (var mode in new[] { connectionMode, targetsMode })
                {
                    await ExpectRefusalAsync(command, mode, expected);
                    await ExpectRefusalAsync(command.With("--validate"), mode, expected);
                    await ExpectAdvisoryAsync(command.With("--validate"), Advisory(mode), expected);
                }
            }
            finally { await fixture.SqlAsync("postgres", restore); }
        }
        await fixture.SqlAsync("lock_b", "CREATE EXTENSION dblink;");
        try
        {
            foreach (var mode in new[] { connectionMode, targetsMode })
                await ExpectRefusalAsync(command, mode, "database lock_b: extension dblink is installed");
        }
        finally { await fixture.SqlAsync("lock_b", "DROP EXTENSION dblink;"); }
        // An explicit EXECUTE grant (no predefined-role membership) reads other databases' relation files.
        await fixture.SqlAsync("lock_b", "GRANT EXECUTE ON FUNCTION pg_catalog.pg_read_binary_file(text) TO mcp_locked;");
        try
        {
            foreach (var mode in new[] { connectionMode, targetsMode })
            {
                await ExpectRefusalAsync(command, mode, "database lock_b: login can execute server-file function pg_read_binary_file(text)");
                await ExpectRefusalAsync(command.With("--validate"), mode, "login can execute server-file function pg_read_binary_file(text)");
            }
        }
        finally { await fixture.SqlAsync("lock_b", "REVOKE EXECUTE ON FUNCTION pg_catalog.pg_read_binary_file(text) FROM mcp_locked;"); }
        // The same grant held only by a role the NOINHERIT login can SET ROLE to is just as usable.
        await fixture.SqlAsync("postgres", "CREATE ROLE mcp_file_reader NOLOGIN; GRANT mcp_file_reader TO mcp_locked; ALTER ROLE mcp_locked NOINHERIT;");
        try
        {
            await fixture.SqlAsync("lock_b", "GRANT EXECUTE ON FUNCTION pg_catalog.pg_read_binary_file(text) TO mcp_file_reader;");
            foreach (var mode in new[] { connectionMode, targetsMode })
                await ExpectRefusalAsync(command.With("--validate"), mode, "database lock_b: login can execute server-file function pg_read_binary_file(text)");
        }
        finally
        {
            await fixture.SqlAsync("lock_b", "REVOKE EXECUTE ON FUNCTION pg_catalog.pg_read_binary_file(text) FROM mcp_file_reader;");
            await fixture.SqlAsync("postgres", "ALTER ROLE mcp_locked INHERIT; DROP ROLE mcp_file_reader;");
        }
        // Grants matching the lock again: startup succeeds after every refusal was restored.
        ProcessResult restored = await Processes.RunAsync(command.With("--validate"), targetsMode);
        Check.That(restored.Error.Contains("Database lock verified", StringComparison.Ordinal), "Startup did not recover after grants matched the lock.");
    }

    private static async Task ExpectRefusalAsync(Command command, Dictionary<string, string> environment, string expected)
    {
        ProcessResult refused = await Processes.RunAsync(command, environment, expected: 1);
        Check.That(refused.Output.Length == 0, "Startup refusal contaminated stdout.");
        Check.That(refused.Error.Contains(expected, StringComparison.Ordinal), $"Startup refusal did not explain: {expected}.");
        Check.Confidential(refused.Error, Password, "Host=", "Username=");
    }

    // Advisory mode: the same findings are warnings and the command succeeds.
    private static async Task ExpectAdvisoryAsync(Command command, Dictionary<string, string> environment, string expected)
    {
        ProcessResult result = await Processes.RunAsync(command, environment);
        Check.That(result.Output.Length == 0, "Advisory lock warnings contaminated stdout.");
        string[] lines = result.Error.Split('\n');
        Check.That(lines.Any(line => line.StartsWith("[warning] database lock (advisory): target ", StringComparison.Ordinal) && line.Contains(expected, StringComparison.Ordinal))
            && lines.Any(line => line.StartsWith("[warning] database lock (advisory): PostgreSQL does not confine", StringComparison.Ordinal))
            && !result.Error.Contains("Refusing to start", StringComparison.Ordinal),
            $"Advisory lock mode did not report as a warning: {expected}.");
        Check.Confidential(result.Error, Password, "Host=", "Username=");
    }

    // A superuser login fails the grant check: advisory still serves, enforces the lock in-process and reports the mode honestly.
    private static async Task VerifyAdvisorySessionAsync(Command command, PostgresFixture fixture, Dictionary<string, string> connectionMode)
    {
        await fixture.SqlAsync("postgres", "ALTER ROLE mcp_locked SUPERUSER;");
        try
        {
            // A CLI value overrides the strict environment setting.
            await ExpectAdvisoryAsync(command.With("--validate", "--database-lock-mode", "advisory"), connectionMode, "is a superuser");
            await using var client = await McpClient.StartAsync(command, Advisory(connectionMode));
            JsonNode listed = await client.OkAsync("list_databases");
            Check.That(ListedNames(listed).SequenceEqual(Locked) && listed["database_lock"].Flag() && listed["database_lock_mode"].Text() == "advisory",
                "Advisory list_databases exposed databases outside the lock or did not report the advisory mode.");
            string instructions = client.Initialization["instructions"].Text();
            string description = (await client.ToolAsync("execute_sql"))["description"].Text();
            Check.That(instructions.Contains("Lock mode advisory", StringComparison.Ordinal) && description.Contains("Lock mode advisory", StringComparison.Ordinal)
                && (await client.ToolAsync("list_databases"))["description"].Text().Contains("Lock mode advisory", StringComparison.Ordinal)
                && !(instructions + description).Contains("verified to confine", StringComparison.Ordinal),
                "Advisory mode was not disclosed, or was described as PostgreSQL confinement.");
            Check.Equal((await client.OkAsync("execute_sql", new { database = "lock_b", sql = "SELECT current_database(),value FROM marker" }))["rows"],
                new[] { new[] { "lock_b", "LOCK_B" } }, "Advisory lock did not select an allowed database.");
            foreach (string outside in new[] { "lock_protected", "postgres" })
                await ExpectLockRejectionAsync(client, new { database = outside });
            await client.StopAsync();
            Check.That(client.StandardError.Contains("[warning] database lock (advisory): target ", StringComparison.Ordinal),
                "Advisory startup did not print the grant findings as warnings.");
            Check.Confidential(client.StandardError, Password);
        }
        finally { await fixture.SqlAsync("postgres", "ALTER ROLE mcp_locked NOSUPERUSER;"); }
    }

    private static Dictionary<string, string> Advisory(Dictionary<string, string> mode)
    {
        var advisory = new Dictionary<string, string>(mode);
        advisory.Remove("POSTGRES_DATABASE_LOCK_MODE");
        return advisory;
    }

    private static async Task ExpectLockRejectionAsync(McpClient client, object selection)
    {
        JsonObject arguments = JsonSerializer.SerializeToNode(selection)!.AsObject();
        (string Tool, JsonObject Extra)[] tools =
        [
            ("execute_sql", new() { ["sql"] = "SELECT 1" }), ("explain_query", new() { ["sql"] = "SELECT 1" }),
            ("list_schemas", new()), ("list_objects", new()),
            ("get_object_details", new() { ["schema"] = "public", ["name"] = "marker" }),
            ("get_top_queries", new()), ("analyze_indexes", new()), ("analyze_db_health", new())
        ];
        foreach (var (tool, extra) in tools)
        {
            var call = (JsonObject)arguments.DeepClone();
            foreach (var (key, value) in extra) call[key] = value?.DeepClone();
            JsonNode error = await client.FailsAsync(tool, call, "invalid_target");
            Check.That(error["error"]!["message"].Text().Contains("database lock", StringComparison.Ordinal), $"{tool} was not rejected by the database lock.");
        }
    }

    private static string[] ListedNames(JsonNode listed) => Check.Rows(listed["databases"]!).Select(row => row["name"].Text()).ToArray();

    // pg_stat_database.sessions exists from PostgreSQL 14. PostgreSQL 13 has no session counter, so committed plus rolled-back
    // transactions stand in: any session the server used to check or query the database would have run at least one.
    private static async Task<long> ProtectedSessionsAsync(PostgresFixture fixture)
    {
        string counter = PostgresFixture.Major >= 14 ? "sessions" : "xact_commit + xact_rollback";
        ProcessResult result = await fixture.SqlAsync("postgres",
            $"SELECT 'sessions=' || ({counter}) FROM pg_catalog.pg_stat_database WHERE datname = 'lock_protected';");
        return long.Parse(result.Output.Split("sessions=")[1].Split('\n')[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
