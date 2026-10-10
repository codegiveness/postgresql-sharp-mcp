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
        var targetsMode = Processes.CleanEnvironment();
        targetsMode["POSTGRES_TARGETS"] = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            // Ordinal-first default profile bootstraps from a database outside the lock.
            ["admin"] = connection + "postgres", ["main"] = connection + "lock_a", ["protected"] = connection + "lock_protected"
        });
        targetsMode["POSTGRES_DATABASES"] = LockList;
        targetsMode["POSTGRES_REQUIRE_DATABASE_LOCK"] = "true";

        await VerifyRequireSwitchAsync(command, connectionMode, targetsMode);
        await VerifyConnectionModeAsync(command, connectionMode);
        await VerifyTargetsModeAsync(command, targetsMode);
        await VerifyValidateAsync(command, connectionMode, targetsMode);
        await VerifyRefusalsAsync(command, fixture, connectionMode, targetsMode);
        // pg_stat_database.sessions counts every session established, including ones already closed.
        Check.That(await ProtectedSessionsAsync(fixture) == protectedSessions, "The server opened a session on a database outside the lock.");
        Console.WriteLine("PASS database lock in connection-string and targets modes, strict switch, PostgreSQL grant verification and startup refusals");
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

    private static async Task VerifyConnectionModeAsync(Command command, Dictionary<string, string> environment)
    {
        await using var client = await McpClient.StartAsync(command, environment);
        Check.Equal((await client.OkAsync("execute_sql", new { database = "lock_b", sql = "SELECT current_database(),value FROM marker" }))["rows"],
            new[] { new[] { "lock_b", "LOCK_B" } }, "Connection-string lock did not select an allowed database.");
        Check.That(ListedNames(await client.OkAsync("list_databases")).SequenceEqual(Locked),
            "Connection-string list_databases exposed databases outside the lock.");
        foreach (string outside in new[] { "lock_protected", "postgres", "template1" })
        {
            await ExpectLockRejectionAsync(client, new { database = outside });
            await ExpectLockRejectionAsync(client, new { database = outside, target = "lock_a" });
        }
        await client.StopAsync();
        Check.That(client.StandardError.Contains("Database lock verified", StringComparison.Ordinal), "Startup did not report the PostgreSQL lock verification.");
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
