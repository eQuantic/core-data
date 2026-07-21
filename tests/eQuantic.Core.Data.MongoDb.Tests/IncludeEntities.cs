using eQuantic.Core.Data.Repository;
using MongoDB.Bson.Serialization.Attributes;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>
///     A customer with a one-to-many collection navigation to <see cref="Order" /> (the orders carry the foreign
///     key back). The navigation is not persisted (populated only by an <c>Include</c> $lookup at read time).
/// </summary>
public sealed class Customer : IEntity<string>
{
    public string Id { get; set; } = default!;

    public string Name { get; set; } = default!;

    [BsonIgnoreIfNull]
    public List<Order>? Orders { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;

    public static Customer New(string name) => new() { Id = Guid.NewGuid().ToString("N"), Name = name };
}

/// <summary>
///     An order holding the <see cref="CustomerId" /> foreign key and a reference navigation to its
///     <see cref="Customer" /> (populated only by an <c>Include</c> $lookup at read time).
/// </summary>
public sealed class Order : IEntity<string>
{
    public string Id { get; set; } = default!;

    public string CustomerId { get; set; } = default!;

    public int Amount { get; set; }

    [BsonIgnoreIfNull]
    public Customer? Customer { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;

    public static Order New(string customerId, int amount) =>
        new() { Id = Guid.NewGuid().ToString("N"), CustomerId = customerId, Amount = amount };
}
