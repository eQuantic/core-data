using Cassandra;
using eQuantic.Core.Data.Evolution;

namespace eQuantic.Core.Data.Cassandra.Evolution;

/// <summary>
///     Describes both sides of a drift check for Cassandra: the tables the model maps, as the model says they
///     should be, and those same tables as the cluster actually has them.
///     <para>
///         Cassandra is the only non-relational store this can be done for honestly, because it is the only one
///         that keeps a schema to read. <c>system_schema.columns</c> answers in CQL type names, which is the same
///         vocabulary the model already speaks — so the two sides compare directly, with no translation step to get
///         wrong.
///     </para>
/// </summary>
public sealed class CassandraDatabaseSnapshotSource(CassandraModel model, ISession session) : IDatabaseSnapshotSource
{
    /// <inheritdoc />
    public string Provider => "cassandra";

    /// <inheritdoc />
    public DatabaseSnapshot Expect() =>
        new(Provider, model.Configurations.Values
            .Select(configuration => new DatabaseCollection(
                configuration.TableName,
                configuration.EntityType.FullName ?? configuration.EntityType.Name,
                configuration.Columns
                    .Select(column => new DatabaseField(Folded(column.Name), Normalize(column.CqlType),
                        Nullable: true))
                    .ToList())
            {
                PartitionKeys = configuration.PartitionKeys.Select(Folded).ToList(),
            })
            .ToList());

    /// <inheritdoc />
    public async Task<DatabaseSnapshot> ObserveAsync(CancellationToken cancellationToken = default)
    {
        // kind and position are what make the partition key readable: it is an ordered subset of the columns.
        var statement = new SimpleStatement(
            "SELECT table_name, column_name, type, kind, position FROM system_schema.columns WHERE keyspace_name = ?",
            session.Keyspace);

        var rows = await session.ExecuteAsync(statement).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var byTable = new Dictionary<string, List<DatabaseField>>(StringComparer.OrdinalIgnoreCase);
        var partitions = new Dictionary<string, List<(int Position, string Column)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var table = row.GetValue<string>("table_name");
            var column = row.GetValue<string>("column_name");

            if (!byTable.TryGetValue(table, out var fields))
            {
                byTable[table] = fields = [];
            }

            // Every non-key column in Cassandra accepts no value — absence is how a row omits one, and there is
            // no NOT NULL to declare otherwise. Recording it uniformly keeps a healthy keyspace silent.
            fields.Add(new DatabaseField(column, Normalize(row.GetValue<string>("type")), Nullable: true));

            if (row.GetValue<string>("kind") == "partition_key")
            {
                if (!partitions.TryGetValue(table, out var keys))
                {
                    partitions[table] = keys = [];
                }

                keys.Add((row.GetValue<int>("position"), column));
            }
        }

        // Only the tables the model maps: a keyspace usually carries more than one application's.
        var collections = model.Configurations.Values
            .Where(configuration => byTable.ContainsKey(configuration.TableName))
            .Select(configuration => new DatabaseCollection(
                configuration.TableName,
                configuration.EntityType.FullName ?? configuration.EntityType.Name,
                byTable[configuration.TableName])
            {
                PartitionKeys = partitions.TryGetValue(configuration.TableName, out var keys)
                    ? keys.OrderBy(key => key.Position).Select(key => key.Column).ToList()
                    : [],
            })
            .ToList();

        return new DatabaseSnapshot(Provider, collections);
    }

    /// <summary>
    ///     The name the cluster actually holds. Cassandra folds every unquoted identifier to lower case, and the
    ///     provider never quotes one — so a model that says <c>OpenedAt</c> describes a column called
    ///     <c>openedat</c>, and comparing the two spellings would report every column of every correct table.
    /// </summary>
    private static string Folded(string identifier) => identifier.ToLowerInvariant();

    /// <summary>
    ///     Reduces a CQL type to one spelling. The cluster writes collection types with a space after the comma
    ///     and the model does not, which is the only disagreement worth flattening here.
    /// </summary>
    private static string Normalize(string cqlType) =>
        string.Concat(cqlType.Where(character => !char.IsWhiteSpace(character))).ToLowerInvariant();
}
