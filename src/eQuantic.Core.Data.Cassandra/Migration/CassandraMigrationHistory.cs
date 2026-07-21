using eQuantic.Core.Data.Migration;
using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra.Migration;

/// <summary>
///     Tracks applied migrations in a dedicated Cassandra table (<c>schema_migrations</c> by default), keyed by the
///     migration's stable <see cref="MigrationAttribute.Id" />. Cassandra forbids a leading underscore in an
///     unquoted identifier, so the table cannot use the <c>_migrations</c> name other providers do.
/// </summary>
public sealed class CassandraMigrationHistory : IMigrationHistory
{
    /// <summary>The default history table name.</summary>
    public const string DefaultTableName = "schema_migrations";

    private readonly ISession _session;
    private readonly string _tableName;

    /// <summary>Initializes the history over a session.</summary>
    /// <param name="session">The session.</param>
    /// <param name="tableName">The history table name; defaults to <see cref="DefaultTableName" />.</param>
    public CassandraMigrationHistory(ISession session, string? tableName = null)
    {
        _session = session;
        _tableName = tableName ?? DefaultTableName;
    }

    /// <inheritdoc />
    public Task EnsureCreatedAsync(CancellationToken cancellationToken = default) =>
        _session.ExecuteAsync(new SimpleStatement(
            $"CREATE TABLE IF NOT EXISTS {_tableName} (id text PRIMARY KEY, title text, date timestamp, appliedat timestamp)"));

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<string>> GetAppliedIdsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _session.ExecuteAsync(new SimpleStatement($"SELECT id FROM {_tableName}")).ConfigureAwait(false);
        return rows.Select(row => row.GetValue<string>("id")).ToList();
    }

    /// <inheritdoc />
    public Task RecordAsync(AppliedMigration migration, CancellationToken cancellationToken = default) =>
        _session.ExecuteAsync(new SimpleStatement(
            $"INSERT INTO {_tableName} (id, title, date, appliedat) VALUES (?, ?, ?, ?)",
            migration.Id, migration.Title, migration.Date, migration.AppliedAt));
}
