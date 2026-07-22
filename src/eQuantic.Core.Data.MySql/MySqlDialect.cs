using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Relational;

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
            return "int";
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
        Enum enumValue => Convert.ToInt32(enumValue),
        _ => value,
    };
}
