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
}
