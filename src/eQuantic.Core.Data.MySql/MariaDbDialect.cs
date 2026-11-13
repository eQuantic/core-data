using eQuantic.Core.Data.Relational;

namespace eQuantic.Core.Data.MySql;

/// <summary>
///     MariaDB's flavour of the MySQL dialect: identical syntax and driver (MySqlConnector), plus
///     <c>INSERT … RETURNING</c> (MariaDB 10.5+) — so database-generated keys are read back into the entities
///     on commit, the capability MySQL itself cannot offer.
/// </summary>
public class MariaDbDialect : MySqlDialect
{
    /// <inheritdoc />
    public override string System => "mariadb";

    /// <inheritdoc />
    public override string InsertSql(string quotedTable, string columns, string values, string? returningKey) =>
        returningKey is null
            ? $"INSERT INTO {quotedTable} ({columns}) VALUES ({values})"
            : $"INSERT INTO {quotedTable} ({columns}) VALUES ({values}) RETURNING {returningKey}";

    /// <inheritdoc />
    public override bool ReadsBackInsertedValues => true;

    /// <inheritdoc />
    /// <remarks>MariaDB reads values back with <c>RETURNING</c>, not by the key.</remarks>
    public override bool ReadsBackByKey => false;

    /// <inheritdoc />
    public override string InsertSql(string quotedTable, string columns, string values, IReadOnlyList<string> returning,
        string? keyPredicate = null) =>
        returning.Count == 0
            ? $"INSERT INTO {quotedTable} ({columns}) VALUES ({values})"
            : $"INSERT INTO {quotedTable} ({columns}) VALUES ({values}) RETURNING {string.Join(", ", returning)}";

    /// <inheritdoc />
    /// <remarks>
    ///     A sequence object (MariaDB 10.3+) rather than <c>AUTO_INCREMENT</c>, which MariaDB allows once per table
    ///     and a generated key may already hold.
    /// </remarks>
    public override string SequenceDdl(string table, RelationalColumn column) =>
        $"DEFAULT NEXT VALUE FOR {Quote(SequenceName(table, column))}";

    /// <inheritdoc />
    public override IReadOnlyList<string> StoreGeneratedPrerequisiteSql(string table, RelationalColumn column) =>
        column.StoreDefaultSql is null && column.StoreValue == eQuantic.Core.Data.Modeling.StoreValue.Sequence
            ? [$"CREATE SEQUENCE IF NOT EXISTS {Quote(SequenceName(table, column))}"]
            : [];

    /// <inheritdoc />
    /// <remarks>MariaDB's <c>JSON</c> is <c>LONGTEXT</c> with a <c>JSON_VALID</c> check, and its catalogue says so.</remarks>
    protected override string Synonym(string baseType) => baseType == "json" ? "longtext" : base.Synonym(baseType);

    private static string SequenceName(string table, RelationalColumn column) => $"{table}_{column.Name}_seq";
}
