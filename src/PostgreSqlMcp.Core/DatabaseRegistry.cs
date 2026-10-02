using Npgsql;

namespace PostgreSqlMcp.Core;

public sealed class DatabaseRegistry : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, Lazy<NpgsqlDataSource>> _sources;
    public string[] TargetNames { get; }

    public DatabaseRegistry(ServerOptions options)
    {
        TargetNames = options.Targets.Keys.Order(StringComparer.Ordinal).ToArray();
        var sources = new Dictionary<string, Lazy<NpgsqlDataSource>>(StringComparer.Ordinal);
        var pools = new Dictionary<string, Lazy<NpgsqlDataSource>>(StringComparer.Ordinal);
        foreach (var pair in options.Targets)
        {
            var builder = new NpgsqlConnectionStringBuilder(pair.Value)
            {
                Pooling = true, MinPoolSize = 0, MaxPoolSize = options.PoolSize,
                ConnectionIdleLifetime = 60, ConnectionPruningInterval = 10, ConnectionLifetime = 1800,
                Timeout = Math.Min(options.QueryTimeout, 15), CommandTimeout = options.QueryTimeout,
                NoResetOnClose = false, Multiplexing = false, Enlist = false, IncludeErrorDetail = false,
                LogParameters = false, ApplicationName = "postgresql-sharp-mcp"
            };
            string connectionString = builder.ConnectionString;
            if (!pools.TryGetValue(connectionString, out var source))
            {
                source = new Lazy<NpgsqlDataSource>(() => NpgsqlDataSource.Create(connectionString),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                pools.Add(connectionString, source);
            }
            sources.Add(pair.Key, source);
        }
        _sources = sources;
    }

    public NpgsqlDataSource Get(string database)
    {
        if (string.IsNullOrWhiteSpace(database) || !_sources.TryGetValue(database, out var source))
            throw new ToolException("invalid_target", "Specify an exact configured target from list_databases. No fallback is used.");
        return source.Value;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var source in _sources.Values.Distinct())
            if (source.IsValueCreated) await source.Value.DisposeAsync().ConfigureAwait(false);
    }
}
