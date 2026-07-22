using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Query;
using eQuantic.Linq.Expressions;
using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra.Migration;

/// <summary>
///     The Cassandra execution context handed to a <see cref="IMigrationBuilder.Run" /> escape hatch: it exposes
///     the <see cref="ISession" /> so a migration can run any CQL when the fluent operations are not enough.
/// </summary>
/// <param name="session">The session.</param>
public sealed class CassandraMigrationExecutionContext(ISession session) : IMigrationExecutionContext
{
    /// <summary>The session.</summary>
    public ISession Session { get; } = session;
}

/// <summary>Convenience access to the Cassandra session from the provider-agnostic context.</summary>
public static class CassandraMigrationExecutionContextExtensions
{
    /// <summary>Narrows the context to Cassandra (e.g. <c>ctx.AsCassandra().Session</c>).</summary>
    /// <param name="context">The provider-agnostic context.</param>
    public static CassandraMigrationExecutionContext AsCassandra(this IMigrationExecutionContext context) =>
        (CassandraMigrationExecutionContext)context;
}

/// <summary>
///     Applies provider-agnostic <see cref="MigrationOperation" />s to Cassandra as CQL DDL: it creates tables
///     (with the partition/clustering keys and column types from the model) and single-column secondary indexes,
///     runs keyed data updates, and hands the session to escape-hatch steps. Cassandra's rigid schema means
///     column type changes (and other document-store data mutations) are reported as not supported.
/// </summary>
public sealed class CassandraMigrationExecutor : IMigrationExecutor
{
    private readonly ISession _session;
    private readonly CassandraModel _model;
    private readonly CassandraMigrationExecutionContext _context;

    /// <summary>Initializes the executor.</summary>
    /// <param name="session">The session.</param>
    /// <param name="model">The Cassandra model.</param>
    public CassandraMigrationExecutor(ISession session, CassandraModel model)
    {
        _session = session;
        _model = model;
        _context = new CassandraMigrationExecutionContext(session);
    }

    /// <inheritdoc />
    public async Task ApplyAsync(IReadOnlyList<MigrationOperation> operations, CancellationToken cancellationToken = default)
    {
        foreach (var operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (operation)
            {
                case EnsureCollectionOperation ensure:
                {
                    var configuration = _model.For(ensure.EntityType);
                    await ExecuteAsync(CreateTable(configuration)).ConfigureAwait(false);

                    // The model's declared search indexes are part of the table's schema: LIKE pushdown needs them.
                    foreach (var search in configuration.SearchColumns)
                    {
                        await ExecuteAsync(CreateSearchIndex(configuration, search)).ConfigureAwait(false);
                    }

                    break;
                }
                case EnsureIndexOperation index:
                    await ExecuteAsync(CreateIndex(index)).ConfigureAwait(false);
                    break;
                case AddFieldOperation add:
                {
                    var configuration = _model.For(add.EntityType);
                    var member = add.Field.GetMemberName();
                    var column = configuration.Columns.FirstOrDefault(candidate =>
                                     CassandraEntityConfiguration.Same(candidate.Member, member))
                                 ?? throw new NotSupportedException(
                                     $"'{add.EntityType.Name}' has no mapped column '{member}' to add.");
                    await ExecuteAsync(new SimpleStatement(
                        $"ALTER TABLE {configuration.TableName} ADD {column.Name} {column.CqlType}")).ConfigureAwait(false);
                    break;
                }

                case DropFieldOperation drop:
                    await ExecuteAsync(new SimpleStatement(
                        $"ALTER TABLE {_model.For(drop.EntityType).TableName} DROP {drop.Field}")).ConfigureAwait(false);
                    break;
                case UpdateOperation update:
                    await ExecuteAsync(Update(update)).ConfigureAwait(false);
                    break;
                case RenameFieldOperation rename:
                {
                    var configuration = _model.For(rename.EntityType);
                    await ExecuteAsync(new SimpleStatement(
                        $"ALTER TABLE {configuration.TableName} RENAME {configuration.ColumnFor(rename.Field.GetMemberName())} TO {rename.NewName}")).ConfigureAwait(false);
                    break;
                }

                case ConvertFieldOperation:
                    throw new NotSupportedException(
                        "Cassandra cannot change a column's type; add a new column and backfill it with a Run step instead.");
                case RunOperation run:
                    await run.Action(_context, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported migration operation '{operation.GetType().Name}'.");
            }
        }
    }

    private Task ExecuteAsync(SimpleStatement statement) => _session.ExecuteAsync(statement);

    private static SimpleStatement CreateTable(CassandraEntityConfiguration configuration)
    {
        var columns = string.Join(", ", configuration.Columns.Select(column => $"{column.Name} {column.CqlType}"));
        var partition = "(" + string.Join(", ", configuration.PartitionKeys) + ")";
        var clustering = configuration.ClusteringKeys.Count > 0
            ? ", " + string.Join(", ", configuration.ClusteringKeys.Select(key => key.Column))
            : string.Empty;

        var cql = $"CREATE TABLE IF NOT EXISTS {configuration.TableName} ({columns}, PRIMARY KEY ({partition}{clustering}))";

        var with = new List<string>();
        if (configuration.ClusteringKeys.Count > 0)
        {
            with.Add("CLUSTERING ORDER BY ("
                     + string.Join(", ", configuration.ClusteringKeys.Select(key => $"{key.Column} {(key.Descending ? "DESC" : "ASC")}"))
                     + ")");
        }

        if (configuration.DefaultTtlSeconds is { } ttl)
        {
            with.Add($"default_time_to_live = {ttl}");
        }

        if (with.Count > 0)
        {
            cql += " WITH " + string.Join(" AND ", with);
        }

        return new SimpleStatement(cql);
    }

    private SimpleStatement CreateIndex(EnsureIndexOperation operation)
    {
        if (operation.Method != IndexMethod.Default)
        {
            throw new NotSupportedException(
                $"Cassandra has no '{operation.Method}' index structure; declare LIKE search with SearchIndex(...) on the model, " +
                "or use the cluster's native tooling via Run(...).");
        }

        if (operation.Filter is not null)
        {
            throw new NotSupportedException(
                "Cassandra has no filtered indexes; model the access pattern with the primary key instead.");
        }

        if (operation.ExpireAfter is not null)
        {
            throw new NotSupportedException(
                "Cassandra has no TTL indexes; write expiring rows with CommitAsync(o => o.WithTtl(...)), or set the " +
                "table's default_time_to_live via Run(...).");
        }

        if (operation.Keys.Count != 1)
        {
            throw new NotSupportedException(
                "Cassandra secondary indexes are single-column; a composite index is not supported (model the access pattern with the primary key or a Run step).");
        }

        var configuration = _model.For(operation.EntityType);
        return new SimpleStatement(
            $"CREATE INDEX IF NOT EXISTS ON {configuration.TableName} ({configuration.ColumnFor(operation.Keys[0].Selector.GetMemberName())})");
    }

    private static SimpleStatement CreateSearchIndex(CassandraEntityConfiguration configuration, CassandraSearchColumn search) =>
        new($"CREATE CUSTOM INDEX IF NOT EXISTS {configuration.TableName}_{search.Column}_search " +
            $"ON {configuration.TableName} ({search.Column}) " +
            "USING 'org.apache.cassandra.index.sasi.SASIIndex' " +
            $"WITH OPTIONS = {{'mode': '{(search.Mode == CassandraSearchMode.Contains ? "CONTAINS" : "PREFIX")}'}}");

    private SimpleStatement Update(UpdateOperation operation)
    {
        var configuration = _model.For(operation.EntityType);
        var (where, whereValues, allowFiltering) = CassandraCqlRenderer.Render(configuration, FilterInterpreter.Interpret(operation.Predicate));
        if (allowFiltering)
        {
            throw new NotSupportedException("A Cassandra migration Update requires the primary key; it cannot filter on non-key columns.");
        }

        var set = string.Join(", ", operation.Sets.Select(assignment => $"{configuration.ColumnFor(assignment.Field.GetMemberName())} = ?"));
        var values = operation.Sets.Select(assignment => assignment.Value).Concat(whereValues).ToArray();
        return new SimpleStatement($"UPDATE {configuration.TableName} SET {set} WHERE {where}", values);
    }
}
