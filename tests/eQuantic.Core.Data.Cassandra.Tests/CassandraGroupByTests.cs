using eQuantic.Core.Data.Cassandra.Extensions;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>
///     Exercises the typed <c>GroupBy</c> against a real cluster: native CQL <c>GROUP BY</c> restricted to the
///     primary key, aggregates computed on the cluster, HAVING evaluated over the cluster-computed aggregate
///     cells (CQL has no <c>HAVING</c> — no extra rows travel), the primary-key validation, and the gated
///     residual fallback.
/// </summary>
[TestFixture]
public sealed class CassandraGroupByTests : CassandraIntegrationTest
{
    private static Reading At(int sensor, int hour, double value, string quality = "good") => new()
    {
        SensorId = sensor,
        At = new DateTime(2026, 1, 1, hour, 0, 0, DateTimeKind.Utc),
        Value = value,
        Quality = quality,
    };

    [Test]
    public async Task Group_by_partition_key_renders_a_native_cql_group_by()
    {
        using var db = await NewSchemaAsync();
        await Seed(db, At(1, 0, 10d), At(1, 1, 20d), At(2, 0, 40d));

        var grouped = (IGroupedReadRepository<Reading>)ReadingRepo(db);
        var groups = (await grouped.GroupByAsync(x => x.SensorId,
                g => new { Sensor = g.Key, Points = g.Count(), Total = g.Sum(x => x.Value), First = g.Min(x => x.At) }))
            .OrderBy(x => x.Sensor).ToList();

        Assert.That(groups.Select(x => (x.Sensor, x.Points, x.Total)),
            Is.EqualTo(new[] { (1, 2L, 30d), (2, 1L, 40d) }),
            "the cluster grouped by the partition key and aggregated per group");
        Assert.That(groups[0].First, Is.EqualTo(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public async Task Group_by_partition_plus_clustering_key_and_scoped_where()
    {
        using var db = await NewSchemaAsync();
        await Seed(db, At(1, 0, 10d), At(1, 1, 20d), At(2, 0, 40d));

        var grouped = (IGroupedReadRepository<Reading>)ReadingRepo(db);
        var groups = (await grouped.GroupByAsync(x => new { x.SensorId, x.At },
                g => new { g.Key.SensorId, g.Key.At, Mean = g.Average(x => x.Value) },
                options: new QueryOptions<Reading>().Where(x => x.SensorId == 1)))
            .OrderBy(x => x.At).ToList();

        Assert.That(groups, Has.Count.EqualTo(2), "the partition-scoped WHERE applied before grouping");
        Assert.That(groups.Select(x => x.Mean), Is.EqualTo(new[] { 10d, 20d }),
            "AVG cast to double on the cluster, the composite key materialized member by member");
    }

    [Test]
    public async Task Having_filters_groups_with_cluster_computed_aggregates()
    {
        using var db = await NewSchemaAsync();
        await Seed(db, At(1, 0, 10d), At(1, 1, 20d), At(2, 0, 40d), At(3, 0, 1d));

        var grouped = (IGroupedReadRepository<Reading>)ReadingRepo(db);

        var big = (await grouped.GroupByAsync(x => x.SensorId, g => new { g.Key, Total = g.Sum(x => x.Value) },
                having: g => g.Sum(x => x.Value) > 15d))
            .OrderBy(x => x.Key).ToList();
        Assert.That(big.Select(x => x.Key), Is.EqualTo(new[] { 1, 2 }));

        var busy = await grouped.GroupByAsync(x => x.SensorId, g => new { g.Key, Total = g.Sum(x => x.Value) },
            having: g => g.Count() >= 2);
        Assert.That(busy.Single().Key, Is.EqualTo(1),
            "the HAVING aggregate not in the projection was computed on the cluster as an extra cell");
    }

    [Test]
    public async Task Group_keys_outside_the_primary_key_are_rejected_with_guidance()
    {
        using var db = await NewSchemaAsync();
        var grouped = (IGroupedReadRepository<Reading>)ReadingRepo(db);

        Assert.That(async () => await grouped.GroupByAsync(x => x.Quality, g => new { g.Key, Rows = g.Count() }),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("primary key"),
            "Cassandra can only group by the primary key");
        Assert.That(async () => await grouped.GroupByAsync(x => x.At, g => new { g.Key, Rows = g.Count() }),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("partition key"),
            "a clustering column alone is not a valid CQL grouping");
    }

    [Test]
    public async Task Residual_filter_degrades_to_gated_client_grouping()
    {
        using var db = await NewSchemaAsync();
        await Seed(db, At(1, 0, 10d, "good"), At(1, 1, 20d, "best"), At(2, 0, 40d, "fine"));

        var grouped = (IGroupedReadRepository<Reading>)ReadingRepo(db);

        Assert.That(async () => await grouped.GroupByAsync(x => x.SensorId, g => new { g.Key, Rows = g.Count() },
                options: new QueryOptions<Reading>().Where(x => x.Quality.Length > 2)),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("AllowClientEvaluation"));

        var groups = (await grouped.GroupByAsync(x => x.SensorId, g => new { g.Key, Total = g.Sum(x => x.Value) },
                having: g => g.Count() >= 1,
                options: new QueryOptions<Reading>().Where(x => x.Quality.Length > 2)
                    .AllowClientEvaluation().AllowFiltering()))
            .OrderBy(x => x.Key).ToList();

        Assert.That(groups.Select(x => (x.Key, x.Total)), Is.EqualTo(new[] { (1, 30d), (2, 40d) }),
            "the gated fallback grouped the fetched rows with the selectors themselves");
    }
}
