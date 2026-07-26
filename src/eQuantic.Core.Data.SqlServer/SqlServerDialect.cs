using eQuantic.Core.Data.Relational;

namespace eQuantic.Core.Data.SqlServer;

/// <summary>
///     SQL Server's SQL flavour: snake_case naming, <c>[bracketed]</c> identifiers, <c>OFFSET/FETCH</c> paging
///     (which demands an ORDER BY — the engine adds a deterministic key order when the query has none),
///     <c>IDENTITY</c> keys read back with <c>OUTPUT INSERTED</c>, and <c>(1=0)</c> for the empty-IN literal.
///     Collection columns and row-wise tuple comparisons do not exist; those clauses degrade to the gated
///     client-side residual.
/// </summary>
public class SqlServerDialect : SqlDialect
{
    /// <summary>Initializes the dialect (T-SQL date parts instead of <c>EXTRACT</c>).</summary>
    public SqlServerDialect() =>
        Functions
            .Map("Year", (column, _) => $"YEAR({column})")
            .Map("Month", (column, _) => $"MONTH({column})")
            .Map("Day", (column, _) => $"DAY({column})");

    /// <inheritdoc />
    public override string System => "mssql";

    /// <inheritdoc />
    public override string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    /// <inheritdoc />
    public override string GeneratedKeyDdl => "IDENTITY(1,1)";

    /// <inheritdoc />
    /// <remarks>
    ///     <c>sys.columns</c> rather than <c>information_schema</c>, and the type composed here rather than
    ///     afterwards: only this query knows that <c>max_length</c> counts bytes, so an <c>nvarchar(450)</c>
    ///     reports 900, and that -1 is how <c>max</c> is stored.
    /// </remarks>
    public override string? IntrospectColumnsSql =>
        """
        SELECT t.name, c.name,
               ty.name + CASE
                   WHEN ty.name IN ('nvarchar', 'nchar')
                       THEN '(' + IIF(c.max_length = -1, 'max', CAST(c.max_length / 2 AS varchar(11))) + ')'
                   WHEN ty.name IN ('varchar', 'char', 'varbinary', 'binary')
                       THEN '(' + IIF(c.max_length = -1, 'max', CAST(c.max_length AS varchar(11))) + ')'
                   WHEN ty.name IN ('decimal', 'numeric')
                       THEN '(' + CAST(c.precision AS varchar(11)) + ',' + CAST(c.scale AS varchar(11)) + ')'
                   ELSE '' END,
               c.is_nullable
        FROM sys.columns c
        JOIN sys.tables t ON t.object_id = c.object_id
        JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE s.name = SCHEMA_NAME()
        """;

    /// <inheritdoc />
    public override bool RequiresOrderByForLimit => true;

    /// <inheritdoc />
    public override string FalseLiteral => "(1=0)";

    /// <inheritdoc />
    public override string LimitClause(string limitParameter, string? offsetParameter) =>
        $"OFFSET {offsetParameter ?? "0"} ROWS FETCH NEXT {limitParameter} ROWS ONLY";

    /// <inheritdoc />
    public override string InsertSql(string quotedTable, string columns, string values, string? returningKey) =>
        returningKey is null
            ? $"INSERT INTO {quotedTable} ({columns}) VALUES ({values})"
            : $"INSERT INTO {quotedTable} ({columns}) OUTPUT INSERTED.{returningKey} VALUES ({values})";

    /// <inheritdoc />
    public override string AlterColumnType(string quotedTable, string quotedColumn, string sqlType) =>
        $"ALTER TABLE {quotedTable} ALTER COLUMN {quotedColumn} {sqlType}";

    /// <inheritdoc />
    public override string CreateTableSql(string quotedTable, string columnsDdl) =>
        $"IF OBJECT_ID(N'{quotedTable}', 'U') IS NULL CREATE TABLE {quotedTable} ({columnsDdl})";

    /// <inheritdoc />
    public override string CreateIndexSql(string quotedName, string quotedTable, string columns, bool unique,
        eQuantic.Core.Data.Migration.IndexMethod method, string? filter)
    {
        if (method != eQuantic.Core.Data.Migration.IndexMethod.Default)
        {
            throw new NotSupportedException(
                $"SQL Server has no '{method}' index structure; use a default index, or the store's native tooling via Run(...).");
        }

        // SQL Server has no CREATE INDEX IF NOT EXISTS; the migration history guards re-runs.
        return $"CREATE {(unique ? "UNIQUE " : string.Empty)}INDEX {quotedName} ON {quotedTable} ({columns})"
               + (filter is not null ? $" WHERE {filter}" : string.Empty);
    }

    /// <inheritdoc />
    /// <remarks>SQL Server booleans are <c>bit</c>: literals inline as <c>1</c>/<c>0</c>.</remarks>
    public override string Literal(object? value) =>
        value is bool flag ? (flag ? "1" : "0") : base.Literal(value);

    /// <summary>The sized text type — <c>nvarchar(n)</c> on SQL Server.</summary>
    /// <param name="length">The maximum length.</param>
    protected override string SizedTextType(int length) => $"nvarchar({length})";

    /// <inheritdoc />
    public override bool SupportsBulkInsert => true;

    /// <inheritdoc />
    /// <remarks>
    ///     <c>SqlBulkCopy</c> — the bulk-load API of the SQL Server client, streaming rows to the server without
    ///     a statement per row. Column mappings are explicit (ordinal to name), so the row order the engine
    ///     produces is authoritative regardless of the table's physical column order.
    /// </remarks>
    public override async Task<long> BulkInsertAsync(System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction? transaction, string quotedTable,
        IReadOnlyList<RelationalColumn> columns, IReadOnlyList<object?[]> rows, CancellationToken cancellationToken)
    {
        using var bulk = new Microsoft.Data.SqlClient.SqlBulkCopy(
            (Microsoft.Data.SqlClient.SqlConnection)connection,
            Microsoft.Data.SqlClient.SqlBulkCopyOptions.Default,
            (Microsoft.Data.SqlClient.SqlTransaction?)transaction)
        {
            DestinationTableName = quotedTable,
        };

        var table = new System.Data.DataTable();
        for (var index = 0; index < columns.Count; index++)
        {
            var stored = Nullable.GetUnderlyingType(columns[index].StoredType) ?? columns[index].StoredType;
            table.Columns.Add(columns[index].Name, stored);
            bulk.ColumnMappings.Add(index, columns[index].Name);
        }

        foreach (var row in rows)
        {
            table.Rows.Add(row.Select(value => value ?? DBNull.Value).ToArray());
        }

        await bulk.WriteToServerAsync(table, cancellationToken).ConfigureAwait(false);
        return rows.Count;
    }

    public override string SqlType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying.IsEnum)
        {
            return "int";
        }

        return underlying switch
        {
            // nvarchar(450) keeps strings within the index key-size limit.
            _ when underlying == typeof(string) => "nvarchar(450)",
            _ when underlying == typeof(Guid) => "uniqueidentifier",
            _ when underlying == typeof(bool) => "bit",
            _ when underlying == typeof(byte) => "tinyint",
            _ when underlying == typeof(short) => "smallint",
            _ when underlying == typeof(int) => "int",
            _ when underlying == typeof(long) => "bigint",
            _ when underlying == typeof(float) => "real",
            _ when underlying == typeof(double) => "float",
            _ when underlying == typeof(decimal) => "decimal(18,6)",
            _ when underlying == typeof(DateTime) => "datetime2",
            _ when underlying == typeof(DateTimeOffset) => "datetimeoffset",
            _ when underlying == typeof(TimeSpan) => "time",
            _ when underlying == typeof(byte[]) => "varbinary(max)",
            _ => throw new NotSupportedException($"No SQL Server type mapping for '{underlying.Name}'."),
        };
    }

    /// <inheritdoc />
    public override object? BindValue(object? value) => value switch
    {
        Enum enumValue => Convert.ToInt32(enumValue),
        _ => value,
    };
}
