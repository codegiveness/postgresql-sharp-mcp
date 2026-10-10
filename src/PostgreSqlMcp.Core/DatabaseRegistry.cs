using System.Text;
using Npgsql;

namespace PostgreSqlMcp.Core;

public sealed class DatabaseRegistry : IAsyncDisposable
{
    private readonly Dictionary<string, Profile> _profiles = new(StringComparer.Ordinal);
    private readonly Dictionary<DatabaseSelection, SourceEntry> _sources = new();
    private readonly SemaphoreSlim _gate = new(1);
    private TaskCompletionSource? _changed;
    private readonly HashSet<string>? _allowedDatabases;
    private readonly int _capacity;
    private long _clock;
    private bool _disposed;
    public string[] TargetNames { get; }
    public string DefaultTarget { get; }
    /// <summary>Database lock in configured order, or null when no lock is configured.</summary>
    public string[]? AllowedDatabaseNames { get; }

    public DatabaseRegistry(ServerOptions options)
    {
        TargetNames = options.Targets.Keys.Order(StringComparer.Ordinal).ToArray();
        DefaultTarget = options.Targets.ContainsKey("primary") ? "primary" : TargetNames[0];
        AllowedDatabaseNames = options.AllowedDatabases?.ToArray();
        _allowedDatabases = AllowedDatabaseNames is null ? null : new(AllowedDatabaseNames, StringComparer.Ordinal);
        _capacity = 256 / options.PoolSize;
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
            string database = builder.Database!;
            builder.Database = "";
            // Keyword order is not connection identity; normalize once per profile.
            string[] keywords = builder.Keys.Cast<string>().Order(StringComparer.Ordinal).ToArray();
            var normalized = new NpgsqlConnectionStringBuilder();
            foreach (string keyword in keywords) normalized[keyword] = builder[keyword];
            _profiles.Add(pair.Key, new(normalized.ConnectionString, database));
        }
    }

    internal static bool IsDatabaseName(string name) =>
        name.Length > 0 && !name.Contains('\0') && Encoding.UTF8.GetByteCount(name) <= 63;

    internal bool Allows(string database) => _allowedDatabases is null || _allowedDatabases.Contains(database);

    /// <summary>
    /// Database a profile connects to for live listing, validation and lock checks: its bootstrap database,
    /// or the first locked database when the bootstrap database is outside the lock.
    /// </summary>
    public string GetDiscoveryDatabase(string target)
    {
        if (!_profiles.TryGetValue(target, out var profile))
            throw new ToolException("invalid_target", "Specify an exact configured connection profile as target.");
        return Allows(profile.Database) ? profile.Database : AllowedDatabaseNames![0];
    }

    /// <summary>One target per distinct login/server profile (bootstrap database excluded), in ordinal target order.</summary>
    internal IEnumerable<(string Target, string Login)> LoginTargets()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string target in TargetNames)
            if (seen.Add(_profiles[target].ConnectionString))
                yield return (target, _profiles[target].ConnectionString);
    }

    internal DatabaseSelection Resolve(string database, string? target)
    {
        if (string.IsNullOrEmpty(database))
            throw new ToolException("invalid_target", "A database name or configured profile alias is required.");
        Profile profile;
        if (target is not null)
        {
            if (!_profiles.TryGetValue(target, out profile!))
                throw new ToolException("invalid_target", "Specify an exact configured connection profile as target.");
        }
        else if (_profiles.TryGetValue(database, out profile!))
            database = profile.Database;
        else
            profile = _profiles[DefaultTarget];
        if (!IsDatabaseName(database))
            throw new ToolException("invalid_target", "Specify a database name of 1..63 UTF-8 bytes without NUL, or a configured profile alias.");
        RequireAllowed(database);
        return new(profile.ConnectionString, database);
    }

    private void RequireAllowed(string database)
    {
        if (!Allows(database))
            throw new ToolException("invalid_target", "The database is outside this server's configured database lock (POSTGRES_DATABASES).");
    }

    internal async ValueTask<SourceEntry> AcquireAsync(DatabaseSelection selection, CancellationToken ct)
    {
        // Every data source is created below; re-check so no caller can open a pool outside the lock.
        RequireAllowed(selection.Database);
        while (true)
        {
            Task changed;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_sources.TryGetValue(selection, out var existing))
                {
                    existing.Users++;
                    existing.LastUsed = ++_clock;
                    return existing;
                }
                SourceEntry? victim = null;
                if (_sources.Count == _capacity)
                {
                    foreach (var entry in _sources.Values)
                        if (entry.Users == 0 && (victim is null || entry.LastUsed < victim.LastUsed))
                            victim = entry;
                    if (victim is not null)
                    {
                        // Keep the slot reserved until the old pool has actually closed.
                        await victim.Source.DisposeAsync().ConfigureAwait(false);
                        _sources.Remove(victim.Selection);
                    }
                }
                if (_sources.Count < _capacity)
                {
                    var builder = new NpgsqlConnectionStringBuilder(selection.ConnectionString) { Database = selection.Database };
                    var entry = new SourceEntry(selection, NpgsqlDataSource.Create(builder.ConnectionString), ++_clock);
                    _sources.Add(selection, entry);
                    return entry;
                }
                changed = (_changed ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            finally { _gate.Release(); }
            // All cached pools are in use. Wait within the caller's original deadline.
            await changed.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    internal async ValueTask ReleaseAsync(SourceEntry entry)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            entry.Users--;
            entry.LastUsed = ++_clock;
            if (entry.Users == 0) SignalChange();
        }
        finally { _gate.Release(); }
    }

    private void SignalChange()
    {
        TaskCompletionSource? changed = _changed;
        _changed = null;
        changed?.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        while (true)
        {
            Task changed;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_disposed)
                {
                    _disposed = true;
                    SignalChange();
                }
                bool busy = false;
                foreach (var entry in _sources.Values)
                    busy |= entry.Users != 0;
                if (!busy)
                {
                    foreach (var entry in _sources.Values)
                        await entry.Source.DisposeAsync().ConfigureAwait(false);
                    _sources.Clear();
                    return;
                }
                changed = (_changed ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            finally { _gate.Release(); }
            await changed.ConfigureAwait(false);
        }
    }

    private sealed record Profile(string ConnectionString, string Database);
    internal readonly record struct DatabaseSelection(string ConnectionString, string Database);
    internal sealed class SourceEntry(DatabaseSelection selection, NpgsqlDataSource source, long lastUsed)
    {
        internal DatabaseSelection Selection { get; } = selection;
        internal NpgsqlDataSource Source { get; } = source;
        internal int Users { get; set; } = 1;
        internal long LastUsed { get; set; } = lastUsed;
    }
}
