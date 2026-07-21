using System.Linq.Expressions;
using BenchmarkDotNet.Attributes;
using eQuantic.Core.Data.Cassandra;
using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Benchmarks;

/// <summary>A representative entity for the translation benchmarks.</summary>
public sealed class Order
{
    public int TenantId { get; set; }

    public DateTime CreatedAt { get; set; }

    public decimal Total { get; set; }

    public string Status { get; set; } = "";

    public List<string> Tags { get; set; } = [];
}

/// <summary>
///     The filter path: predicate → node model → dialect-agnostic IR → CQL plan. These are the per-query costs
///     the engine adds before a statement reaches the driver (the statement itself is prepared once per session,
///     so translation is the recurring client-side cost).
/// </summary>
[MemoryDiagnoser]
public class FilterTranslationBenchmarks
{
    private static readonly Expression<Func<Order, bool>> Simple = x => x.TenantId == 5;

    private static readonly Expression<Func<Order, bool>> Composite =
        x => x.TenantId == 5 && x.CreatedAt >= new DateTime(2026, 1, 1) && x.Total > 100m;

    private static readonly Expression<Func<Order, bool>> WithResidual =
        x => x.TenantId == 5 && x.Status != "closed";

    private static readonly Expression<Func<Order, bool>> OrSplit =
        x => x.TenantId == 1 || (x.TenantId == 2 && x.CreatedAt >= new DateTime(2026, 1, 1));

    private static readonly CassandraEntityConfiguration Configuration = new CassandraModelBuilder()
        .Entity<Order>(entity => entity
            .Table("orders")
            .PartitionKey(x => x.TenantId)
            .ClusteringKey(x => x.CreatedAt, descending: true))
        .Build()
        .For(typeof(Order));

    [Benchmark(Baseline = true)]
    public QueryFilter Interpret_simple_equality() => FilterInterpreter.Interpret(Simple);

    [Benchmark]
    public QueryFilter Interpret_composite() => FilterInterpreter.Interpret(Composite);

    [Benchmark]
    public object Plan_fully_pushed_down() => CassandraCql.Plan<Order>(Configuration, null, Composite);

    [Benchmark]
    public object Plan_with_residual_rebuild() => CassandraCql.Plan<Order>(Configuration, null, WithResidual);

    [Benchmark]
    public object Plan_or_split() => CassandraCql.Plan<Order>(Configuration, null, OrSplit);
}

/// <summary>The update path: member-init factory → node model → dialect-agnostic assignments.</summary>
[MemoryDiagnoser]
public class UpdateTranslationBenchmarks
{
    private static readonly Expression<Func<Order, Order>> SetOnly = x => new Order { Status = "closed" };

    private static readonly Expression<Func<Order, Order>> Increment = x => new Order { Total = x.Total + 1m };

    private static readonly Expression<Func<Order, Order>> CollectionAdd =
        x => new Order { Tags = x.Tags.Append("vip").ToList() };

    [Benchmark(Baseline = true)]
    public object Interpret_set_only() => UpdateInterpreter.Interpret(SetOnly);

    [Benchmark]
    public object Interpret_increment() => UpdateInterpreter.Interpret(Increment);

    [Benchmark]
    public object Interpret_collection_add() => UpdateInterpreter.Interpret(CollectionAdd);
}
