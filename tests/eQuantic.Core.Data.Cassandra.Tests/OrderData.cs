using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>A test entity: partition key <see cref="TenantId" />, clustering key <see cref="CreatedAt" />.</summary>
public sealed class OrderData : IEntity<Guid>
{
    public Guid Id { get; set; }

    public int TenantId { get; set; }

    public DateTime CreatedAt { get; set; }

    public decimal Total { get; set; }

    public bool IsPaid { get; set; }

    public string Status { get; set; } = "";

    public List<string> Tags { get; set; } = [];

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}
