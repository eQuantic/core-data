# Specifications

The repositories accept [eQuantic.Linq](https://www.nuget.org/packages/eQuantic.Linq.Specification)
specifications everywhere a filter goes — the classic DDD pattern for **naming and composing**
business predicates instead of scattering lambdas:

```csharp
using eQuantic.Linq.Specification;

public sealed class OverdueInvoices : Specification<Invoice>
{
    private readonly DateTime _today;
    public OverdueInvoices(DateTime today) => _today = today;

    public override Expression<Func<Invoice, bool>> SatisfiedBy() =>
        invoice => invoice.DueDate < _today && invoice.Status == InvoiceStatus.Open;
}
```

```csharp
// use it directly…
var overdue = await repository.AllMatchingAsync(new OverdueInvoices(today));

// …compose it…
var risky = new OverdueInvoices(today).And(new HighValue(10_000m));
var results = await repository.AllMatchingAsync(risky);

// …or put it on the options next to everything else:
var page = await repository.GetPagedAsync(new PageRequest(1, 20),
    new QueryOptions<Invoice>()
        .Where(new OverdueInvoices(today))
        .OrderBy(i => i.DueDate));
```

`And`, `Or` and `Not` compose specifications into new ones; `SatisfiedBy()` reduces the composite
to a single expression tree, which then flows through exactly the same
[pushdown pipeline](../querying/pushdown.md) as an inline lambda — a specification is never a
client-side afterthought.

## When to use them

- The predicate **is a business concept** ("overdue", "eligible for discount") that deserves a
  name, a home and its own unit tests.
- The same rule is used from several places — queries, validations, batch jobs.
- You compose rules dynamically (search screens, feature-flagged filters).

For one-off, purely technical filters, an inline lambda on `.Where(...)` is the right tool; the
engine treats both identically.

## Wire-friendly filters

`QueryOptions` also accepts filters as **data** — `Where(path, FilterOperator, value)`, query-string
syntax (`Where("price:lt:30")`-style via `QueryStringOptions`), and serialized
`ExpressionModel<TEntity>` payloads from eQuantic.Linq.Expressions. That is what makes filters
portable across process boundaries (an HTTP API accepting client-defined filters, a message
carrying a query) while still rendering server-side through the same interpreters.
