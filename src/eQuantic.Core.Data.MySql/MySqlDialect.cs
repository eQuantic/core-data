using System.Data.Common;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Relational;
using MySqlConnector;

namespace eQuantic.Core.Data.MySql;

/// <summary>
///     MySQL's SQL flavour: snake_case naming, <c>`backtick`</c> identifiers, <c>LIMIT/OFFSET</c>, row-wise tuple
///     comparisons, and <c>AUTO_INCREMENT</c> keys — which MySQL cannot read back from an insert (no
///     <c>RETURNING</c>), so generated-key backfill is rejected with guidance: declare a client-generated key.
///     Collection columns do not exist; collection clauses degrade to the gated client-side residual.
/// </summary>
public class MySqlDialect : SqlDialect
{
    /// <inheritdoc />
    public override string System => "mysql";

    /// <inheritdoc />
    public override string Quote(string identifier) => "`" + identifier.Replace("`", "``") + "`";

    /// <inheritdoc />
    public override string GeneratedKeyDdl => "AUTO_INCREMENT";

    /// <inheritdoc />
    /// <remarks>
    ///     <c>column_type</c> rather than <c>data_type</c>: it carries the display width and the fractional-second
    ///     precision that this dialect writes into its DDL — <c>tinyint(1)</c>, <c>datetime(6)</c> — which
    ///     <c>data_type</c> drops, and dropping them would make every boolean and every timestamp read as drift.
    /// </remarks>
    public override string? IntrospectColumnsSql =>
        """
        SELECT table_name, column_name, column_type, is_nullable = 'YES'
        FROM information_schema.columns
        WHERE table_schema = database()
        """;

    /// <inheritdoc />
    public override string TupleComparison(IReadOnlyList<string> columns, ComparisonOperator op, IReadOnlyList<string> parameters) =>
        $"({string.Join(", ", columns)}) {op switch
        {
            ComparisonOperator.Equal => "=",
            ComparisonOperator.NotEqual => "<>",
            ComparisonOperator.GreaterThan => ">",
            ComparisonOperator.GreaterThanOrEqual => ">=",
            ComparisonOperator.LessThan => "<",
            ComparisonOperator.LessThanOrEqual => "<=",
            _ => throw new NotSupportedException($"The operator '{op}' is not expressible."),
        }} ({string.Join(", ", parameters)})";

    /// <inheritdoc />
    public override string AlterColumnType(string quotedTable, string quotedColumn, string sqlType) =>
        $"ALTER TABLE {quotedTable} MODIFY {quotedColumn} {sqlType}";

    /// <inheritdoc />
    public override string CreateIndexSql(string quotedName, string quotedTable, string columns, bool unique,
        eQuantic.Core.Data.Migration.IndexMethod method, string? filter)
    {
        if (method != eQuantic.Core.Data.Migration.IndexMethod.Default)
        {
            throw new NotSupportedException(
                $"MySQL has no '{method}' index structure; use a default index, or the store's native tooling via Run(...).");
        }

        if (filter is not null)
        {
            throw new NotSupportedException(
                "MySQL has no filtered indexes; index the whole column, or restructure with a generated column via Run(...).");
        }

        // MySQL has no CREATE INDEX IF NOT EXISTS; the migration history guards re-runs.
        return $"CREATE {(unique ? "UNIQUE " : string.Empty)}INDEX {quotedName} ON {quotedTable} ({columns})";
    }

    /// <inheritdoc />
    public override string SqlType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying.IsEnum)
        {
            return NativeEnumOf(underlying) is { } native ? EnumSqlType(native) : "int";
        }

        if (IsDocumentColumn(underlying))
        {
            return "json";
        }

        return underlying switch
        {
            // varchar keeps strings indexable (MySQL cannot index TEXT without a key length).
            _ when underlying == typeof(string) => "varchar(255)",
            _ when underlying == typeof(Guid) => "char(36)",
            _ when underlying == typeof(bool) => "tinyint(1)",
            _ when underlying == typeof(byte) || underlying == typeof(short) => "smallint",
            _ when underlying == typeof(int) => "int",
            _ when underlying == typeof(long) => "bigint",
            _ when underlying == typeof(float) => "float",
            _ when underlying == typeof(double) => "double",
            _ when underlying == typeof(decimal) => "decimal(18,6)",
            _ when underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset) => "datetime(6)",
            _ when underlying == typeof(TimeSpan) => "time(6)",
            _ when underlying == typeof(byte[]) => "longblob",
            _ => throw new NotSupportedException($"No MySQL type mapping for '{underlying.Name}'."),
        };
    }

    /// <inheritdoc />
    public override object? BindValue(object? value) => value switch
    {
        Enum enumValue when NativeEnumOf(enumValue.GetType()) is null => Convert.ToInt32(enumValue),
        _ => base.BindValue(value),
    };

    /// <inheritdoc />
    /// <remarks>An <c>ENUM(...)</c> column: the store itself holds it to the labels.</remarks>
    public override string EnumSqlType(RelationalNativeEnum declaration) =>
        "ENUM(" + string.Join(", ", declaration.Labels.Values.Select(TextLiteral)) + ")";

    /// <inheritdoc />
    public override bool EnumTypeRestrictsLabels => true;

    /// <inheritdoc />
    public override string TextLiteral(string text) => "'" + text.Replace("\\", "\\\\").Replace("'", "''") + "'";

    /// <inheritdoc />
    public override string ClockSql(Type type) => "(UTC_TIMESTAMP(6))";

    /// <inheritdoc />
    /// <remarks>
    ///     MySQL numbers a column with <c>AUTO_INCREMENT</c>, which it allows on one indexed column per table: a key
    ///     MySQL generates is refused already (it cannot be read back), so this column can always be that one.
    /// </remarks>
    public override string SequenceDdl(string table, RelationalColumn column) => "AUTO_INCREMENT UNIQUE";

    /// <inheritdoc />
    /// <remarks>MySQL has no <c>RETURNING</c>: the row is read back by its key, in the same batch.</remarks>
    public override bool ReadsBackInsertedValues => true;

    /// <inheritdoc />
    public override bool ReadsBackByKey => true;

    /// <inheritdoc />
    public override string InsertSql(string quotedTable, string columns, string values, IReadOnlyList<string> returning,
        string? keyPredicate = null) =>
        returning.Count == 0
            ? $"INSERT INTO {quotedTable} ({columns}) VALUES ({values})"
            : keyPredicate is null
                // Only a generated key comes without a predicate, and MySQL cannot read one back.
                ? InsertSql(quotedTable, columns, values, returning[0])
                : $"INSERT INTO {quotedTable} ({columns}) VALUES ({values}); " +
                  $"SELECT {string.Join(", ", returning)} FROM {quotedTable} WHERE {keyPredicate}";

    /// <inheritdoc />
    public override bool SupportsBulkInsert => true;

    /// <inheritdoc />
    /// <remarks>
    ///     MySqlConnector's <c>MySqlBulkCopy</c> — the client-side bulk loader, which streams rows without a
    ///     statement per row. Column mappings are explicit (ordinal to name), so the engine's row order is
    ///     authoritative regardless of the table's physical column order.
    /// </remarks>
    public override async Task<long> BulkInsertAsync(DbConnection connection, DbTransaction? transaction,
        string quotedTable, IReadOnlyList<RelationalColumn> columns, IReadOnlyList<object?[]> rows,
        CancellationToken cancellationToken)
    {
        var bulk = new MySqlBulkCopy((MySqlConnection)connection, (MySqlTransaction?)transaction)
        {
            DestinationTableName = quotedTable,
        };
        for (var index = 0; index < columns.Count; index++)
        {
            bulk.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(index, columns[index].Name));
        }

        var table = new System.Data.DataTable();
        foreach (var column in columns)
        {
            table.Columns.Add(column.Name, BoundType(column));
        }

        foreach (var row in rows)
        {
            table.Rows.Add(row.Select(value => value ?? DBNull.Value).ToArray());
        }

        try
        {
            var result = await bulk.WriteToServerAsync(table, cancellationToken).ConfigureAwait(false);
            return result.RowsInserted;
        }
        catch (NotSupportedException exception)
        {
            // MySQL's bulk loader rides LOAD DATA LOCAL INFILE, which both sides must opt into — the client
            // in its connection string and the server in its configuration. That is a deployment decision with
            // security weight (the server can ask the client for local files), so the engine surfaces it
            // instead of turning it on behind your back.
            throw new NotSupportedException(
                "MySQL's bulk load needs LOAD DATA LOCAL INFILE enabled on both sides: add " +
                "'AllowLoadLocalInfile=true' to the connection string and set 'local_infile=1' on the server. " +
                "Without it, stage the entities and Commit() — the flush already batches them.", exception);
        }
    }
}
