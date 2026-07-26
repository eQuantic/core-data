using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Query;
using eQuantic.Linq.Expressions;

namespace eQuantic.Core.Data.Relational.Migration;

/// <summary>
///     The relational execution context handed to a <see cref="IMigrationBuilder.Run" /> escape hatch: it exposes
///     the open <see cref="DbConnection" /> so a migration can run any SQL when the fluent operations are not enough.
/// </summary>
/// <param name="connection">The open connection.</param>
public sealed class RelationalMigrationExecutionContext(DbConnection connection) : IMigrationExecutionContext
{
    /// <summary>The open connection.</summary>
    public DbConnection Connection { get; } = connection;
}

/// <summary>Convenience access to the connection from the provider-agnostic context.</summary>
public static class RelationalMigrationExecutionContextExtensions
{
    /// <summary>Narrows the context to the relational engine (e.g. <c>ctx.AsRelational().Connection</c>).</summary>
    /// <param name="context">The provider-agnostic context.</param>
    public static RelationalMigrationExecutionContext AsRelational(this IMigrationExecutionContext context) =>
        (RelationalMigrationExecutionContext)context;
}

/// <summary>
///     Applies provider-agnostic <see cref="MigrationOperation" />s as SQL DDL/DML: <c>CREATE TABLE</c> from the
///     model (column types from the dialect, generated keys declared), single- and multi-column indexes, keyed
///     data updates, column renames and type conversions — and hands the connection to escape-hatch steps.
/// </summary>
public sealed class RelationalMigrationExecutor : IMigrationExecutor
{
    private readonly DbDataSource _dataSource;
    private readonly SqlDialect _dialect;
    private readonly RelationalModel _model;

    /// <summary>Initializes the executor.</summary>
    public RelationalMigrationExecutor(DbDataSource dataSource, SqlDialect dialect, RelationalModel model)
    {
        _dataSource = dataSource;
        _dialect = dialect;
        _model = model;
    }

    /// <inheritdoc />
    public async Task ApplyAsync(IReadOnlyList<MigrationOperation> operations, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var context = new RelationalMigrationExecutionContext(connection);

        foreach (var operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (operation)
            {
                case EnsureCollectionOperation ensure:
                {
                    var configuration = _model.For(ensure.EntityType);
                    await ExecuteAsync(connection, CreateTable(configuration), [], cancellationToken).ConfigureAwait(false);

                    // The model's declared search indexes are part of the table's schema where the dialect can
                    // materialize them (PostgreSQL: GIN trigram); elsewhere the declaration is a no-op by design.
                    foreach (var search in configuration.SearchColumns)
                    {
                        foreach (var sql in _dialect.SearchIndexSql(
                                     $"ix_{configuration.TableName}_{search.Column.Name}_search",
                                     _dialect.Quote(configuration.TableName), _dialect.Quote(search.Column.Name)))
                        {
                            await ExecuteAsync(connection, sql, [], cancellationToken).ConfigureAwait(false);
                        }
                    }

                    // The ordered-read declaration materializes as one multi-column index with the declared directions.
                    if (configuration.ClusteringColumns.Count > 0)
                    {
                        var list = string.Join(", ", configuration.ClusteringColumns.Select(clustering =>
                            $"{_dialect.Quote(clustering.Column.Name)}{(clustering.Descending ? " DESC" : string.Empty)}"));
                        await ExecuteAsync(connection,
                            _dialect.CreateIndexSql(_dialect.Quote($"ix_{configuration.TableName}_clustering"),
                                _dialect.Quote(configuration.TableName), list, unique: false),
                            [], cancellationToken).ConfigureAwait(false);
                    }

                    break;
                }

                case EnsureIndexOperation index:
                    await ExecuteAsync(connection, CreateIndex(index), [], cancellationToken).ConfigureAwait(false);
                    break;
                case UpdateOperation update:
                {
                    var (sql, parameters) = Update(update);
                    await ExecuteAsync(connection, sql, parameters, cancellationToken).ConfigureAwait(false);
                    break;
                }

                case RenameFieldOperation rename:
                {
                    var configuration = _model.For(rename.EntityType);
                    // A stated pair is already in stored form and goes through verbatim. A selector has to be
                    // resolved against the model, and its target then takes the naming convention the way a
                    // hand-written rename expects.
                    var (from, to) = rename.CurrentName is { } stated
                        ? (stated, rename.NewName)
                        : (Column(configuration, rename.Field!.GetMemberName()).Name, _dialect.ColumnName(rename.NewName));
                    await ExecuteAsync(connection,
                        $"ALTER TABLE {_dialect.Quote(configuration.TableName)} RENAME COLUMN {_dialect.Quote(from)} " +
                        $"TO {_dialect.Quote(to)}", [], cancellationToken).ConfigureAwait(false);
                    break;
                }

                case AddFieldOperation add:
                {
                    var configuration = _model.For(add.EntityType);
                    var column = Column(configuration, add.Field.GetMemberName());
                    await ExecuteAsync(connection,
                        _dialect.AddColumnSql(_dialect.Quote(configuration.TableName), _dialect.Quote(column.Name),
                            _dialect.SqlType(column.StoredType)), [], cancellationToken).ConfigureAwait(false);
                    break;
                }

                case DropFieldOperation drop:
                {
                    // The stored name comes as a string by design: the CLR member is usually already gone.
                    var configuration = _model.For(drop.EntityType);
                    await ExecuteAsync(connection,
                        _dialect.DropColumnSql(_dialect.Quote(configuration.TableName), _dialect.Quote(drop.Field)),
                        [], cancellationToken).ConfigureAwait(false);
                    break;
                }

                case ConvertFieldOperation convert:
                {
                    var configuration = _model.For(convert.EntityType);
                    var column = Column(configuration, convert.Field.GetMemberName());
                    await ExecuteAsync(connection,
                        _dialect.AlterColumnType(_dialect.Quote(configuration.TableName), _dialect.Quote(column.Name), SqlTypeOf(convert.To)),
                        [], cancellationToken).ConfigureAwait(false);
                    break;
                }

                case ResizeFieldOperation resize:
                {
                    // The size lives in the model, so the column is restated to whatever it now declares —
                    // the same statement a conversion uses, with the type read from the mapping instead of
                    // named by the caller.
                    var configuration = _model.For(resize.EntityType);
                    var column = Column(configuration, resize.Field.GetMemberName());
                    await ExecuteAsync(connection,
                        _dialect.AlterColumnType(_dialect.Quote(configuration.TableName),
                            _dialect.Quote(column.Name), _dialect.SqlType(column)),
                        [], cancellationToken).ConfigureAwait(false);
                    break;
                }

                case RenameCollectionOperation move:
                    await ExecuteAsync(connection,
                        _dialect.RenameTableSql(_dialect.Quote(move.CurrentName), move.NewName),
                        [], cancellationToken).ConfigureAwait(false);
                    break;

                case DropCollectionOperation discard:
                    await ExecuteAsync(connection, _dialect.DropTableSql(_dialect.Quote(discard.Name)),
                        [], cancellationToken).ConfigureAwait(false);
                    break;

                case RunOperation run:
                    await run.Action(context, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported migration operation '{operation.GetType().Name}'.");
            }
        }
    }

    private string CreateTable(RelationalEntityConfiguration configuration)
    {
        var columns = configuration.Columns.Select(column =>
        {
            var declaration = $"{_dialect.Quote(column.Name)} {_dialect.SqlType(column)}";
            if (!configuration.HasCompositeKey && column == configuration.Key)
            {
                if (configuration.KeyIsGenerated)
                {
                    declaration += " " + _dialect.GeneratedKeyDdl;
                }

                declaration += " PRIMARY KEY";
            }

            return declaration;
        });

        // A composite key declares at the table level; a simple one stays inline on its column.
        var body = string.Join(", ", columns);
        if (configuration.HasCompositeKey)
        {
            body += $", PRIMARY KEY ({string.Join(", ", configuration.Keys.Select(key => _dialect.Quote(key.Name)))})";
        }

        return _dialect.CreateTableSql(_dialect.Quote(configuration.TableName), body);
    }

    private string CreateIndex(EnsureIndexOperation operation)
    {
        var configuration = _model.For(operation.EntityType);
        if (operation.ExpireAfter is not null)
        {
            throw new NotSupportedException("Relational stores have no TTL indexes; expire rows with a scheduled delete instead.");
        }

        var keys = operation.Keys
            .Select(key => (Column: Column(configuration, key.Selector.GetMemberName()), key.Descending))
            .ToList();
        var name = operation.Name
                   ?? $"ix_{configuration.TableName}_{string.Join("_", keys.Select(key => key.Column.Name))}";
        var list = string.Join(", ", keys.Select(key => $"{_dialect.Quote(key.Column.Name)}{(key.Descending ? " DESC" : string.Empty)}"));

        return _dialect.CreateIndexSql(_dialect.Quote(name), _dialect.Quote(configuration.TableName), list,
            operation.Unique, operation.Method, FilterFragment(configuration, operation.Filter));
    }

    /// <summary>
    ///     Renders a filtered-index predicate through the same interpretation as query filters, then inlines the
    ///     bound values as literals — DDL cannot carry parameters.
    /// </summary>
    private string? FilterFragment(RelationalEntityConfiguration configuration, LambdaExpression? filter)
    {
        if (filter is null)
        {
            return null;
        }

        var parameters = new List<object?>();
        string fragment;
        try
        {
            fragment = SqlFilterRenderer.Render(_dialect, configuration, FilterInterpreter.Interpret(filter), parameters);
        }
        catch (NotSupportedException inner)
        {
            throw new NotSupportedException(
                $"The filtered-index predicate must be fully expressible in SQL. {inner.Message}", inner);
        }

        for (var index = parameters.Count - 1; index >= 0; index--)
        {
            fragment = fragment.Replace("@p" + index, _dialect.Literal(parameters[index]));
        }

        return fragment;
    }

    private (string Sql, List<object?> Parameters) Update(UpdateOperation operation)
    {
        var configuration = _model.For(operation.EntityType);
        var parameters = new List<object?>();

        var set = string.Join(", ", operation.Sets.Select(assignment =>
        {
            parameters.Add(_dialect.BindValue(assignment.Value));
            return $"{_dialect.Quote(Column(configuration, assignment.Field.GetMemberName()).Name)} = @p{parameters.Count - 1}";
        }));

        var where = SqlFilterRenderer.Render(_dialect, configuration, FilterInterpreter.Interpret(operation.Predicate), parameters);
        return ($"UPDATE {_dialect.Quote(configuration.TableName)} SET {set} WHERE {where}", parameters);
    }

    private static RelationalColumn Column(RelationalEntityConfiguration configuration, string memberName) =>
        configuration.ColumnFor(memberName)
        ?? throw new NotSupportedException($"'{configuration.EntityType.Name}' has no mapped member '{memberName}'.");

    private string SqlTypeOf(MigrationFieldType type) => type switch
    {
        MigrationFieldType.String => _dialect.SqlType(typeof(string)),
        MigrationFieldType.Boolean => _dialect.SqlType(typeof(bool)),
        MigrationFieldType.Int32 => _dialect.SqlType(typeof(int)),
        MigrationFieldType.Int64 => _dialect.SqlType(typeof(long)),
        MigrationFieldType.Double => _dialect.SqlType(typeof(double)),
        MigrationFieldType.Decimal => _dialect.SqlType(typeof(decimal)),
        MigrationFieldType.DateTime => _dialect.SqlType(typeof(DateTime)),
        MigrationFieldType.Guid => _dialect.SqlType(typeof(Guid)),
        _ => throw new NotSupportedException($"Cannot convert a relational column to '{type}'."),
    };

    private static async Task ExecuteAsync(DbConnection connection, string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "p" + index;
            parameter.Value = parameters[index] ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Tracks applied migrations in a <c>_migrations</c> table.</summary>
public sealed class RelationalMigrationHistory : IMigrationHistory
{
    private readonly DbDataSource _dataSource;
    private readonly SqlDialect _dialect;

    /// <summary>Initializes the history.</summary>
    public RelationalMigrationHistory(DbDataSource dataSource, SqlDialect dialect)
    {
        _dataSource = dataSource;
        _dialect = dialect;
    }

    private string Table => _dialect.Quote("_migrations");

    /// <inheritdoc />
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = _dialect.CreateTableSql(Table,
            $"{_dialect.Quote("id")} {_dialect.SqlType(typeof(string))} PRIMARY KEY, " +
            $"{_dialect.Quote("title")} {_dialect.SqlType(typeof(string))}, " +
            $"{_dialect.Quote("date")} {_dialect.SqlType(typeof(DateTime))}, " +
            $"{_dialect.Quote("applied_at")} {_dialect.SqlType(typeof(DateTime))}");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<string>> GetAppliedIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {_dialect.Quote("id")} FROM {Table}";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var ids = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <inheritdoc />
    public async Task RecordAsync(AppliedMigration migration, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO {Table} VALUES (@p0, @p1, @p2, @p3)";
        var values = new object?[]
        {
            migration.Id, migration.Title,
            DateTime.SpecifyKind(migration.Date, DateTimeKind.Utc), DateTime.UtcNow,
        };
        for (var index = 0; index < values.Length; index++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "p" + index;
            parameter.Value = values[index] ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
///     Discovers the migrations marked with <see cref="MigrationAttribute" /> across the supplied assemblies,
///     orders them by timestamp, skips the recorded ones and applies the rest through the executor.
/// </summary>
public sealed class RelationalMigrationRunner : IMigrationRunner
{
    private readonly IMigrationExecutor _executor;
    private readonly IMigrationHistory _history;
    private readonly IReadOnlyList<System.Reflection.Assembly> _assemblies;
    private readonly Data.Migration.MigrationSource? _source;

    /// <summary>Initializes the runner.</summary>
    public RelationalMigrationRunner(IMigrationExecutor executor, IMigrationHistory history,
        IEnumerable<System.Reflection.Assembly> assemblies, Data.Migration.MigrationSource? source = null)
    {
        _executor = executor;
        _history = history;
        _assemblies = assemblies.Distinct().ToArray();
        _source = source;
    }

    /// <inheritdoc />
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        await _history.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        var pending = Data.Migration.MigrationDiscovery.Pending(_assemblies, _source);
        if (pending.Count == 0)
        {
            return 0;
        }

        var applied = new HashSet<string>(await _history.GetAppliedIdsAsync(cancellationToken).ConfigureAwait(false));

        var count = 0;
        foreach (var (attribute, migration) in pending)
        {
            if (applied.Contains(attribute.Id))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var builder = new MigrationBuilder();
            migration.Up(builder);

            await _executor.ApplyAsync(builder.Operations, cancellationToken).ConfigureAwait(false);
            await _history
                .RecordAsync(new AppliedMigration(attribute.Id, attribute.Title, attribute.Date, DateTime.UtcNow), cancellationToken)
                .ConfigureAwait(false);

            count++;
        }

        return count;
    }
}
