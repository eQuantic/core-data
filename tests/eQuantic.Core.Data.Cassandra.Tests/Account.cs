using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>
///     A partition-key-only test entity: the primary key is the single partition column <see cref="Id" />, which is
///     also the <see cref="IEntity{TKey}" /> key — so point lookups (<c>Get</c>/<c>Find</c>) resolve an exact row.
///     Carries a decimal, a boolean and a collection to prove those CQL types round-trip through the mapper.
/// </summary>
public sealed class Account : IEntity<Guid>
{
    public Guid Id { get; set; }

    public string Owner { get; set; } = "";

    public decimal Balance { get; set; }

    public bool Active { get; set; }

    public List<string> Tags { get; set; } = [];

    public DateTime OpenedAt { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}
