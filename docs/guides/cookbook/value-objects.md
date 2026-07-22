# Cookbook — value objects and conversions

Keep the domain typed — `Email`, `Money`, enums with meaning — and let converters own the boundary
to storage. Declared once, applied **everywhere**: DDL, writes, filters, set-based updates,
materialization.

## A value object on a relational store

```csharp
public sealed class EmailAddress
{
    public string Value { get; }
    private EmailAddress(string value) => Value = value;
    public static EmailAddress Create(string value) =>
        value.Contains('@') ? new EmailAddress(value.ToLowerInvariant())
                            : throw new ArgumentException("Invalid email.");
}

public sealed class Subscriber : IEntity<Guid>
{
    public Guid Id { get; set; }
    public EmailAddress Email { get; set; } = EmailAddress.Create("nobody@nowhere");
    public SubscriberStatus Status { get; set; }
    public Guid GetKey() => Id;
    public void SetKey(Guid key) => Id = key;
}
```

```csharp
model.Entity<Subscriber>(entity => entity
    .Converts(x => x.Email, email => email.Value, EmailAddress.Create)
    .Converts(x => x.Status, status => status.ToString(), value => Enum.Parse<SubscriberStatus>(value)));
```

What you get, with zero further ceremony:

```csharp
// filters compare through the converter — this renders WHERE email = 'ana@corp.com':
var found = await repo.GetFilteredAsync(s => s.Email == EmailAddress.Create("ana@corp.com"));

// enum-as-string: readable rows in the database, typed code above it:
var active = await repo.GetFilteredAsync(s => s.Status == SubscriberStatus.Active);
```

The DDL types the column from the **stored** type (`text` here), inserts and updates bind the
converted value, and materialization runs `FromStored` — the value object's factory validates on
the way back in, exactly where corrupt data should surface.

## Per store

| Store | Converter shape | Note |
|---|---|---|
| Relational | `Converts(member, toStored, fromStored)` — per member | applies to DDL, binding, filters, set-based updates, materialization |
| MongoDB | `Converts(member, toStored, fromStored)` — per member | flows into the class map; the driver serializes filter constants through it |
| Cosmos DB | `Converts<TMember, TStored>(toStored, fromStored)` — **per type** | the SDK serializes filter constants by type; type-level keeps documents and filters converting identically |
| Cassandra | not yet — map scalars directly or via `[StoredAs]`-renamed scalar members | on the roadmap |

## Rules of thumb

- Convert to **scalars** (string, number, Guid, date) — the model validates this and says so.
- Keep `FromStored` total or loudly failing: it is your last line against bad rows.
- For enums, string storage (`ToString`/`Parse`) beats numeric storage the moment anyone reads the
  database directly or reorders the enum. That readability is why the recipe exists.
