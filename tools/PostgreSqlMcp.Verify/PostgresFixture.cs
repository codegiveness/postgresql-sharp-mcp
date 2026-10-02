using System.Globalization;

namespace PostgreSqlMcp.Verify;

internal sealed class PostgresFixture : IAsyncDisposable
{
    private readonly string name = "postgresql-sharp-mcp-verify-" + Guid.NewGuid().ToString("N");
    private bool started;
    private bool attempted;
    public int Port { get; private set; }

    public async Task StartAsync()
    {
        // This verifier never attaches to an existing container or operator database.
        attempted = true;
        await Processes.RunAsync(new("docker", "run", "-d", "--name", name, "-e", "POSTGRES_PASSWORD=mcp-disposable-only",
            "-p", "127.0.0.1::5432", "postgres:17-bookworm", "-c", "shared_preload_libraries=pg_stat_statements"), timeout: 180);
        started = true;
        bool ready = false;
        for (int attempt = 0; attempt < 60; attempt++)
        {
            var result = await Processes.RunAsync(new("docker", "exec", name, "pg_isready", "-h", "127.0.0.1", "-U", "postgres"), expected: null);
            if (result.ExitCode == 0) { ready = true; break; }
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        Check.That(ready, "Disposable PostgreSQL fixture did not become ready.");
        await Processes.RunAsync(new("docker", "exec", name, "apt-get", "update", "-qq"), timeout: 180);
        await Processes.RunAsync(new("docker", "exec", name, "apt-get", "install", "-y", "-qq", "postgresql-17-hypopg"), timeout: 180);
        string endpoint = (await Processes.RunAsync(new("docker", "port", name, "5432/tcp"))).Output.Trim();
        Port = int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
        await SeedAsync();
    }

    public Task<ProcessResult> SqlAsync(string database, string sql) => Processes.RunAsync(
        new("docker", "exec", "-i", name, "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", database), input: sql);

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
