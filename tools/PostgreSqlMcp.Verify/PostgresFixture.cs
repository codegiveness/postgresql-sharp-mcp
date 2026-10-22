// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace PostgreSqlMcp.Verify;

internal sealed class PostgresFixture : IAsyncDisposable
{
    private readonly string name = "postgresql-sharp-mcp-verify-" + Guid.NewGuid().ToString("N");
    private bool started;
    private bool attempted;
    public int Port { get; private set; }
    public const string PunctuationDatabase = "tenant ' ; \" = punctuation";
    public const int DefaultMajor = 17;

    // Manifest-list digests of the official postgres bookworm tags, read from the Docker Hub registry API on 2026-10-22. The tag is
    // <major>-bookworm for released majors. PostgreSQL 19 is still a pre-release, so its tag names the release candidate explicitly;
    // move it to 19-bookworm once the final release is published. Dependabot does not track them: refresh deliberately (see
    // CONTRIBUTING.md) and review the resulting image change.
    private static readonly Dictionary<int, (string Tag, string Digest)> Images = new()
    {
        [13] = ("13-bookworm", "sha256:f0cffcc050a9f1f3c78a9968e221badc1cdd02e2ae15b1de9b12bee1ba5ea2db"),
        [14] = ("14-bookworm", "sha256:eba39627b02201a04e851363546d34547cc82550a8d7f68c6e9ff6c8b2c6ff33"),
        [15] = ("15-bookworm", "sha256:22c6356b2897cb0b4be6ad550b44ff97a0f29df7dbe327c01bc3c84bd457694d"),
        [16] = ("16-bookworm", "sha256:d81f7b51697014367236d07ec58b54153fc28f828b72bf247fe5c7487520bcb0"),
        [17] = ("17-bookworm", "sha256:858bb5e98eed02eb8cafbcdc007f9a65d1b613972028944a8ec2133ccaf80d27"),
        [18] = ("18-bookworm", "sha256:bbf83594798e144bff365a9c191c5d779f181fb3135902064ab8ff11918020fc"),
        [19] = ("19rc1-bookworm", "sha256:d1123bed61c903d36aa7c4ede13d7641d149fe4b82205b4e98ab1765b6cc6b5a")
    };

    // Exact pgdg bookworm package version, the same for majors 13-19 on amd64 when it was read on 2026-10-22 (19 comes from the
    // pgdg "19" pre-release component the image already enables). The pgdg index keeps only recent versions, so fixture start fails
    // loudly once this one is withdrawn; bump it with the image digests.
    private const string HypoPgVersion = "1.4.3-1.pgdg12+2";

    // Resolved per use (not in a static initializer) so an invalid value is reported as a verification failure.
    public static int Major
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable("POSTGRES_FIXTURE_MAJOR");
            if (string.IsNullOrEmpty(value)) return DefaultMajor;
            Check.That(int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int major) && Images.ContainsKey(major),
                $"POSTGRES_FIXTURE_MAJOR must be one of {string.Join(", ", Images.Keys.Order())} (default {DefaultMajor}).");
            return major;
        }
    }

    /// <summary>The digest-pinned official image for <see cref="Major"/>.</summary>
    public static string Image => $"postgres:{Images[Major].Tag}@{Images[Major].Digest}";

    public async Task StartAsync()
    {
        // This verifier never attaches to an existing container or operator database.
        attempted = true;
        await Processes.RunAsync(new("docker", "run", "-d", "--name", name, "-e", "POSTGRES_PASSWORD=mcp-disposable-only",
            "-p", "127.0.0.1::5432", Image, "-c", "shared_preload_libraries=pg_stat_statements"), timeout: 180);
        started = true;
        bool ready = false;
        for (int attempt = 0; attempt < 60; attempt++)
        {
            var result = await Processes.RunAsync(new("docker", "exec", name, "pg_isready", "-h", "127.0.0.1", "-U", "postgres"), expected: null);
            if (result.ExitCode == 0) { ready = true; break; }
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        Check.That(ready, "Disposable PostgreSQL fixture did not become ready.");
        // A mislabeled tag/digest pair would otherwise silently verify another major.
        string versionNumber = (await Processes.RunAsync(new("docker", "exec", name, "psql", "-X", "-At", "-U", "postgres", "-c", "SHOW server_version_num"))).Output.Trim();
        Check.That(int.TryParse(versionNumber, NumberStyles.None, CultureInfo.InvariantCulture, out int serverVersion) && serverVersion / 10000 == Major,
            $"Fixture image for PostgreSQL {Major} reports server_version_num {versionNumber}.");
        await Processes.RunAsync(new("docker", "exec", name, "apt-get", "update", "-qq"), timeout: 180);
        await Processes.RunAsync(new("docker", "exec", name, "apt-get", "install", "-y", "-qq", $"postgresql-{Major}-hypopg={HypoPgVersion}"), timeout: 180);
        string endpoint = (await Processes.RunAsync(new("docker", "port", name, "5432/tcp"))).Output.Trim();
        Port = int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
        await SeedAsync();
    }

    public Task<ProcessResult> SqlAsync(string database, string sql) => Processes.RunAsync(
        new("docker", "exec", "-i", "-e", "PGDATABASE=" + database, name, "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres"), input: sql);

    public async Task CreateDatabaseAsync(string database)
    {
        await SqlAsync("postgres", $"CREATE DATABASE {Identifier(database)};");
        await SqlAsync(database, "CREATE TABLE marker(value text NOT NULL); INSERT INTO marker VALUES ('DYNAMIC_ONLY'); GRANT SELECT ON marker TO mcp_reader,mcp_writer;");
    }

    public async Task<int> RuntimeBackendCountAsync(bool activeOnly = false)
    {
        ProcessResult result = await SqlAsync("postgres", "SELECT count(*) FROM pg_stat_activity WHERE application_name='postgresql-sharp-mcp'"
            + (activeOnly ? " AND state='active'" : "") + ";");
        string value = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())
            .Single(line => int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out _));
        return int.Parse(value, CultureInfo.InvariantCulture);
    }

    public static string Identifier(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private async Task SeedAsync()
    {
        await SqlAsync("postgres", """
            CREATE ROLE mcp_reader LOGIN PASSWORD 'reader-disposable';
            CREATE ROLE mcp_writer LOGIN PASSWORD 'writer-disposable';
            GRANT pg_read_all_stats TO mcp_reader;
            CREATE DATABASE tenant_a;
            CREATE DATABASE tenant_b;
            CREATE DATABASE tenant_denied;
            REVOKE CONNECT ON DATABASE tenant_denied FROM PUBLIC;
            GRANT CONNECT ON DATABASE tenant_a, tenant_b TO mcp_reader, mcp_writer;
            """);
        foreach (var (database, marker) in new[] { ("tenant_a", "A_ONLY"), ("tenant_b", "B_ONLY") })
            await SqlAsync(database, $$"""
                CREATE TABLE marker(value text NOT NULL);
                INSERT INTO marker VALUES ('{{marker}}');
                CREATE TABLE orders(id integer PRIMARY KEY, customer integer NOT NULL, note text DEFAULT 'sample');
                INSERT INTO orders SELECT g,g%1000,'row-'||g FROM generate_series(1,20000) g;
                CREATE INDEX orders_customer_duplicate_1 ON orders(customer);
                CREATE INDEX orders_customer_duplicate_2 ON orders(customer);
                CREATE TABLE tuning(id integer NOT NULL, customer integer NOT NULL);
                INSERT INTO tuning SELECT g,g%1000 FROM generate_series(1,50000) g;
                ANALYZE orders; ANALYZE tuning;
                CREATE TABLE secret(value text);
                INSERT INTO secret VALUES ('not-granted');
                CREATE SCHEMA app;
                CREATE VIEW app.marker_view AS SELECT value FROM public.marker;
                CREATE FUNCTION app.lookup(n integer) RETURNS integer LANGUAGE sql AS $fn$SELECT n$fn$;
                CREATE FUNCTION app.lookup(n text) RETURNS text LANGUAGE sql AS $fn$SELECT n$fn$;
                CREATE PROCEDURE app.noop(n integer) LANGUAGE sql AS $fn$SELECT n$fn$;
                CREATE SEQUENCE app.counter;
                CREATE FUNCTION app.touch() RETURNS trigger LANGUAGE plpgsql AS $fn$BEGIN RETURN NEW; END$fn$;
                CREATE TRIGGER orders_touch BEFORE INSERT ON orders FOR EACH ROW EXECUTE FUNCTION app.touch();
                GRANT USAGE ON SCHEMA public,app TO mcp_reader,mcp_writer;
                GRANT SELECT ON marker,orders,tuning,app.marker_view TO mcp_reader,mcp_writer;
                GRANT USAGE,SELECT ON SEQUENCE app.counter TO mcp_reader,mcp_writer;
                GRANT INSERT,UPDATE,DELETE ON marker TO mcp_writer;
                CREATE TABLE scoped_rows(id integer, owner_name text NOT NULL);
                INSERT INTO scoped_rows VALUES (1,'mcp_reader'),(2,'mcp_writer');
                ALTER TABLE scoped_rows ENABLE ROW LEVEL SECURITY;
                CREATE POLICY own_rows ON scoped_rows USING (owner_name=current_user);
                GRANT SELECT ON scoped_rows TO mcp_reader,mcp_writer;
                CREATE FUNCTION app.fail_with_sensitive_diagnostic() RETURNS integer LANGUAGE plpgsql AS $fn$
                BEGIN RAISE EXCEPTION 'sensitive-error-marker' USING HINT='sensitive-hint-marker'; END$fn$;
                CREATE FUNCTION app.fail_with_sensitive_access_error() RETURNS integer LANGUAGE plpgsql AS $fn$
                BEGIN RAISE EXCEPTION USING ERRCODE='42501', MESSAGE='sensitive-error-marker', HINT='sensitive-hint-marker'; END$fn$;
                GRANT CREATE ON SCHEMA public TO mcp_writer;
                """);
        await SqlAsync("tenant_a", """
            CREATE SCHEMA extensions;
            CREATE EXTENSION pg_stat_statements WITH SCHEMA extensions;
            CREATE EXTENSION hypopg WITH SCHEMA extensions;
            GRANT USAGE ON SCHEMA extensions TO mcp_reader;
            SELECT value AS a_workload_marker FROM marker;
            """);
        await SqlAsync("tenant_b", "SELECT value AS b_workload_marker FROM marker;");
    }

    public async ValueTask DisposeAsync()
    {
        if (attempted)
            await Processes.RunAsync(new("docker", "rm", "-f", name), expected: started ? 0 : null);
    }
}
