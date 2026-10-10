using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PostgreSqlMcp.Verify;

internal static class Integration
{
    public static async Task RunAsync(string root)
    {
        await Processes.RunAsync(new("dotnet", "build", Path.Combine(root, "postgresql-sharp-mcp.slnx"), "-c", "Release"), timeout: 180);
        await ResourceChecks.RunAsync();
        SarifRegression.Run();
        await using var fixture = new PostgresFixture();
        await fixture.StartAsync();
        var command = new Command("dotnet", Path.Combine(root, "src", "PostgreSqlMcp", "bin", "Release", "net10.0", "PostgreSqlMcp.dll"));
        string connection = $"Host=127.0.0.1;Port={fixture.Port};Username=mcp_reader;Password=reader-disposable;Database=";
        var targets = new Dictionary<string, string>
        {
            ["a"] = connection + "tenant_a", ["b"] = connection + "tenant_b",
            ["a_copy"] = $"Database=tenant_a;Password=reader-disposable;Username=mcp_reader;Port={fixture.Port};Host=127.0.0.1",
            ["denied"] = connection + "tenant_denied", ["missing"] = connection + "does_not_exist",
            ["bad_auth"] = connection.Replace("reader-disposable", "wrong", StringComparison.Ordinal) + "tenant_a",
            ["offline"] = "Host=127.0.0.1;Port=1;Username=mcp_reader;Password=reader-disposable;Database=tenant_a;Timeout=1"
        };
        var environment = Processes.CleanEnvironment();
        environment["POSTGRES_TARGETS"] = JsonSerializer.Serialize(targets);
        environment["POSTGRES_ACCESS_MODE"] = "restricted";
        environment["POSTGRES_POOL_SIZE"] = "2";
        environment["POSTGRES_MAX_CONCURRENT_CALLS"] = "8";
        environment["POSTGRES_MAX_RESULT_BYTES"] = "4096";
        environment["POSTGRES_MAX_CELL_CHARS"] = "256";
        environment["POSTGRES_MAX_ROWS"] = "50";
        environment["POSTGRES_QUERY_TIMEOUT"] = "3";
        environment["POSTGRES_LOG_LEVEL"] = "trace";
        await using (var client = await McpClient.StartAsync(command, environment))
        {
            await VerifyToolsAsync(client);
            await VerifyPoolsAsync(client);
            await VerifyBoundariesAsync(client);
            await VerifyPaginationAsync(client);
            await VerifySqlAsync(client);
            await FuzzChecks.RunAsync(client);
            await VerifyCatalogAsync(client);
            await VerifyOperationsAsync(client, fixture);
            await VerifyPlansAsync(client);
            await client.StopAsync();
            Check.Confidential(client.StandardError, "reader-disposable", "sensitive-error-marker", "sensitive-hint-marker", "sensitive-result-marker");
        }
        Console.WriteLine("PASS PostgreSQL error and trace-level request/result confidentiality");
        await VerifyWritesAsync(command, environment, fixture.Port);
        await VerifyConfigurationAsync(command, environment, targets, connection);
        await VerifyDiscoveryAsync(command, environment, fixture, connection);
        await VerifyPoolCapacityAsync(command, environment, fixture, connection);
        await VerifyStatementsExtensionVersionsAsync(command, environment, fixture, connection);
        // Last: hardening revokes PUBLIC CONNECT on every fixture database.
        await DatabaseLockChecks.RunAsync(command, fixture);
        Console.WriteLine("ALL MCP INTEGRATION SCENARIOS PASSED");
    }

    private static async Task VerifyToolsAsync(McpClient client)
    {
        JsonArray tools = (await client.RequestAsync("tools/list", new { }))["result"]!["tools"].Array();
        string[] expected = ["list_databases", "list_schemas", "list_objects", "get_object_details", "execute_sql", "explain_query", "get_top_queries", "analyze_indexes", "analyze_db_health"];
        Check.That(tools.Select(tool => tool!["name"].Text()).ToHashSet().SetEquals(expected), "Incorrect shared nine-tool set.");
        JsonNode listed = await client.OkAsync("list_databases", new { target = "a", limit = 2 });
        Check.That(listed["access_mode"].Text() == "restricted", "Explicit restricted access was not retained.");
        Check.That(Check.Rows((await client.OkAsync("list_databases"))["databases"]!).Single(row => row["is_current"].Flag())["name"].Text() == "tenant_a",
            "Default discovery did not select the ordinal-first profile when primary was absent.");
        var discovered = new List<string>();
        int offset = 0;
        do
        {
            JsonNode page = (await client.OkAsync("list_databases", new { target = "a", limit = 2, offset }))["databases"]!;
            discovered.AddRange(Check.Rows(page).Select(row => row["name"].Text()));
            if (page["next_offset"] is null) break;
            int next = page["next_offset"].Int();
            Check.That(next > offset, "Catalog pagination did not advance.");
            offset = next;
        } while (true);
        Check.That(discovered.SequenceEqual(discovered.Distinct().Order(StringComparer.Ordinal))
            && discovered.Contains("tenant_a") && discovered.Contains("tenant_b")
            && !discovered.Contains("tenant_denied") && !discovered.Contains("template0") && !discovered.Contains("template1"),
            "Live catalog pagination omitted accessible databases, included templates/denied databases, or repeated rows.");
        JsonNode missing = await client.RequestAsync("tools/call", new { name = "execute_sql", arguments = new { sql = "SELECT 1" } });
        Check.That(missing["error"] is not null || missing["result"]?["isError"]?.Flag() == true, "Missing database argument was accepted.");
        Console.WriteLine("PASS nine-tool contract, live catalog pagination and explicit restricted access");
    }

    private static async Task VerifyPoolsAsync(McpClient client)
    {
        var requests = Enumerable.Range(0, 24).Select(index =>
        {
            string alias = index % 2 == 0 ? "a" : "b";
            return (Alias: alias, Task: client.OkAsync("execute_sql", new { database = alias,
                sql = "SELECT current_database() AS db, value, pg_backend_pid() AS pid FROM marker CROSS JOIN LATERAL (SELECT pg_sleep(0.03)) s" }));
        }).ToArray();
        JsonNode[] results = await Task.WhenAll(requests.Select(item => item.Task));
        var pids = new Dictionary<string, HashSet<int>> { ["a"] = [], ["b"] = [] };
        for (int index = 0; index < results.Length; index++)
        {
            string alias = requests[index].Alias;
            var row = Check.Rows(results[index]).Single();
            Check.That(results[index]["database"].Text() == alias && row["db"].Text() == (alias == "a" ? "tenant_a" : "tenant_b")
                && row["value"].Text() == (alias == "a" ? "A_ONLY" : "B_ONLY"), "Concurrent requests crossed database boundaries.");
            pids[alias].Add(row["pid"].Int());
        }
        Check.That(!pids["a"].Overlaps(pids["b"]) && pids.Values.All(set => set.Count <= 2), "Backend pools exceeded bounds or crossed targets.");
        JsonNode[] copies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.OkAsync("execute_sql", new
        {
            database = "a_copy", sql = "SELECT current_database(),value,pg_backend_pid() FROM marker"
        })));
        foreach (JsonNode result in copies)
        {
            JsonNode row = result["rows"]![0]!;
            Check.That(result["database"].Text() == "a_copy" && row[0].Text() == "tenant_a" && row[1].Text() == "A_ONLY" && pids["a"].Contains(row[2].Int()), "Equivalent aliases did not reuse the bounded pool.");
        }
        Console.WriteLine("PASS 24 concurrent alternating requests, per-target bounded pools and equivalent alias reuse");
    }

    private static async Task VerifyBoundariesAsync(McpClient client)
    {
        foreach (var (target, code, state) in new (string, string, string?)[]
        {
            ("unconfigured", "postgresql_error", "3D000"), ("denied", "postgresql_error", "42501"),
            ("missing", "postgresql_error", "3D000"), ("bad_auth", "postgresql_error", "28P01"),
            ("offline", "connection_error", null)
        })
            await client.FailsAsync("execute_sql", new { database = target, sql = "SELECT 1" }, code, state);
        await client.FailsAsync("execute_sql", new { database = "tenant_a", target = "unconfigured", sql = "SELECT 1" }, "invalid_target");
        await client.FailsAsync("list_databases", new { target = "unconfigured" }, "invalid_target");
        await client.FailsAsync("list_databases", new { target = "offline" }, "connection_error");
        await client.FailsAsync("execute_sql", new { database = "a", sql = "SELECT * FROM secret" }, "postgresql_error", "42501");
        Check.Equal((await client.OkAsync("execute_sql", new { database = "b", sql = "SELECT value FROM marker" }))["rows"], new[] { new[] { "B_ONLY" } }, "Denied targets fell back to another database.");
        Check.Equal((await client.OkAsync("execute_sql", new { database = "a", sql = "SELECT id FROM scoped_rows ORDER BY id" }))["rows"], new[] { new[] { 1 } }, "RLS was bypassed.");
        Console.WriteLine("PASS unknown, denied, missing, bad-auth, offline targets, permissions and RLS");
    }

    private static async Task VerifyPaginationAsync(McpClient client)
    {
        foreach (string alias in new[] { "a", "b" })
        {
            var seen = new List<int>();
            int offset = 0;
            while (true)
            {
                JsonNode page = await client.OkAsync("execute_sql", new { database = alias, sql = "SELECT g FROM generate_series(1,11) g ORDER BY g", limit = 3, offset });
                seen.AddRange(page["rows"].Array().Select(row => row![0].Int()));
                if (page["next_offset"] is null)
                {
                    Check.That(!page["truncated"].Flag(), "Final row page reported truncation.");
                    break;
                }
                Check.That(page["truncated"].Flag() && page["truncation_reason"].Text() == "row_limit", "Row limit did not report explicit truncation.");
                int next = page["next_offset"].Int();
                Check.That(next > offset, "Row pagination did not advance.");
                offset = next;
            }
            Check.That(seen.SequenceEqual(Enumerable.Range(1, 11)), "Pagination omitted or duplicated rows.");
        }
        const string byteSql = "SELECT g,g*2,g*3,g*4,g*5,g*6,g*7,g*8 FROM generate_series(1000000000::bigint,1000000500::bigint) g ORDER BY g";
        JsonNode bytes = await client.OkAsync("execute_sql", new { database = "a", sql = byteSql, limit = 50 });
        Check.That(bytes["truncated"].Flag() && bytes["truncation_reason"].Text() == "byte_limit", "Byte budget did not truncate explicitly.");
        JsonNode nextPage = await client.OkAsync("execute_sql", new { database = "a", sql = byteSql, limit = 50, offset = bytes["next_offset"].Int() });
        Check.That(nextPage["rows"]![0]![0].Long() == bytes["rows"].Array().Last()![0].Long() + 1, "Byte pagination omitted or repeated a row.");
        foreach (string expression in new[] { "repeat('x',100000)", "decode(repeat('61',100000),'hex')", "repeat('😀',1000)" })
        {
            JsonNode clipped = await client.OkAsync("execute_sql", new { database = "a", sql = "SELECT " + expression + " AS value" });
            Check.Equal(clipped["clipped_cells"], new[] { new { row = 0, column = 0 } }, "Cell clipping coordinates were incorrect.");
            Check.That(clipped["rows"]![0]![0].Text().EnumerateRunes().Count() <= 256, "Clipped cell exceeded Unicode character budget.");
        }
        foreach (var (sql, code) in new[] { ("SELECT ARRAY[1,2]", "unsupported_result_type"), ("SELECT interval '1 month'", "unsupported_result_value"), ("SELECT 1e100::numeric", "unsupported_result_value") })
            await client.FailsAsync("execute_sql", new { database = "a", sql }, code);
        Check.Equal((await client.OkAsync("execute_sql", new { database = "a", sql = "SELECT 1e100::numeric::text" }))["rows"], new[] { new[] { "1" + new string('0', 100) } }, "Explicit numeric text conversion changed the value.");
        Check.Equal((await client.OkAsync("execute_sql", new { database = "a", sql = "SELECT extract(month FROM interval '1 month')::integer" }))["rows"], new[] { new[] { 1 } }, "Explicit interval conversion changed the value.");
        await client.FailsAsync("execute_sql", new { database = "a", sql = "SELECT 1", limit = 51 }, "invalid_limit");
        await client.FailsAsync("execute_sql", new { database = "a", sql = "SELECT 1", offset = -1 }, "invalid_offset");
        Console.WriteLine("PASS row/byte pagination, text/binary/Unicode clipping, unsupported values and limits");
    }

    private static async Task VerifySqlAsync(McpClient client)
    {
        foreach (string sql in new[] { "SELECT 1; SELECT 2", "/*outer /*nested*/ */ COMMIT", "ROLLBACK", "SET transaction_read_only=off", "DO $$BEGIN END$$", "SELECT 'bad" })
            await client.FailsAsync("execute_sql", new { database = "a", sql }, "invalid_sql");
        await client.FailsAsync("execute_sql", new { database = "a", sql = "INSERT INTO marker VALUES ('BAD')" }, "postgresql_error", "25006");
        await client.FailsAsync("execute_sql", new { database = "a", sql = "WITH x AS (DELETE FROM marker RETURNING *) SELECT * FROM x" }, "postgresql_error");
        await client.FailsAsync("execute_sql", new { database = "a", sql = "SELECT set_config('transaction_read_only','off',true)" }, "postgresql_error");
        await client.FailsAsync("execute_sql", new { database = "a", sql = "INSERT INTO marker VALUES ('BAD')", read_only = false }, "read_only");
        await client.FailsAsync("execute_sql", new { database = "a", sql = "SELECT FROM WHERE" }, "postgresql_error", "42601");
        await client.FailsAsync("execute_sql", new { database = "a", sql = "SELECT pg_sleep(10)::text" }, "timeout");
        JsonNode quoted = await client.OkAsync("execute_sql", new { database = "a", sql = "SELECT $$a;b$$ AS d, E'escaped\\';still-string' AS e, 'ordinary;string' AS s; -- tail" });
        Check.Equal(quoted["rows"], new[] { new[] { "a;b", "escaped';still-string", "ordinary;string" } }, "Quoted SQL single-statement parsing changed values.");
        Check.Equal((await client.OkAsync("execute_sql", new { database = "a", sql = "SELECT value FROM marker" }))["rows"], new[] { new[] { "A_ONLY" } }, "Read-only protection changed fixture data.");
        foreach (var (sql, state) in new[] { ("SELECT CAST('sensitive-error-marker' AS integer)", "22P02"), ("SELECT app.fail_with_sensitive_diagnostic()", "P0001") })
        {
            JsonNode diagnostic = await client.FailsAsync("execute_sql", new { database = "a", sql }, "postgresql_error", state);
            Check.Confidential(diagnostic.ToJsonString(), "sensitive-");
            Check.That(diagnostic["error"]?["hint"] is null, "PostgreSQL hint leaked in error envelope.");
        }
        Check.Equal((await client.OkAsync("execute_sql", new { database = "a", sql = "SELECT 'sensitive-result-marker'" }))["rows"], new[] { new[] { "sensitive-result-marker" } }, "Result confidentiality probe did not return the marker.");
        Console.WriteLine("PASS SQL guards, read-only DML/CTE protection, quoted literals, syntax errors and timeout");
    }

    private static async Task VerifyCatalogAsync(McpClient client)
    {
        var schemas = Check.Rows(await client.OkAsync("list_schemas", new { database = "a", prefix = "app" })).Select(row => row["schema_name"].Text());
        Check.That(schemas.SequenceEqual(new[] { "app" }), "Schema prefix filter returned incorrect schemas.");
        var objects = Check.Rows(await client.OkAsync("list_objects", new { database = "a", schema = "public", type = "table", limit = 50 })).Select(row => row["name"].Text()).ToHashSet();
        Check.That(objects.Contains("orders") && !objects.Contains("secret"), "Catalog object privilege filtering failed.");
        Check.Equal((await client.OkAsync("list_objects", new { database = "a", schema = "public' OR 1=1 --" }))["rows"], System.Array.Empty<int[]>(), "Schema filter accepted SQL injection.");
        var allObjects = new List<Dictionary<string, JsonNode?>>();
        int offset = 0;
        while (true)
        {
            JsonNode page = await client.OkAsync("list_objects", new { database = "a", schema = "app", limit = 2, offset });
            allObjects.AddRange(Check.Rows(page));
            if (page["next_offset"] is null) break;
            int next = page["next_offset"].Int();
            Check.That(next > offset, "Object pagination did not advance.");
            offset = next;
        }
        Check.That(allObjects.Select(row => row["type"].Text()).ToHashSet().IsSupersetOf(new[] { "function", "procedure", "view", "sequence" }), "Paged object discovery lost an object type.");
        JsonNode columns = await client.OkAsync("get_object_details", new { database = "a", schema = "public", name = "orders", section = "columns", limit = 2 });
        Check.That(Check.Rows(columns["page"]!).Select(row => row["name"].Text()).SequenceEqual(new[] { "id", "customer" }), "First detail column page incorrect.");
        JsonNode more = await client.OkAsync("get_object_details", new { database = "a", schema = "public", name = "orders", section = "columns", limit = 2, offset = columns["page"]!["next_offset"].Int() });
        Check.That(Check.Rows(more["page"]!).Select(row => row["name"].Text()).SequenceEqual(new[] { "note" }), "Second detail column page incorrect.");
        foreach (string section in new[] { "constraints", "indexes", "triggers", "definition" })
        {
            JsonNode detail = await client.OkAsync("get_object_details", new { database = "a", schema = "public", name = "orders", section, limit = 2 });
            Check.That(detail["section"].Text() == section && detail["page"]!["rows"].Array().Count > 0, "Object detail section lost fixture evidence.");
        }
        await client.OkAsync("get_object_details", new { database = "a", schema = "app", name = "marker_view", section = "definition" });
        await client.OkAsync("get_object_details", new { database = "a", schema = "app", name = "counter", section = "definition" });
        await client.FailsAsync("get_object_details", new { database = "a", schema = "app", name = "lookup", section = "parameters" }, "ambiguous_object");
        string identity = allObjects.Single(row => row["name"].Text() == "lookup" && row["identity_arguments"].Text().Contains("integer", StringComparison.Ordinal))["identity_arguments"].Text();
        foreach (string section in new[] { "parameters", "definition" })
            await client.OkAsync("get_object_details", new { database = "a", schema = "app", name = "lookup", section, identity_arguments = identity });
        await client.OkAsync("get_object_details", new { database = "a", schema = "app", name = "noop", section = "parameters" });
        await client.FailsAsync("get_object_details", new { database = "a", schema = "public", name = "does_not_exist" }, "object_not_found");
        await client.FailsAsync("get_object_details", new { database = "a", schema = "app", name = "counter", section = "columns" }, "unsupported_section");
        Console.WriteLine("PASS focused schemas, paged objects/details, all sections, routine overloads and privilege filtering");
    }

    private static async Task VerifyOperationsAsync(McpClient client, PostgresFixture fixture)
    {
        foreach (string section in new[] { "summary", "vacuum", "index", "constraints", "sequences", "replication", "blocking" })
        {
            JsonNode result = await client.OkAsync("analyze_db_health", new { database = "a", section, limit = 2 });
            Check.That(result["database"].Text() == "a" && result["section"].Text() == section, "Health section returned incorrect identity.");
        }
        JsonNode indexes = await client.OkAsync("analyze_indexes", new { database = "a", schema = "public", table = "orders", limit = 3 });
        var duplicates = Check.Rows(indexes["result"]!).Where(row => row["index_name"].Text().StartsWith("orders_customer_duplicate", StringComparison.Ordinal)).ToArray();
        Check.That(duplicates.Length == 2 && duplicates.All(row => row["duplicate_count"].Int() == 1), "Structural duplicate index evidence incorrect.");
        // Reset fixture-owned statistics and create timed probes: setup call counts do not prove ranking.
        await fixture.SqlAsync("tenant_a", "SELECT extensions.pg_stat_statements_reset(); SELECT pg_sleep(0.15) AS a_timed_workload_marker;");
        await fixture.SqlAsync("tenant_b", "SELECT pg_sleep(0.35) AS b_timed_workload_marker;");
        JsonNode workload = await client.OkAsync("get_top_queries", new { database = "a", order_by = "total_time", limit = 2 });
        var workloadRows = Check.Rows(workload["result"]!).ToArray();
        Check.That(workloadRows.Any(row => (row["query_text"]?.Text() ?? "").Contains("a_timed_workload_marker", StringComparison.Ordinal)), "Timed local workload probe absent from ranking.");
        Check.That(workloadRows.All(row => !(row["query_text"]?.Text() ?? "").Contains("b_timed_workload_marker", StringComparison.Ordinal)), "Workload evidence crossed database boundaries.");
        // The fixture installs the server's default extension version: toplevel exists from PostgreSQL 14 (1.9) and is null before.
        Check.That(workloadRows.All(row => row.ContainsKey("toplevel") && (row["toplevel"] is null) == (PostgresFixture.Major < 14)),
            "toplevel availability did not follow the installed pg_stat_statements version.");
        foreach (string order in new[] { "mean_time", "calls", "rows", "reads" })
        {
            JsonNode ordered = await client.OkAsync("get_top_queries", new { database = "a", order_by = order, limit = 2 });
            Check.That(ordered["order_by"].Text() == order && Check.Rows(ordered["result"]!).All(row => !(row["query_text"]?.Text() ?? "").Contains("b_timed_workload_marker", StringComparison.Ordinal)), "Workload ordering lost database scope.");
        }
        await client.FailsAsync("get_top_queries", new { database = "a", order_by = "invalid" }, "invalid_order");
        await client.FailsAsync("get_top_queries", new { database = "b" }, "extension_missing");
        Console.WriteLine("PASS health sections, duplicate indexes, timed database-filtered workload and missing extension");
    }

    private static async Task VerifyPlansAsync(McpClient client)
    {
        const string sql = "SELECT * FROM tuning WHERE customer=42";
        JsonNode baseline = await client.OkAsync("explain_query", new { database = "a", sql });
        double baselineCost = baseline["plan"]!["total_cost"].Number();
        Check.That(baselineCost > 0, "Estimated plan did not return a cost.");
        JsonNode full = await client.OkAsync("explain_query", new { database = "a", sql, format = "json" });
        Check.That(full["plan"]![0]!["Plan"]!["Node Type"].Text() == "Seq Scan", "Unindexed fixture plan was not a sequential scan.");
        JsonNode actual = await client.OkAsync("explain_query", new { database = "a", sql = "SELECT * FROM orders WHERE id=1", analyze = true });
        Check.That(actual["analyzed"].Flag() && actual["plan"]!["execution_time_ms"].Number() >= 0, "ANALYZE did not report execution timing.");
        await client.FailsAsync("explain_query", new { database = "a", sql = "DELETE FROM marker", analyze = true }, "postgresql_error", "25006");
        string[] candidate = ["CREATE INDEX ON public.tuning(customer)"];
        JsonNode whatIf = await client.OkAsync("explain_query", new { database = "a", sql, indexes = candidate });
        Check.That(whatIf["comparison"]!["with_indexes_total_cost"].Number() < whatIf["comparison"]!["baseline_total_cost"].Number() && whatIf["hypothetical_indexes"].Array().Count > 0, "HypoPG candidate did not improve fixture planner cost.");
        await client.FailsAsync("explain_query", new { database = "b", sql, indexes = candidate }, "extension_missing");
        await client.FailsAsync("explain_query", new { database = "a", sql, indexes = new[] { candidate[0], "CREATE INDEX ON public.no_such_table(customer)" } }, "postgresql_error");
        JsonNode[] recovered = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.OkAsync("explain_query", new { database = "a", sql })));
        Check.That(recovered.All(plan => plan["plan"]!["total_cost"].Number() == baselineCost), "Hypothetical indexes leaked after partial failure or success.");
        JsonNode combined = await client.OkAsync("explain_query", new { database = "a", sql, indexes = new[] { candidate[0], "CREATE INDEX ON public.tuning(id)" } });
        Check.That(combined["hypothetical_indexes"].Array().Select(item => item!["candidate"].Int()).SequenceEqual(new[] { 0, 1 }), "Combined candidates lost attribution.");
        Check.That(combined["comparison"]!["with_indexes_total_cost"].Number() == whatIf["comparison"]!["with_indexes_total_cost"].Number(), "Independent hypothetical candidate unexpectedly changed selected fixture plan.");
        await client.FailsAsync("explain_query", new { database = "a", sql = "SELECT * FROM tuning", analyze = true, indexes = candidate }, "invalid_options");
        Check.Equal((await client.OkAsync("execute_sql", new { database = "a", sql = "SELECT count(*) AS n FROM pg_indexes WHERE tablename='tuning'" }))["rows"], new[] { new[] { 0 } }, "HypoPG created a permanent index.");
        Check.Equal((await client.OkAsync("execute_sql", new { database = "a", sql = "SELECT value FROM marker" }))["rows"], new[] { new[] { "A_ONLY" } }, "Plan analysis mutated fixture data.");
        Console.WriteLine("PASS estimated/JSON/ANALYZE plans, HypoPG cost comparison, candidate attribution and partial-failure cleanup");
    }

    private static async Task VerifyWritesAsync(Command command, Dictionary<string, string> environment, int port)
    {
        var writerEnvironment = new Dictionary<string, string>(environment)
        {
            ["POSTGRES_TARGETS"] = JsonSerializer.Serialize(new { a = $"Host=127.0.0.1;Port={port};Username=mcp_writer;Password=writer-disposable;Database=tenant_a" })
        };
        writerEnvironment.Remove("POSTGRES_ACCESS_MODE");
        await using var client = await McpClient.StartAsync(command, writerEnvironment);
        await client.FailsAsync("execute_sql", new { database = "a", sql = "INSERT INTO marker VALUES ('WRITE_OK')" }, "postgresql_error", "25006");
        JsonNode mutation = await client.OkAsync("execute_sql", new { database = "a", sql = "INSERT INTO marker VALUES ('WRITE_OK')", read_only = false });
        Check.That(mutation["rows_affected"].Int() == 1, "Write opt-in did not report affected rows.");
        Check.Equal((await client.OkAsync("execute_sql", new { database = "a", sql = "SELECT value FROM marker ORDER BY value" }))["rows"], new[] { new[] { "A_ONLY" }, new[] { "WRITE_OK" } }, "Write transaction did not commit.");
        await client.FailsAsync("execute_sql", new { database = "a", sql = "DELETE FROM marker RETURNING *", offset = 1, read_only = false }, "invalid_offset");
        await client.FailsAsync("execute_sql", new { database = "a", sql = "SELECT * FROM secret", read_only = false }, "postgresql_error", "42501");
        await client.OkAsync("execute_sql", new { database = "a", sql = "DELETE FROM marker WHERE value='WRITE_OK'", read_only = false });
        await client.OkAsync("execute_sql", new { database = "a", sql = "CREATE TABLE mcp_write_test(id integer)", read_only = false });
        Check.Equal((await client.OkAsync("execute_sql", new { database = "a", sql = "SELECT to_regclass('public.mcp_write_test')::text" }))["rows"], new[] { new[] { "mcp_write_test" } }, "Destructive DDL did not commit.");
        await client.FailsAsync("execute_sql", new { database = "a", sql = "INSERT INTO mcp_write_test SELECT 100/(g-2) FROM generate_series(1,3) g", read_only = false }, "postgresql_error", "22012");
        Check.Equal((await client.OkAsync("execute_sql", new { database = "a", sql = "SELECT count(*) FROM mcp_write_test" }))["rows"], new[] { new[] { 0 } }, "Failed write transaction did not roll back.");
        JsonNode returning = await client.OkAsync("execute_sql", new { database = "a", sql = "INSERT INTO mcp_write_test SELECT g FROM generate_series(1,10) g RETURNING id", limit = 3, read_only = false });
        Check.That(returning["truncated"].Flag() && returning["next_offset"] is null, "Write RETURNING incorrectly advertised replay pagination.");
        Check.Equal((await client.OkAsync("execute_sql", new { database = "a", sql = "SELECT count(*) FROM mcp_write_test" }))["rows"], new[] { new[] { 10 } }, "Truncated RETURNING did not commit all writes.");
        await client.OkAsync("execute_sql", new { database = "a", sql = "DROP TABLE mcp_write_test", read_only = false });
        await client.StopAsync();
        Check.Confidential(client.StandardError, "writer-disposable");
        Console.WriteLine("PASS default unrestricted write opt-in, DML/DDL commit, rollback, credential boundaries and no write replay");
    }

    private static async Task VerifyDiscoveryAsync(Command command, Dictionary<string, string> environment, PostgresFixture fixture, string connection)
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, "seed.json");
        string seed = JsonSerializer.Serialize(new { primary = connection + "tenant_a" });
        await File.WriteAllTextAsync(path, seed);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var fileEnvironment = new Dictionary<string, string>(environment) { ["POSTGRES_TARGETS_FILE"] = path };
        fileEnvironment.Remove("POSTGRES_TARGETS");
        await using (var client = await McpClient.StartAsync(command, fileEnvironment))
        {
            Check.Equal((await client.OkAsync("execute_sql", new { database = "tenant_b", target = "primary", sql = "SELECT current_database(),value FROM marker" }))["rows"],
                new[] { new[] { "tenant_b", "B_ONLY" } }, "Optional seed file did not select another physical database.");
            Check.That(await File.ReadAllTextAsync(path) == seed, "Dynamic selection rewrote the protected seed file.");
            await client.StopAsync();
            Check.Confidential(client.StandardError, "reader-disposable", path);
        }
        foreach (var (key, value, validationCommand) in new[]
        {
            ("POSTGRES_TARGETS", seed, command.With("--validate")),
            ("POSTGRES_TARGETS_FILE", path, command.With("--validate")),
            ("", path, command.With("--targets-file", path, "--validate"))
        })
        {
            var conflictEnvironment = new Dictionary<string, string>(environment)
            {
                ["POSTGRES_CONNECTION_STRING"] = connection + "tenant_a"
            };
            conflictEnvironment.Remove("POSTGRES_TARGETS");
            if (key.Length != 0) conflictEnvironment[key] = value;
            ProcessResult conflict = await Processes.RunAsync(validationCommand, conflictEnvironment, expected: 1);
            Check.That(conflict.Output.Length == 0, "Conflicting environment/profile configuration contaminated stdout.");
            Check.Confidential(conflict.Error, "reader-disposable", connection + "tenant_a", path);
        }
        var discoveryEnvironment = new Dictionary<string, string>(environment)
        {
            ["POSTGRES_CONNECTION_STRING"] = connection + "tenant_a"
        };
        discoveryEnvironment.Remove("POSTGRES_TARGETS");
        await using (var client = await McpClient.StartAsync(command, discoveryEnvironment))
        {
            JsonNode listed = await client.OkAsync("list_databases");
            var initial = Check.Rows(listed["databases"]!).ToArray();
            Check.That(initial.Any(row => row["name"].Text() == "tenant_a" && row["is_current"].Flag())
                && initial.Any(row => row["name"].Text() == "tenant_b" && !row["is_current"].Flag()),
                "Environment-only primary seed did not discover other physical databases or mark the bootstrap database.");
            var requests = Enumerable.Range(0, 24).Select(index =>
            {
                string database = index % 2 == 0 ? "tenant_a" : "tenant_b";
                return (Database: database, Task: client.OkAsync("execute_sql", new { database,
                    sql = "SELECT current_database(),value FROM marker CROSS JOIN LATERAL (SELECT pg_sleep(0.02)) s" }));
            }).ToArray();
            JsonNode[] results = await Task.WhenAll(requests.Select(request => request.Task));
            for (int index = 0; index < results.Length; index++)
                Check.Equal(results[index]["rows"], new[] { new[] { requests[index].Database,
                    requests[index].Database == "tenant_a" ? "A_ONLY" : "B_ONLY" } }, "Concurrent physical-name selection crossed a database boundary.");
            await VerifyOptionalTargetAsync(client);
            await fixture.CreateDatabaseAsync(PostgresFixture.PunctuationDatabase);
            await fixture.CreateDatabaseAsync("tenant_after_start");
            await fixture.CreateDatabaseAsync("tenant_no_connections");
            await fixture.SqlAsync("postgres", "ALTER DATABASE tenant_no_connections ALLOW_CONNECTIONS false;");
            var fresh = Check.Rows((await client.OkAsync("list_databases"))["databases"]!).Select(row => row["name"].Text()).ToHashSet();
            Check.That(fresh.Contains("tenant_after_start") && fresh.Contains(PostgresFixture.PunctuationDatabase)
                && !fresh.Contains("tenant_no_connections"), "Live discovery cached the catalog or included a nonconnectable database.");
            Check.Equal((await client.OkAsync("execute_sql", new { database = PostgresFixture.PunctuationDatabase, target = "primary",
                sql = "SELECT current_database(),value FROM marker" }))["rows"],
                new[] { new[] { PostgresFixture.PunctuationDatabase, "DYNAMIC_ONLY" } }, "Punctuation in a physical name escaped its connection-string boundary.");
            await fixture.SqlAsync("postgres", "REVOKE CONNECT ON DATABASE tenant_after_start FROM PUBLIC,mcp_reader;");
            Check.That(Check.Rows((await client.OkAsync("list_databases"))["databases"]!).All(row => row["name"].Text() != "tenant_after_start"),
                "Revoked CONNECT remained visible in discovery.");
            await client.FailsAsync("execute_sql", new { database = "tenant_after_start", sql = "SELECT 1" }, "postgresql_error", "42501");
            await client.FailsAsync("execute_sql", new { database = "physical_missing", target = "primary", sql = "SELECT 1" }, "postgresql_error", "3D000");
            await client.FailsAsync("execute_sql", new { database = "tenant_b", target = "unknown_profile", sql = "SELECT 1" }, "invalid_target");
            Check.Equal((await client.OkAsync("execute_sql", new { database = "tenant_b", sql = "SELECT value FROM marker" }))["rows"],
                new[] { new[] { "B_ONLY" } }, "Failed dynamic selection fell back or poisoned another database.");
            await client.StopAsync();
            Check.Confidential(client.StandardError, "reader-disposable");
        }
        var precedenceEnvironment = new Dictionary<string, string>(environment)
        {
            ["POSTGRES_TARGETS"] = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["a"] = connection + "tenant_b", ["primary"] = connection + "tenant_a", ["tenant_b"] = connection + "tenant_a"
            })
        };
        await using (var client = await McpClient.StartAsync(command, precedenceEnvironment))
        {
            Check.That(Check.Rows((await client.OkAsync("list_databases"))["databases"]!).Single(row => row["is_current"].Flag())["name"].Text() == "tenant_a",
                "Default profile did not prefer primary.");
            Check.Equal((await client.OkAsync("execute_sql", new { database = "tenant_b", sql = "SELECT current_database()" }))["rows"],
                new[] { new[] { "tenant_a" } }, "Exact configured alias no longer selected its bootstrap database.");
            Check.Equal((await client.OkAsync("execute_sql", new { database = "tenant_b", target = "primary", sql = "SELECT current_database()" }))["rows"],
                new[] { new[] { "tenant_b" } }, "Explicit profile treated a physical database name as an alias.");
        }
        Console.WriteLine("PASS optional immutable protected seed file, env/profile conflict rejection, environment-only primary seed, concurrent physical selection, nine optional targets, live create/revoke, profile precedence and punctuation-safe names");
    }

    private static async Task VerifyOptionalTargetAsync(McpClient client)
    {
        Check.Equal((await client.OkAsync("execute_sql", new { database = "tenant_b", target = "primary", sql = "SELECT value FROM marker" }))["rows"],
            new[] { new[] { "B_ONLY" } }, "Explicit target failed to select another physical database.");
        Check.That(Check.Rows((await client.OkAsync("list_databases", new { target = "primary" }))["databases"]!).Any(row => row["name"].Text() == "tenant_b"),
            "Explicit discovery profile lost an accessible database.");
        Check.That(Check.Rows(await client.OkAsync("list_schemas", new { database = "tenant_b", target = "primary", prefix = "app" }))
            .Select(row => row["schema_name"].Text()).SequenceEqual(new[] { "app" }), "Explicit profile schema discovery failed.");
        Check.That(Check.Rows(await client.OkAsync("list_objects", new { database = "tenant_b", target = "primary", schema = "public", type = "table" }))
            .Any(row => row["name"].Text() == "marker"), "Explicit profile object discovery failed.");
        Check.That(Check.Rows((await client.OkAsync("get_object_details", new { database = "tenant_b", target = "primary", schema = "public", name = "marker", section = "columns" }))["page"]!)
            .Single()["name"].Text() == "value", "Explicit profile object details failed.");
        JsonNode plan = await client.OkAsync("explain_query", new { database = "tenant_b", target = "primary", sql = "SELECT value FROM marker", format = "json" });
        Check.That(plan["plan"]![0]!["Plan"]!["Relation Name"].Text() == "marker", "Explicit profile plan selected the wrong relation.");
        await client.FailsAsync("get_top_queries", new { database = "tenant_b", target = "primary" }, "extension_missing");
        JsonNode indexes = await client.OkAsync("analyze_indexes", new { database = "tenant_b", target = "primary", schema = "public", table = "orders" });
        Check.That(Check.Rows(indexes["result"]!).Count(row => row["index_name"].Text().StartsWith("orders_customer_duplicate", StringComparison.Ordinal)) == 2,
            "Explicit profile index analysis lost database evidence.");
        JsonNode health = await client.OkAsync("analyze_db_health", new { database = "tenant_b", target = "primary", section = "summary" });
        Check.That(Check.Rows(health["result"]!).Single()["database_name"].Text() == "tenant_b", "Explicit profile health evidence crossed databases.");
    }

    // An upgraded cluster keeps its old extension version until ALTER EXTENSION UPDATE: the tool must adapt or say what to do.
    private static async Task VerifyStatementsExtensionVersionsAsync(Command command, Dictionary<string, string> environment, PostgresFixture fixture, string connection)
    {
        var singleProfile = new Dictionary<string, string>(environment) { ["POSTGRES_CONNECTION_STRING"] = connection + "tenant_a" };
        singleProfile.Remove("POSTGRES_TARGETS");
        foreach (string database in new[] { "stats_ext_v18", "stats_ext_v17" }) await fixture.CreateDatabaseAsync(database);
        // 1.8 is the oldest supported layout (PostgreSQL 13) and has no toplevel; 1.7 predates total_exec_time. Both install on 13-18.
        await fixture.SqlAsync("stats_ext_v18", "CREATE EXTENSION pg_stat_statements VERSION '1.8'; SELECT pg_sleep(0.02) AS stats_ext_v18_marker;");
        await fixture.SqlAsync("stats_ext_v17", "CREATE EXTENSION pg_stat_statements VERSION '1.7'; SELECT pg_sleep(0.02) AS stats_ext_v17_marker;");
        await using var client = await McpClient.StartAsync(command, singleProfile);
        foreach (string order in new[] { "total_time", "mean_time", "calls", "rows", "reads" })
        {
            JsonNode ranked = await client.OkAsync("get_top_queries", new { database = "stats_ext_v18", order_by = order, limit = 5 });
            var rows = Check.Rows(ranked["result"]!).ToArray();
            Check.That(rows.Length > 0 && rows.All(row => row.ContainsKey("toplevel") && row["toplevel"] is null),
                "Extension 1.8 must return a consistently null toplevel column.");
            Check.That(ranked["notes"].Text().Contains("toplevel is null", StringComparison.Ordinal), "Missing toplevel was not explained.");
        }
        Check.That(Check.Rows((await client.OkAsync("get_top_queries", new { database = "stats_ext_v18", limit = 5 }))["result"]!)
            .Any(row => (row["query_text"]?.Text() ?? "").Contains("stats_ext_v18_marker", StringComparison.Ordinal)), "Extension 1.8 ranking lost the workload probe.");
        JsonNode outdated = await client.FailsAsync("get_top_queries", new { database = "stats_ext_v17" }, "extension_outdated");
        string message = outdated["error"]!["message"].Text();
        Check.That(message.Contains("ALTER EXTENSION pg_stat_statements UPDATE", StringComparison.Ordinal) && message.Contains("1.7", StringComparison.Ordinal)
            && outdated["error"]!["sql_state"] is null, "Outdated-extension error was not specific and actionable.");
        await fixture.SqlAsync("stats_ext_v17", "ALTER EXTENSION pg_stat_statements UPDATE TO '1.8'; SELECT pg_sleep(0.02) AS stats_ext_v17_updated_marker;");
        await client.OkAsync("get_top_queries", new { database = "stats_ext_v17", limit = 5 });
        Console.WriteLine("PASS pg_stat_statements 1.8 without toplevel, outdated 1.7 guidance and recovery after ALTER EXTENSION UPDATE");
    }

    private static async Task VerifyPoolCapacityAsync(Command command, Dictionary<string, string> environment, PostgresFixture fixture, string connection)
    {
        string[] databases = Enumerable.Range(0, 10).Select(index => "pool_churn_" + index).ToArray();
        foreach (string database in databases) await fixture.CreateDatabaseAsync(database);
        var poolEnvironment = new Dictionary<string, string>(environment)
        {
            ["POSTGRES_CONNECTION_STRING"] = connection + "tenant_a",
            ["POSTGRES_POOL_SIZE"] = "32", ["POSTGRES_MAX_CONCURRENT_CALLS"] = "16"
        };
        poolEnvironment.Remove("POSTGRES_TARGETS");
        await using (var client = await McpClient.StartAsync(command, poolEnvironment))
        {
            foreach (string database in databases)
            {
                Check.Equal((await client.OkAsync("execute_sql", new { database, sql = "SELECT current_database(),value FROM marker" }))["rows"],
                    new[] { new[] { database, "DYNAMIC_ONLY" } }, "Pool churn changed physical database selection.");
                Check.That(await fixture.RuntimeBackendCountAsync() <= 8, "Idle pool eviction did not bound runtime backend retention.");
            }
            for (int index = 0; index < 10; index++)
                await client.FailsAsync("execute_sql", new { database = "failed_pool_" + index, sql = "SELECT 1" }, "postgresql_error", "3D000");
            Task<JsonNode>[] active = databases.Take(8).Select(database =>
                client.FailsAsync("execute_sql", new { database, sql = "SELECT pg_sleep(10)::text" }, "timeout")).ToArray();
            bool saturated = false;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (await fixture.RuntimeBackendCountAsync(activeOnly: true) == 8) { saturated = true; break; }
                await Task.Delay(50);
            }
            Check.That(saturated, "Active-capacity fixture never reached eight simultaneous physical pools.");
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            Task<JsonNode> waiting = client.FailsAsync("execute_sql", new { database = databases[8], sql = "SELECT pg_sleep(2)::text" }, "timeout");
            await Task.Delay(100);
            Check.That(await fixture.RuntimeBackendCountAsync() <= 8, "A busy pool was evicted or active capacity was exceeded.");
            await waiting;
            Check.That(elapsed.Elapsed < TimeSpan.FromSeconds(4.5), "Capacity waiting reset the original query deadline.");
            await Task.WhenAll(active);
            Check.Equal((await client.OkAsync("execute_sql", new { database = databases[9], sql = "SELECT value FROM marker" }))["rows"],
                new[] { new[] { "DYNAMIC_ONLY" } }, "Timed-out capacity wait leaked a lease or poisoned later selection.");
        }
        Check.That(await fixture.RuntimeBackendCountAsync() == 0, "Runtime disposal retained physical-database backends.");
        Console.WriteLine("PASS environment-only bounded idle churn, failed-name recovery, busy-pool capacity, original-deadline cancellation and deterministic disposal");
    }

    private static async Task VerifyConfigurationAsync(Command command, Dictionary<string, string> environment, Dictionary<string, string> targets, string connection)
    {
        var validationEnvironment = new Dictionary<string, string>(environment)
        {
            ["POSTGRES_TARGETS"] = JsonSerializer.Serialize(new { a = targets["a"], b = targets["b"] })
        };
        ProcessResult preflight = await Processes.RunAsync(command.With("--validate"), validationEnvironment);
        Check.That(preflight.Output.Length == 0, "Validation contaminated protocol stdout.");
        JsonNode[] reports = preflight.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
        Check.That(reports.Select(report => report["database"].Text()).ToHashSet().SetEquals(new[] { "a", "b" })
            && reports.Select(report => report["rows"]![0]![0].Text()).ToHashSet().SetEquals(new[] { "tenant_a", "tenant_b" }), "Preflight did not validate both configured targets.");
        var baseEnvironment = new Dictionary<string, string>(environment);
        baseEnvironment.Remove("POSTGRES_TARGETS");
        baseEnvironment["POSTGRES_CONNECTION_STRING"] = connection[..^"Database=".Length];
        await using (var client = await McpClient.StartAsync(command, baseEnvironment))
        {
            Check.That(Check.Rows((await client.OkAsync("list_databases"))["databases"]!).Single(row => row["is_current"].Flag())["name"].Text() == "postgres",
                "Connection string without Database did not bootstrap postgres.");
            Check.Equal((await client.OkAsync("execute_sql", new { database = "tenant_a", sql = "SELECT value FROM marker" }))["rows"],
                new[] { new[] { "A_ONLY" } }, "Default postgres bootstrap prevented physical selection.");
        }
        ProcessResult unsafeCli = await Processes.RunAsync(command.With("--sensitive-cli-marker"), environment, expected: 1);
        Check.That(unsafeCli.Output.Length == 0, "Unknown CLI input contaminated stdout.");
        Check.Confidential(unsafeCli.Error, "sensitive-cli-marker");
        if (!OperatingSystem.IsWindows() && !Unix.IsRoot)
        {
            using var temporary = new TemporaryDirectory();
            string path = Path.Combine(temporary.Path, "sensitive-target-file-marker.json");
            await File.WriteAllTextAsync(path, "{}");
            File.SetUnixFileMode(path, UnixFileMode.None);
            try
            {
                ProcessResult denied = await Processes.RunAsync(command.With("--targets-file", path, "--validate"), Processes.CleanEnvironment(), expected: 1);
                Check.That(denied.Output.Length == 0, "Denied targets file contaminated stdout.");
                Check.Confidential(denied.Error, path, "Exception");
            }
            finally { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        }
        string[] invalidTargets = ["{\"a\":\"Host=localhost;Username=test\"}", "{", "[]", "{}", "{\"a\":\"Host=localhost;Database=test;Password=sensitive-configuration-marker;No Reset On Close=true\"}"];
        foreach (string invalid in invalidTargets)
        {
            var invalidEnvironment = new Dictionary<string, string>(environment) { ["POSTGRES_TARGETS"] = invalid };
            ProcessResult bad = await Processes.RunAsync(command.With("--validate"), invalidEnvironment, expected: 1);
            Check.That(bad.Output.Length == 0, "Invalid configuration contaminated stdout.");
            Check.Confidential(bad.Error, "sensitive-configuration-marker", "reader-disposable");
        }
        foreach (var (key, value) in new[] { ("POSTGRES_POOL_SIZE", "0"), ("POSTGRES_MAX_CONCURRENT_CALLS", "0"), ("POSTGRES_MAX_RESULT_BYTES", "4095"), ("POSTGRES_MAX_ROWS", "0"), ("POSTGRES_MAX_CELL_CHARS", "0"), ("POSTGRES_QUERY_TIMEOUT", "0"), ("POSTGRES_ACCESS_MODE", "invalid") })
        {
            var invalidEnvironment = new Dictionary<string, string>(validationEnvironment) { [key] = value };
            ProcessResult bad = await Processes.RunAsync(command.With("--validate"), invalidEnvironment, expected: 1);
            Check.That(bad.Output.Length == 0, "Invalid resource configuration contaminated stdout.");
            Check.Confidential(bad.Error, "reader-disposable");
        }
        Console.WriteLine("PASS preflight, unreadable targets, unknown options and invalid configuration");
    }
}
