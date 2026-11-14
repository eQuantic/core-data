namespace eQuantic.Core.Data.Repository.Options;

/// <summary>
///     Where a sort places the rows whose key is null (or, on a document store, missing). Stores disagree on the
///     default — PostgreSQL puts nulls last when ascending, SQL Server, MySQL, MariaDB, MongoDB and Cosmos DB put
///     them first — so a read that depends on the placement says so, and every engine returns that order.
/// </summary>
public enum NullsOrder
{
    /// <summary>The store's own placement (the order a read had before nulls could be placed).</summary>
    Default = 0,

    /// <summary>Nulls come before every value, whatever the direction.</summary>
    First = 1,

    /// <summary>Nulls come after every value, whatever the direction.</summary>
    Last = 2,
}
