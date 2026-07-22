# Modeling — overview and precedence

Entities are modeled three ways, with an **explicit, deterministic precedence**:

```
conventions  <  annotations  <  fluent
```

1. **Conventions** cover the common case with zero declarations: the member named `Id` is the key,
   names go through each store's naming convention (snake_case columns on SQL, member names on
   MongoDB, camelCase on Cosmos), scalars map to columns/elements, entity references become
   navigations.
2. **Annotations** — the [store-neutral vocabulary](annotations.md) owned by eQuantic — declare the
   portable middle layer *on the entity*: keys, stored names, partitioning, ordering, concurrency,
   search, TTL, facets.
3. **Fluent builders** — per provider, at registration — override everything and add what only
   configuration can know (converters with lambdas, composite keys, explicit paths).

The same entity, all three layers at work:

```csharp
[Entity("sale_orders")]                       // annotation: storage name
public sealed class SaleOrder : IEntity<Guid>
{
    public Guid Id { get; set; }              // convention: the key
    [StoredAs("client_name")]
    public string Name { get; set; } = "";    // annotation: stored name
    public OrderStatus Status { get; set; }   // fluent (below): stored as string
    public Guid GetKey() => Id;
    public void SetKey(Guid key) => Id = key;
}

services.AddPostgreSqlDatabase(connectionString, model => model
    .Entity<SaleOrder>(entity => entity
        .Converts(x => x.Status, s => s.ToString(), v => Enum.Parse<OrderStatus>(v))));
```

## Two rules that keep the system honest

**Never a driver attribute.** Entities carry no `[BsonElement]`, no EF `[Column]`, no
`[JsonProperty]` — only the eQuantic vocabulary. Move an entity from MongoDB to PostgreSQL and the
model still means the same thing; each provider interprets the subset that maps to its store.

**Out-of-vocabulary is ignored, in-vocabulary misuse throws.** `[PartitionKey]` on a relational
entity is simply ignored — relational stores have no partition concept, and ignoring it is
truthful. But `[TimeToLive]` on a MongoDB entity *without* a date member throws at model build with
guidance — the store has the concept and the declaration is incomplete, and silence would be a lie.

## Where each piece is documented

- [The annotation vocabulary](annotations.md) — all eleven annotations, with the per-provider
  matrix of what each one becomes.
- [Fluent builders](fluent.md) — the per-provider builder surfaces, including what only fluent can
  declare.
- [Conventions and lifecycle](conventions.md) — naming, `DataConventions`, timestamps, soft delete,
  user attribution.
- [Explain](explain.md) — how to *see* every decision the model made.
