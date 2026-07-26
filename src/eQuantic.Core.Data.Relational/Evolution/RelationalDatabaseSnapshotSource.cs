using System.Data.Common;
using eQuantic.Core.Data.Evolution;

namespace eQuantic.Core.Data.Relational.Evolution;

/// <summary>
///     Describes both sides of a drift check for a relational store: the tables the model maps, as the model says
///     they should be, and those same tables as the database actually has them.
///     <para>
///         Both descriptions run every type through <see cref="SqlDialect.NormalizeStoredType" />, so a difference
///         between them is a difference in the database and not in how two catalogues spell the same thing. That
///         is the whole reason one class produces both: a check that cries wolf is a check nobody runs.
///     </para>
/// </summary>
public sealed class RelationalDatabaseSnapshotSource : IDatabaseSnapshotSource
{
    private readonly DbDataSource _dataSource;
    private readonly SqlDialect _dialect;
    private readonly RelationalModel _model;

    /// <summary>Initializes the source over the registered model and data source.</summary>
    /// <param name="model">The relational model.</param>
    /// <param name="dialect">The dialect, which knows how to read its own catalogue.</param>
    /// <param name="dataSource">The connection to the database being checked.</param>
    public RelationalDatabaseSnapshotSource(RelationalModel model, SqlDialect dialect, DbDataSource dataSource)
    {
        _model = model;
        _dialect = dialect;
        _dataSource = dataSource;
        Provider = dialect.System;
    }

    /// <inheritdoc />
    public string Provider { get; }

    /// <inheritdoc />
    public DatabaseSnapshot Expect() =>
        new(Provider, _model.Configurations.Values
            .Select(configuration => new DatabaseCollection(
                configuration.TableName,
                configuration.EntityType.FullName ?? configuration.EntityType.Name,
                configuration.Columns.Select(column => Expect(configuration, column)).ToList()))
            .ToList());

    /// <inheritdoc />
    public async Task<DatabaseSnapshot> ObserveAsync(CancellationToken cancellationToken = default)
    {
        var sql = _dialect.IntrospectColumnsSql
            ?? throw new NotSupportedException(
                $"The '{Provider}' dialect does not read its own catalogue, so there is nothing to compare the " +
                "model against. Drift cannot be reported for it — which is not the same as there being none.");

        var columns = await ReadAsync(sql, cancellationToken).ConfigureAwait(false);

        // Only the tables the model maps: the rest of the database belongs to somebody else.
        var collections = new List<DatabaseCollection>();
        foreach (var configuration in _model.Configurations.Values)
        {
            if (!columns.TryGetValue(configuration.TableName, out var found))
            {
                continue;
            }

            collections.Add(new DatabaseCollection(configuration.TableName,
                configuration.EntityType.FullName ?? configuration.EntityType.Name, found));
        }

        return new DatabaseSnapshot(Provider, collections);
    }

    private DatabaseField Expect(RelationalEntityConfiguration configuration, RelationalColumn column) =>
        new(column.Name,
            _dialect.NormalizeStoredType(_dialect.SqlType(column)),
            // What the engine would create, not what the CLR type implies. Its CREATE TABLE writes no NOT NULL
            // for ordinary columns — only the primary key is required, and that by the store's own rule — so a
            // healthy database has every other column nullable. Expecting otherwise would report a finding for
            // every non-nullable member of every correct table, which is how a check gets ignored.
            !configuration.Keys.Any(key => key.Property.Name == column.Property.Name));

    private async Task<Dictionary<string, List<DatabaseField>>> ReadAsync(string sql,
        CancellationToken cancellationToken)
    {
        var byTable = new Dictionary<string, List<DatabaseField>>(StringComparer.Ordinal);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var table = reader.GetString(0);
            if (!byTable.TryGetValue(table, out var fields))
            {
                byTable[table] = fields = [];
            }

            fields.Add(new DatabaseField(
                reader.GetString(1),
                _dialect.NormalizeStoredType(reader.GetString(2)),
                reader.GetBoolean(3)));
        }

        return byTable;
    }
}
