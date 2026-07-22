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
    /// <inheritdoc />
    public override string System => "mssql";

    /// <inheritdoc />
    public override string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    /// <inheritdoc />
    public override string GeneratedKeyDdl => "IDENTITY(1,1)";

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
    public override string CreateIndexSql(string quotedName, string quotedTable, string columns, bool unique) =>
        // SQL Server has no CREATE INDEX IF NOT EXISTS; the migration history guards re-runs.
        $"CREATE {(unique ? "UNIQUE " : string.Empty)}INDEX {quotedName} ON {quotedTable} ({columns})";

    /// <inheritdoc />
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
