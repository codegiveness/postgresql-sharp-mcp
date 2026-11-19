// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Npgsql;

namespace PostgreSqlMcp.Core.Tests;

/// <summary>
/// <see cref="ServerOptions.Parse"/> reads process-wide POSTGRES_* variables, so every call goes through <see cref="Parse"/>,
/// which serializes access, starts from a clean slate and restores the previous values. All values here are synthetic.
/// </summary>
public sealed class ServerOptionsTests
{
    private const string Conn = "Host=db.example.test;Database=app;Username=reader;Password=synthetic-pw";
    private const string Secret = "synthetic-secret-value";

    private static readonly Lock EnvironmentLock = new();
    private static readonly string[] Variables =
    [
        "TARGETS", "TARGETS_FILE", "CONNECTION_STRING", "DATABASES", "REQUIRE_DATABASE_LOCK", "DATABASE_LOCK_MODE", "ACCESS_MODE",
        "POOL_SIZE", "QUERY_TIMEOUT", "MAX_ROWS", "MAX_RESULT_BYTES", "MAX_CELL_CHARS", "MAX_CONCURRENT_CALLS", "LOG_LEVEL"
    ];

    internal static ServerOptions Parse(string[] args, params (string Name, string Value)[] environment)
    {
        lock (EnvironmentLock)
        {
            Dictionary<string, string?> saved = Variables.ToDictionary(name => name, name => Environment.GetEnvironmentVariable("POSTGRES_" + name));
            try
            {
                foreach (string name in Variables) Environment.SetEnvironmentVariable("POSTGRES_" + name, null);
                foreach ((string name, string value) in environment) Environment.SetEnvironmentVariable("POSTGRES_" + name, value);
                return ServerOptions.Parse(args);
            }
            finally
            {
                foreach ((string name, string? value) in saved) Environment.SetEnvironmentVariable("POSTGRES_" + name, value);
            }
        }
    }

    private static string Rejected(string[] args, params (string Name, string Value)[] environment)
    {
        ToolException ex = Assert.Throws<ToolException>(() => Parse(args, environment));
        Assert.Equal("configuration", ex.Code);
        return ex.Message;
    }

    private static string RejectedEnv(params (string Name, string Value)[] environment) => Rejected([], environment);

    private static (string, string) Connection => ("CONNECTION_STRING", Conn);

    [Fact]
    public void Defaults_with_a_connection_string()
    {
        ServerOptions options = Parse([], Connection);

        string alias = Assert.Single(options.Targets).Key;
        Assert.Equal("primary", alias);
        Assert.Null(options.AllowedDatabases);
        Assert.False(options.StrictDatabaseLock);
        Assert.True(options.Unrestricted);
        Assert.Equal(30, options.QueryTimeout);
        Assert.Equal(1000, options.MaxRows);
        Assert.Equal(65536, options.MaxResultBytes);
        Assert.Equal(4096, options.MaxCellChars);
        Assert.Equal(8, options.PoolSize);
        Assert.Equal(16, options.MaxConcurrentCalls);
        Assert.Equal("warning", options.LogLevel);
        Assert.False(options.Validate);
    }

    [Fact]
    public void Connection_string_without_a_database_connects_to_postgres()
    {
        ServerOptions options = Parse([], ("CONNECTION_STRING", "Host=db.example.test;Username=reader"));

        Assert.Equal("postgres", new NpgsqlConnectionStringBuilder(options.Targets["primary"]).Database);
    }

    [Fact]
    public void Connection_string_without_a_host_is_rejected() =>
        Assert.Equal("Each target requires an explicit Host and Database.", RejectedEnv(("CONNECTION_STRING", "Database=app;Username=reader")));

    [Fact]
    public void Connection_string_may_come_from_the_command_line()
    {
        ServerOptions options = Parse(["--connection-string", Conn]);

        Assert.Equal("db.example.test", new NpgsqlConnectionStringBuilder(options.Targets["primary"]).Host);
    }

    [Fact]
    public void Flags_and_equals_form_values_are_accepted()
    {
        ServerOptions options = Parse(["--validate", "--query-timeout=45", "--log-level", "debug", "--access-mode=restricted"], Connection);

        Assert.True(options.Validate);
        Assert.Equal(45, options.QueryTimeout);
        Assert.Equal("debug", options.LogLevel);
        Assert.False(options.Unrestricted);
    }

    [Fact]
    public void Command_line_values_take_precedence_over_environment_values()
    {
        ServerOptions options = Parse(["--query-timeout=5", "--log-level=error", "--access-mode=restricted"],
            Connection, ("QUERY_TIMEOUT", "99"), ("LOG_LEVEL", "debug"), ("ACCESS_MODE", "unrestricted"));

        Assert.Equal(5, options.QueryTimeout);
        Assert.Equal("error", options.LogLevel);
        Assert.False(options.Unrestricted);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--bogus=1")]
    [InlineData("positional")]
    [InlineData("--VALIDATE")]
    [InlineData("--validate=true")]
    public void Unknown_options_are_rejected_without_echoing_them(string option)
    {
        string message = Rejected([option], Connection);

        Assert.Equal("Unknown option. Use --help for supported options.", message);
    }

    [Theory]
    [InlineData("--query-timeout")]
    [InlineData("--log-level")]
    [InlineData("--access-mode")]
    public void A_value_option_without_a_value_is_rejected(string option) =>
        Assert.Equal($"Missing value for {option}.", Rejected([option], Connection));

    [Fact]
    public void A_repeated_option_is_rejected_even_across_the_two_forms() =>
        Assert.Equal("Duplicate option: --log-level.", Rejected(["--log-level", "debug", "--log-level=info"], Connection));

    [Fact]
    public void Query_timeout_errors_name_the_command_line_option_when_it_supplied_the_value() =>
        Assert.Equal("--query-timeout must be 1..600.", Rejected(["--query-timeout", "0"], Connection, ("QUERY_TIMEOUT", "30")));

    [Fact]
    public void Query_timeout_errors_name_the_environment_variable_when_it_supplied_the_value() =>
        Assert.Equal("POSTGRES_QUERY_TIMEOUT must be 1..600.", RejectedEnv(Connection, ("QUERY_TIMEOUT", "601")));

    [Theory]
    [InlineData("POOL_SIZE", "1", "32")]
    [InlineData("QUERY_TIMEOUT", "1", "600")]
    [InlineData("MAX_ROWS", "1", "5000")]
    [InlineData("MAX_RESULT_BYTES", "4096", "1048576")]
    [InlineData("MAX_CELL_CHARS", "1", "16384")]
    [InlineData("MAX_CONCURRENT_CALLS", "1", "64")]
    public void Numeric_settings_accept_both_inclusive_bounds_and_reject_values_just_outside(string name, string min, string max)
    {
        Assert.NotNull(Parse([], Connection, (name, min)));
        Assert.NotNull(Parse([], Connection, (name, max)));

        int below = int.Parse(min, System.Globalization.CultureInfo.InvariantCulture) - 1;
        int above = int.Parse(max, System.Globalization.CultureInfo.InvariantCulture) + 1;
        string expected = $"POSTGRES_{name} must be {min}..{max}.";
        Assert.Equal(expected, RejectedEnv(Connection, (name, below.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        Assert.Equal(expected, RejectedEnv(Connection, (name, above.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData(" 5")]
    [InlineData("5 ")]
    [InlineData("1e2")]
    [InlineData("5.0")]
    [InlineData("99999999999")]
    public void Numeric_settings_accept_plain_digits_only(string value) =>
        Assert.Equal("POSTGRES_MAX_ROWS must be 1..5000.", RejectedEnv(Connection, ("MAX_ROWS", value)));

    [Fact]
    public void Numeric_settings_are_applied()
    {
        ServerOptions options = Parse([], Connection, ("POOL_SIZE", "4"), ("MAX_ROWS", "50"), ("MAX_RESULT_BYTES", "8192"),
            ("MAX_CELL_CHARS", "10"), ("MAX_CONCURRENT_CALLS", "2"), ("QUERY_TIMEOUT", "7"));

        Assert.Equal((4, 50, 8192, 10, 2, 7), (options.PoolSize, options.MaxRows, options.MaxResultBytes, options.MaxCellChars, options.MaxConcurrentCalls, options.QueryTimeout));
    }

    [Theory]
    [InlineData("restricted", false)]
    [InlineData("unrestricted", true)]
    public void Access_mode_values(string mode, bool unrestricted) =>
        Assert.Equal(unrestricted, Parse([], Connection, ("ACCESS_MODE", mode)).Unrestricted);

    [Theory]
    [InlineData("Restricted")]
    [InlineData("readonly")]
    public void Access_mode_is_an_exact_lowercase_enum(string mode) =>
        Assert.Equal("Access mode must be restricted or unrestricted.", RejectedEnv(Connection, ("ACCESS_MODE", mode)));

    [Theory]
    [InlineData("advisory", false)]
    [InlineData("strict", true)]
    public void Lock_mode_values(string mode, bool strict) =>
        Assert.Equal(strict, Parse([], Connection, ("DATABASES", """["app"]"""), ("DATABASE_LOCK_MODE", mode)).StrictDatabaseLock);

    [Fact]
    public void Lock_mode_rejects_other_values() =>
        Assert.Equal("POSTGRES_DATABASE_LOCK_MODE/--database-lock-mode must be advisory or strict.",
            RejectedEnv(Connection, ("DATABASE_LOCK_MODE", "Strict")));

    [Fact]
    public void Strict_lock_mode_without_a_lock_is_rejected() =>
        Assert.Contains("is strict, but no POSTGRES_DATABASES/--databases database lock is configured",
            Rejected(["--database-lock-mode=strict"], Connection), StringComparison.Ordinal);

    [Fact]
    public void Required_lock_without_a_lock_is_rejected_from_flag_or_environment()
    {
        const string expected = "POSTGRES_REQUIRE_DATABASE_LOCK/--require-database-lock is set, but no POSTGRES_DATABASES/--databases database lock is configured.";
        Assert.Equal(expected, Rejected(["--require-database-lock"], Connection));
        Assert.Equal(expected, RejectedEnv(Connection, ("REQUIRE_DATABASE_LOCK", "true")));
        Assert.NotNull(Parse([], Connection, ("REQUIRE_DATABASE_LOCK", "false")));
        Assert.NotNull(Parse(["--require-database-lock"], Connection, ("DATABASES", """["app"]""")));
    }

    [Fact]
    public void Required_lock_variable_must_be_a_boolean() =>
        Assert.Equal("POSTGRES_REQUIRE_DATABASE_LOCK must be true or false.", RejectedEnv(Connection, ("REQUIRE_DATABASE_LOCK", "yes")));

    [Fact]
    public void Lock_aliases_come_from_the_connection_string_in_configured_order()
    {
        ServerOptions options = Parse([], Connection, ("DATABASES", """["zeta","alpha","mid"]"""));

        Assert.Equal(["zeta", "alpha", "mid"], options.AllowedDatabases);
        Assert.Equal(["alpha", "mid", "zeta"], options.Targets.Keys.Order(StringComparer.Ordinal));
        foreach ((string alias, string connectionString) in options.Targets)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            Assert.Equal(alias, builder.Database);
            Assert.Equal("db.example.test", builder.Host);
        }
    }

    [Fact]
    public void Lock_aliases_share_one_profile_so_many_databases_do_not_count_against_pool_math()
    {
        string names = JsonSerializer.Serialize(Enumerable.Range(0, 40).Select(i => $"db{i}"));

        ServerOptions options = Parse([], Connection, ("DATABASES", names), ("POOL_SIZE", "32"));

        Assert.Equal(40, options.Targets.Count);
    }

    [Fact]
    public void Databases_option_overrides_the_environment_variable() =>
        Assert.Equal(["cli"], Parse(["--databases", """["cli"]"""], Connection, ("DATABASES", """["env"]""")).AllowedDatabases);

    [Fact]
    public void Lock_may_be_combined_with_targets_json()
    {
        ServerOptions options = Parse([], ("TARGETS", $$"""{"one":"{{Conn}}"}"""), ("DATABASES", """["app","other"]"""));

        Assert.Equal(["app", "other"], options.AllowedDatabases);
        Assert.Equal(["one"], options.Targets.Keys);
    }

    [Theory]
    [InlineData("""[]""", "POSTGRES_DATABASES must list at least one database.")]
    [InlineData("""[""]""", "POSTGRES_DATABASES entries must be database names of 1..63 UTF-8 bytes without NUL.")]
    [InlineData("""[null]""", "POSTGRES_DATABASES entries must be database names of 1..63 UTF-8 bytes without NUL.")]
    [InlineData("""["a\u0000b"]""", "POSTGRES_DATABASES entries must be database names of 1..63 UTF-8 bytes without NUL.")]
    [InlineData("""["a","a"]""", "Duplicate database name in POSTGRES_DATABASES.")]
    [InlineData("""not json""", "POSTGRES_DATABASES/--databases must be a JSON array of database names, for example [\"app_db\"].")]
    [InlineData("""{"a":1}""", "POSTGRES_DATABASES/--databases must be a JSON array of database names, for example [\"app_db\"].")]
    [InlineData("""[1]""", "POSTGRES_DATABASES/--databases must be a JSON array of database names, for example [\"app_db\"].")]
    [InlineData("\"app\"", "POSTGRES_DATABASES/--databases must be a JSON array of database names, for example [\"app_db\"].")]
    public void Database_lock_list_is_validated(string json, string expected) =>
        Assert.Equal(expected, RejectedEnv(Connection, ("DATABASES", json)));

    [Fact]
    public void Database_names_are_limited_to_63_utf8_bytes_not_characters()
    {
        string sixtyThree = new('a', 63);
        string multibyteAtLimit = new string('é', 31) + "a";
        string multibyteOver = new('é', 32);

        Assert.Equal([sixtyThree], Parse([], Connection, ("DATABASES", JsonSerializer.Serialize(new[] { sixtyThree }))).AllowedDatabases);
        Assert.Equal([multibyteAtLimit], Parse([], Connection, ("DATABASES", JsonSerializer.Serialize(new[] { multibyteAtLimit }))).AllowedDatabases);
        Assert.Contains("1..63 UTF-8 bytes", RejectedEnv(Connection, ("DATABASES", JsonSerializer.Serialize(new[] { sixtyThree + "a" }))), StringComparison.Ordinal);
        Assert.Contains("1..63 UTF-8 bytes", RejectedEnv(Connection, ("DATABASES", JsonSerializer.Serialize(new[] { multibyteOver }))), StringComparison.Ordinal);
    }

    [Fact]
    public void No_target_source_is_rejected() =>
        Assert.Equal("Set POSTGRES_CONNECTION_STRING, POSTGRES_TARGETS, or POSTGRES_TARGETS_FILE.", RejectedEnv());

    [Fact]
    public void Targets_json_and_a_connection_string_are_mutually_exclusive()
    {
        const string expected = "Use targets JSON/file OR a connection string, not both.";
        Assert.Equal(expected, RejectedEnv(("TARGETS", $$"""{"a":"{{Conn}}"}"""), Connection));
        Assert.Equal(expected, RejectedEnv(("TARGETS_FILE", "ignored.json"), Connection));
        Assert.Equal(expected, Rejected(["--targets-file", "ignored.json"], Connection));
        Assert.Equal(expected, Rejected(["--connection-string", Conn], ("TARGETS", """{}""")));
    }

    [Fact]
    public void Targets_json_defines_one_profile_per_alias_and_applies_the_tls_default()
    {
        ServerOptions options = Parse([], ("TARGETS", $$"""{"a":"{{Conn}}","b":"Host=other.example.test;Database=x;SSL Mode=Disable"}"""));

        Assert.Equal(["a", "b"], options.Targets.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(SslMode.Prefer, new NpgsqlConnectionStringBuilder(options.Targets["a"]).SslMode);
        Assert.Equal(SslMode.Disable, new NpgsqlConnectionStringBuilder(options.Targets["b"]).SslMode);
    }

    [Fact]
    public void Root_certificate_without_ssl_mode_selects_verify_full_in_the_parsed_target()
    {
        ServerOptions options = Parse([], ("CONNECTION_STRING", Conn + ";Root Certificate=/etc/ssl/synthetic-ca.pem"));

        Assert.Equal(SslMode.VerifyFull, new NpgsqlConnectionStringBuilder(options.Targets["primary"]).SslMode);
    }

    [Fact]
    public void Targets_file_is_read_and_environment_json_wins_over_it()
    {
        string path = Path.Combine(Path.GetTempPath(), $"postgresql-sharp-mcp-tests-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""{"fromfile":"{{Conn}}"}""");
        try
        {
            Assert.Equal(["fromfile"], Parse(["--targets-file", path]).Targets.Keys);
            Assert.Equal(["fromfile"], Parse([], ("TARGETS_FILE", path)).Targets.Keys);
            Assert.Equal(["fromenv"], Parse([], ("TARGETS_FILE", path), ("TARGETS", $$"""{"fromenv":"{{Conn}}"}""")).Targets.Keys);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Unreadable_targets_file_error_names_the_setting_but_not_the_path()
    {
        string path = Path.Combine(Path.GetTempPath(), $"synthetic-private-name-{Guid.NewGuid():N}.json");

        string message = Rejected(["--targets-file", path]);

        Assert.StartsWith("Cannot read the targets file named by POSTGRES_TARGETS_FILE/--targets-file", message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-private-name", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("\"text\"")]
    [InlineData("""1""")]
    public void Targets_json_must_be_an_object(string json) =>
        Assert.Equal("Targets must be a JSON object of alias: connection-string pairs.", RejectedEnv(("TARGETS", json)));

    [Fact]
    public void Empty_targets_object_has_no_profiles() =>
        Assert.Equal("Configure 1..32 connection profiles in targets JSON/file.", RejectedEnv(("TARGETS", "{}")));

    [Fact]
    public void Targets_json_accepts_up_to_32_profiles_and_rejects_33()
    {
        static string Targets(int count) => JsonSerializer.Serialize(Enumerable.Range(0, count).ToDictionary(i => $"p{i}", _ => Conn));

        Assert.Equal(32, Parse([], ("TARGETS", Targets(32))).Targets.Count);
        Assert.Equal("Configure 1..32 connection profiles in targets JSON/file.", RejectedEnv(("TARGETS", Targets(33))));
    }

    [Fact]
    public void Pool_size_times_profile_count_may_not_exceed_256()
    {
        static string Targets(int count) => JsonSerializer.Serialize(Enumerable.Range(0, count).ToDictionary(i => $"p{i}", _ => Conn));
        const string expected = "Connection profiles × POSTGRES_POOL_SIZE must not exceed 256.";

        Assert.NotNull(Parse([], ("TARGETS", Targets(8)), ("POOL_SIZE", "32")));
        Assert.Equal(expected, RejectedEnv(("TARGETS", Targets(9)), ("POOL_SIZE", "32")));
        Assert.NotNull(Parse([], ("TARGETS", Targets(32))));
        Assert.Equal(expected, RejectedEnv(("TARGETS", Targets(32)), ("POOL_SIZE", "9")));
    }

    [Fact]
    public void Duplicate_target_aliases_are_rejected() =>
        Assert.Equal("Duplicate target alias.", RejectedEnv(("TARGETS", $$"""{"a":"{{Conn}}","a":"{{Conn}}"}""")));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\\u0001b")]
    [InlineData("tab\\there")]
    public void Alias_must_be_nonblank_without_control_characters(string alias) =>
        Assert.Equal("Target aliases must be nonblank, <=128 characters, without control characters.",
            RejectedEnv(("TARGETS", $$"""{"{{alias}}":"{{Conn}}"}""")));

    [Fact]
    public void Alias_length_limit_is_128_characters()
    {
        Assert.Single(Parse([], ("TARGETS", $$"""{"{{new string('a', 128)}}":"{{Conn}}"}""")).Targets);
        Assert.Contains("<=128 characters", RejectedEnv(("TARGETS", $$"""{"{{new string('a', 129)}}":"{{Conn}}"}""")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"a":"Host=h.example.test"}""", "Each target requires an explicit Host and Database.")]
    [InlineData("""{"a":"Database=d"}""", "Each target requires an explicit Host and Database.")]
    [InlineData("""{"a":null}""", "Each target requires an explicit Host and Database.")]
    [InlineData("""{"a":"Host=h.example.test;Database=d;No Reset On Close=true"}""", "No Reset On Close and Multiplexing are not supported.")]
    [InlineData("""{"a":"Host=h.example.test;Database=d;Multiplexing=true"}""", "No Reset On Close and Multiplexing are not supported.")]
    [InlineData("""{"a":1}""", "Invalid targets configuration. Check JSON, file access and Npgsql connection-string syntax.")]
    [InlineData("""{"a":""", "Invalid targets configuration. Check JSON, file access and Npgsql connection-string syntax.")]
    [InlineData("""not json""", "Invalid targets configuration. Check JSON, file access and Npgsql connection-string syntax.")]
    public void Each_profile_is_validated(string json, string expected) => Assert.Equal(expected, RejectedEnv(("TARGETS", json)));

    [Theory]
    [InlineData("postgres://reader:synthetic-secret-value@db.example.test/app")]
    [InlineData("postgresql://reader:synthetic-secret-value@db.example.test/app")]
    [InlineData("  POSTGRES://reader:synthetic-secret-value@db.example.test/app")]
    public void Url_connection_strings_are_refused_without_echoing_them(string connectionString)
    {
        string fromEnvironment = RejectedEnv(("CONNECTION_STRING", connectionString));
        string fromTargets = RejectedEnv(("TARGETS", JsonSerializer.Serialize(new Dictionary<string, string> { ["a"] = connectionString })));

        Assert.Contains("is a postgres:// URL, which is not supported", fromEnvironment, StringComparison.Ordinal);
        Assert.StartsWith("Targets profile values must be Npgsql key=value connection strings, not postgres:// URLs", fromTargets, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, fromEnvironment + fromTargets, StringComparison.Ordinal);
        Assert.DoesNotContain("password=", (fromEnvironment + fromTargets).ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Host=h.example.test;Database=d;Bogus Keyword=synthetic-secret-value")]
    [InlineData("this is not a connection string; Password=synthetic-secret-value")]
    [InlineData("Host=h.example.test;Database=d;Port=synthetic-secret-value")]
    public void Malformed_connection_strings_never_leak_their_content(string malformed)
    {
        string fromEnvironment = RejectedEnv(("CONNECTION_STRING", malformed));
        string fromTargets = RejectedEnv(("TARGETS", JsonSerializer.Serialize(new Dictionary<string, string> { ["a"] = malformed })));

        Assert.Contains("(its value is not shown)", fromEnvironment, StringComparison.Ordinal);
        Assert.Equal("Invalid targets configuration. Check JSON, file access and Npgsql connection-string syntax.", fromTargets);
        Assert.DoesNotContain(Secret, fromEnvironment + fromTargets, StringComparison.Ordinal);
    }
}
