using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace PostgreSqlMcp.Core;

public sealed class ServerOptions
{
    public required IReadOnlyDictionary<string, string> Targets { get; init; }
    /// <summary>Database lock: the only physical databases this server may connect to, in configured order.</summary>
    public IReadOnlyList<string>? AllowedDatabases { get; init; }
    public bool Unrestricted { get; init; } = true;
    public int QueryTimeout { get; init; } = 30;
    public int MaxRows { get; init; } = 1000;
    public int MaxResultBytes { get; init; } = 65536;
    public int MaxCellChars { get; init; } = 4096;
    public int PoolSize { get; init; } = 8;
    public int MaxConcurrentCalls { get; init; } = 16;
    public string LogLevel { get; init; } = "warning";
    public bool Validate { get; init; }

    public static ServerOptions Parse(string[] args)
    {
        var cli = new Dictionary<string, string>(StringComparer.Ordinal);
        bool validate = false, requireLock = false;
        string[] allowed = ["--targets-file", "--connection-string", "--databases", "--access-mode", "--query-timeout", "--log-level"];
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--validate") { validate = true; continue; }
            if (args[i] == "--require-database-lock") { requireLock = true; continue; }
            int equals = args[i].IndexOf('=');
            string key = equals < 0 ? args[i] : args[i][..equals];
            if (!allowed.Contains(key, StringComparer.Ordinal)) throw new ToolException("configuration", "Unknown option. Use --help for supported options.");
            string value = equals < 0
                ? ++i < args.Length ? args[i] : throw new ToolException("configuration", $"Missing value for {key}.")
                : args[i][(equals + 1)..];
            if (!cli.TryAdd(key, value)) throw new ToolException("configuration", $"Duplicate option: {key}.");
        }
        string? Env(string name) => Environment.GetEnvironmentVariable("POSTGRES_" + name);
        string? Value(string key, string env) => cli.GetValueOrDefault(key) ?? Env(env);
        int Number(string env, int fallback, int min, int max, string? key = null)
        {
            string? text = key is null ? Env(env) : Value(key, env);
            if (text is null) return fallback;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < min || n > max)
                throw new ToolException("configuration", $"POSTGRES_{env} must be {min}..{max}.");
            return n;
        }
        string? requireText = Env("REQUIRE_DATABASE_LOCK");
        if (!requireLock && requireText is not null && !bool.TryParse(requireText, out requireLock))
            throw new ToolException("configuration", "POSTGRES_REQUIRE_DATABASE_LOCK must be true or false.");
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        List<string>? allowedDatabases = null;
        try
        {
            string? json = Env("TARGETS");
            string? file = Value("--targets-file", "TARGETS_FILE");
            string? baseString = Env("CONNECTION_STRING") ?? cli.GetValueOrDefault("--connection-string");
            string? names = Value("--databases", "DATABASES");
            if ((json is not null || file is not null) && baseString is not null)
                throw new ToolException("configuration", "Use targets JSON/file OR a connection string, not both.");
            if (names is not null)
            {
                using var doc = JsonDocument.Parse(names);
                allowedDatabases = [];
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    string db = item.GetString() ?? "";
                    if (!DatabaseRegistry.IsDatabaseName(db))
                        throw new ToolException("configuration", "POSTGRES_DATABASES entries must be database names of 1..63 UTF-8 bytes without NUL.");
                    if (allowedDatabases.Contains(db)) throw new ToolException("configuration", "Duplicate database name in POSTGRES_DATABASES.");
                    allowedDatabases.Add(db);
                }
                if (allowedDatabases.Count == 0) throw new ToolException("configuration", "POSTGRES_DATABASES must list at least one database.");
            }
            if (json is null && file is not null) json = File.ReadAllText(file);
            if (json is not null)
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ToolException("configuration", "Targets must be a JSON object of alias: connection-string pairs.");
                foreach (var item in doc.RootElement.EnumerateObject())
                    if (!targets.TryAdd(item.Name, item.Value.GetString() ?? ""))
                        throw new ToolException("configuration", "Duplicate target alias.");
            }
            else if (baseString is not null)
            {
                var builder = new NpgsqlConnectionStringBuilder(baseString);
                if (allowedDatabases is not null)
                {
                    foreach (string db in allowedDatabases)
                    {
                        builder.Database = db;
                        targets.Add(db, builder.ConnectionString);
                    }
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(builder.Database)) builder.Database = "postgres";
                    targets.Add("primary", builder.ConnectionString);
                }
            }
            else throw new ToolException("configuration", "Set POSTGRES_CONNECTION_STRING, POSTGRES_TARGETS, or POSTGRES_TARGETS_FILE.");
            if (targets.Count is < 1 or > 32) throw new ToolException("configuration", "Configure 1..32 database targets.");
            foreach (var (alias, connectionString) in targets)
            {
                if (string.IsNullOrWhiteSpace(alias) || alias.Length > 128 || alias.Any(char.IsControl))
                    throw new ToolException("configuration", "Target aliases must be nonblank, <=128 characters, without control characters.");
                var builder = new NpgsqlConnectionStringBuilder(connectionString);
                if (string.IsNullOrWhiteSpace(builder.Database) || string.IsNullOrWhiteSpace(builder.Host))
                    throw new ToolException("configuration", "Each target requires an explicit Host and Database.");
                if (builder.NoResetOnClose || builder.Multiplexing)
                    throw new ToolException("configuration", "No Reset On Close and Multiplexing are not supported.");
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Provider/parser messages can contain secrets; never echo connection-string input.
            throw new ToolException("configuration", "Invalid targets configuration. Check JSON, file access and Npgsql connection-string syntax.");
        }
        if (requireLock && allowedDatabases is null)
            throw new ToolException("configuration", "POSTGRES_REQUIRE_DATABASE_LOCK/--require-database-lock is set, but no POSTGRES_DATABASES/--databases database lock is configured.");
        string mode = Value("--access-mode", "ACCESS_MODE") ?? "unrestricted";
        if (mode is not ("restricted" or "unrestricted")) throw new ToolException("configuration", "Access mode must be restricted or unrestricted.");
        int poolSize = Number("POOL_SIZE", 8, 1, 32);
        if (poolSize * targets.Count > 256) throw new ToolException("configuration", "Targets × POSTGRES_POOL_SIZE must not exceed 256.");
        return new ServerOptions
        {
            Targets = new ReadOnlyDictionary<string, string>(targets), AllowedDatabases = allowedDatabases,
            Unrestricted = mode == "unrestricted",
            QueryTimeout = Number("QUERY_TIMEOUT", 30, 1, 600, "--query-timeout"),
            MaxRows = Number("MAX_ROWS", 1000, 1, 5000), MaxResultBytes = Number("MAX_RESULT_BYTES", 65536, 4096, 1048576),
            MaxCellChars = Number("MAX_CELL_CHARS", 4096, 1, 16384), PoolSize = poolSize,
            MaxConcurrentCalls = Number("MAX_CONCURRENT_CALLS", 16, 1, 64),
            LogLevel = Value("--log-level", "LOG_LEVEL") ?? "warning", Validate = validate
        };
    }
}

public sealed class ToolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
