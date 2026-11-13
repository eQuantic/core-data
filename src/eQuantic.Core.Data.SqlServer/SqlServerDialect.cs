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
    public override bool ReadsBackInsertedValues => true;

    /// <inheritdoc />
    public override string InsertSql(string quotedTable, string columns, string values, IReadOnlyList<string> returning,
        string? keyPredicate = null) =>
        returning.Count == 0
            ? $"INSERT INTO {quotedTable} ({columns}) VALUES ({values})"
            : $"INSERT INTO {quotedTable} ({columns}) OUTPUT {string.Join(", ", returning.Select(column => "INSERTED." + column))} VALUES ({values})";

    /// <inheritdoc />
    public override string AlterColumnType(string quotedTable, string quotedColumn, string sqlType) =>
        $"ALTER TABLE {quotedTable} ALTER COLUMN {quotedColumn} {sqlType}";

    /// <inheritdoc />
    /// <remarks>
    ///     Errors 2627 (a primary key or unique constraint) and 2601 (a unique index); the name is read from the
    ///     message, the only place SQL Server gives it.
    /// </remarks>
    public override eQuantic.Core.Data.Repository.DuplicateKeyException? DuplicateKey(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is Microsoft.Data.SqlClient.SqlException sql
                && sql.Errors.Cast<Microsoft.Data.SqlClient.SqlError>().FirstOrDefault(error => error.Number is 2627 or 2601) is { } error)
            {
                return new eQuantic.Core.Data.Repository.DuplicateKeyException(ConstraintName(error.Number, error.Message), exception);
            }
        }

        return null;
    }

    /// <summary>
    ///     The name in <c>Violation of UNIQUE KEY constraint 'name'.</c> (2627) or <c>… with unique index 'name'.</c> (2601).
    /// </summary>
    private static string? ConstraintName(int number, string message)
    {
        var marker = number == 2601 ? "unique index '" : "constraint '";
        var start = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = message.IndexOf('\'', start);
        return end > start ? message[start..end] : null;
    }

    /// <inheritdoc />
    public override bool IsDeadlock(Exception exception) =>
        exception is Microsoft.Data.SqlClient.SqlException sql && sql.Errors.Cast<Microsoft.Data.SqlClient.SqlError>().Any(error => error.Number is 1205 or 1222);

    /// <inheritdoc />
    /// <remarks>SQL Server binds at most 2100 parameters per request.</remarks>
    public override int MaxParameters => 2000;

    /// <inheritdoc />
    /// <remarks>
    ///     <c>MERGE … WITH (HOLDLOCK) … OUTPUT INSERTED</c>: <c>HOLDLOCK</c> keeps the range the target falls in locked
    ///     from the match to the insert, so two merges of one target cannot both insert it — without it <c>MERGE</c> is
    ///     not race-free. The source names its columns apart from the table's, so the conflict filter's columns are the
    ///     target's.
    /// </remarks>
    public override bool UpsertsInOneStatement => true;

    /// <inheritdoc />
    public override string UpsertExistingRow(string quotedTable) => "[target]";

    /// <inheritdoc />
    public override string UpsertSql(string quotedTable, IReadOnlyList<string> insertColumns, IReadOnlyList<string> insertValues,
        IReadOnlyList<string> targetColumns, IReadOnlyList<string> targetValues, string? conflictFilter,
        IReadOnlyList<string> assignments, IReadOnlyList<string> returning)
    {
        var sources = targetColumns.Select((_, index) => $"[__eq_on{index}]").ToList();
        var on = string.Join(" AND ", targetColumns.Select((column, index) => $"[target].{column} = [source].{sources[index]}"));
        return $"MERGE INTO {quotedTable} WITH (HOLDLOCK) AS [target] " +
               $"USING (VALUES ({string.Join(", ", targetValues)})) AS [source] ({string.Join(", ", sources)}) " +
               $"ON {on}" + (conflictFilter is null ? string.Empty : $" AND {conflictFilter}") +
               $" WHEN MATCHED THEN UPDATE SET {string.Join(", ", assignments)}" +
               $" WHEN NOT MATCHED THEN INSERT ({string.Join(", ", insertColumns)}) VALUES ({string.Join(", ", insertValues)})" +
               $" OUTPUT {string.Join(", ", returning.Select(column => "INSERTED." + column))};";
    }

    /// <inheritdoc />
    /// <remarks>
    ///     One insert per row, each in <c>TRY…CATCH</c> that swallows only errors 2627 and 2601: a duplicate key
    ///     ends its own statement and leaves the transaction committable, so the batch carries on and counts the rows
    ///     it inserted. Any other error is thrown again.
    /// </remarks>
    public override string? InsertSkippingDuplicatesSql(string quotedTable, string columns, IReadOnlyList<string> rows) =>
        "DECLARE @inserted int = 0; " +
        string.Concat(rows.Select(row =>
            $"BEGIN TRY INSERT INTO {quotedTable} ({columns}) VALUES {row}; SET @inserted += 1; END TRY " +
            "BEGIN CATCH IF ERROR_NUMBER() NOT IN (2601, 2627) THROW; END CATCH; ")) +
        "SELECT @inserted;";

    /// <inheritdoc />
    public override string? UpdateReturningSql(string quotedTable, string set, string where, IReadOnlyList<string> returning) =>
        $"UPDATE {quotedTable} SET {set} OUTPUT {string.Join(", ", returning.Select(column => "INSERTED." + column))} WHERE {where}";

    /// <inheritdoc />
    /// <remarks>
    ///     SQL Server has no <c>RENAME</c> statement; it calls <c>sp_rename</c>, whose second argument is the new
    ///     name alone — schema-qualifying or bracketing it renames the table to something containing brackets.
    /// </remarks>
    public override string RenameTableSql(string quotedTable, string target) =>
        $"EXEC sp_rename '{quotedTable}', '{target}'";

    /// <inheritdoc />
    /// <remarks>
    ///     The statement runs through <c>EXEC</c>: SQL Server refuses <c>NEXT VALUE FOR</c> (a store-generated
    ///     column's sequence default) in a <c>CREATE TABLE</c> that sits directly under an <c>IF</c>.
    /// </remarks>
    public override string CreateTableSql(string quotedTable, string columnsDdl) =>
        $"IF OBJECT_ID(N'{quotedTable}', 'U') IS NULL EXEC(N'{$"CREATE TABLE {quotedTable} ({columnsDdl})".Replace("'", "''")}')";

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
            table.Columns.Add(columns[index].Name, BoundType(columns[index]));
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
            return NativeEnumOf(underlying) is { } native ? EnumSqlType(native) : "int";
        }

        // SQL Server keeps JSON as text, held to being JSON by an ISJSON check (CheckConstraintSql).
        if (IsDocumentColumn(underlying))
        {
            return "nvarchar(max)";
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
        Enum enumValue when NativeEnumOf(enumValue.GetType()) is null => Convert.ToInt32(enumValue),
        _ => base.BindValue(value),
    };

    /// <inheritdoc />
    public override string TextLiteral(string text) => "N'" + text.Replace("'", "''") + "'";

    /// <inheritdoc />
    /// <remarks>
    ///     An enum stored as its labels is held to them, and a JSON member's text to being JSON. A JSON member may
    ///     hold a scalar, which <c>ISJSON</c> accepts only with the <c>VALUE</c> type of SQL Server 2022; wrapped in
    ///     brackets, any JSON value is an array that every <c>ISJSON</c> since SQL Server 2016 accepts.
    /// </remarks>
    public override string? CheckConstraintSql(string quotedColumn, RelationalColumn column) =>
        base.CheckConstraintSql(quotedColumn, column)
        ?? (column.Converter is null && IsDocumentColumn(column.StoredType)
            ? $"CHECK (ISJSON(CONCAT(N'[', {quotedColumn}, N']')) = 1)"
            : null);

    /// <inheritdoc />
    public override string ClockSql(Type type) =>
        (Nullable.GetUnderlyingType(type) ?? type) == typeof(DateTimeOffset) ? "SYSDATETIMEOFFSET()" : "SYSUTCDATETIME()";

    /// <inheritdoc />
    /// <remarks>
    ///     A sequence object rather than an identity: a table holds one identity column, which a generated key
    ///     may already be.
    /// </remarks>
    public override string SequenceDdl(string table, RelationalColumn column) =>
        $"DEFAULT (NEXT VALUE FOR {Quote(SequenceName(table, column))})";

    /// <inheritdoc />
    public override IReadOnlyList<string> StoreGeneratedPrerequisiteSql(string table, RelationalColumn column) =>
        column.StoreDefaultSql is null && column.StoreValue == eQuantic.Core.Data.Modeling.StoreValue.Sequence
            ?
            [
                $"IF OBJECT_ID(N'{SequenceName(table, column).Replace("'", "''")}', N'SO') IS NULL " +
                $"CREATE SEQUENCE {Quote(SequenceName(table, column))} AS {SqlType(column.StoredType)} START WITH 1 INCREMENT BY 1",
            ]
            : [];

    private static string SequenceName(string table, RelationalColumn column) => $"{table}_{column.Name}_seq";
}
