using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>
///     Proves composite keys, facets and the ordered-read declaration against a real PostgreSQL: the schema
///     carries the two-column primary key, the sized types and the clustering index, and the repository
///     addresses rows by key tuples end to end.
/// </summary>
[TestFixture]
public sealed class PostgreSqlCompositeKeyTests : PostgreSqlIntegrationTest
{
    [Test]
    public async Task Composite_key_addresses_rows_end_to_end()
    {
        using var db = await NewSchemaAsync();
        var repo = db.Resolve<IAsyncRepository<OrderLine, (Guid, int)>>();

        var order = Guid.NewGuid();
        await repo.AddAsync(new OrderLine { OrderId = order, LineNo = 1, Product = "Keyboard", Amount = 49.90m });
        await repo.AddAsync(new OrderLine { OrderId = order, LineNo = 2, Product = "Mouse", Amount = 19.90m });
        await Uow(db).CommitAsync();

        var second = await repo.GetAsync((order, 2));
        Assert.That(second?.Product, Is.EqualTo("Mouse"), "the point lookup addresses the row by its key tuple");

        second!.Amount = 24.90m;
        await repo.ModifyAsync(second);
        await Uow(db).CommitAsync();
        Assert.That((await repo.GetAsync((order, 2)))!.Amount, Is.EqualTo(24.90m),
            "the update's WHERE spans both key columns");

        await repo.RemoveAsync(second);
        await Uow(db).CommitAsync();
        Assert.That(await repo.GetAsync((order, 2)), Is.Null, "the delete addressed only that line");
        Assert.That(await repo.GetAsync((order, 1)), Is.Not.Null, "its sibling under the same order survived");
    }

    [Test]
    public async Task The_schema_carries_the_composite_key_facets_and_clustering_index()
    {
        using var db = await NewSchemaAsync();
        var dataSource = db.Resolve<System.Data.Common.DbDataSource>();

        await using (var columns = dataSource.CreateCommand(
                         "SELECT column_name, data_type, character_maximum_length, numeric_precision, numeric_scale " +
                         "FROM information_schema.columns WHERE table_name = 'order_lines'"))
        await using (var reader = await columns.ExecuteReaderAsync())
        {
            var byName = new Dictionary<string, (string Type, object? Length, object? Precision, object? Scale)>();
            while (await reader.ReadAsync())
            {
                byName[reader.GetString(0)] = (reader.GetString(1), reader.GetValue(2), reader.GetValue(3), reader.GetValue(4));
            }

            Assert.That(byName["product"].Type, Is.EqualTo("character varying"), "[Facet(Length)] sized the text column");
            Assert.That(byName["product"].Length, Is.EqualTo(200));
            Assert.That(byName["amount"].Precision, Is.EqualTo(18), "[Facet(Precision/Scale)] sized the decimal column");
            Assert.That(byName["amount"].Scale, Is.EqualTo(2));
        }

        await using (var primary = dataSource.CreateCommand(
                         "SELECT string_agg(a.attname, ',' ORDER BY k.ordinality) FROM pg_constraint c " +
                         "JOIN LATERAL unnest(c.conkey) WITH ORDINALITY AS k(attnum, ordinality) ON true " +
                         "JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.attnum " +
                         "WHERE c.contype = 'p' AND c.conrelid = 'order_lines'::regclass"))
        {
            Assert.That(await primary.ExecuteScalarAsync(), Is.EqualTo("order_id,line_no"),
                "the primary key spans both columns, in the declared order");
        }

        await using var clustering = dataSource.CreateCommand(
            "SELECT indexdef FROM pg_indexes WHERE tablename = 'order_lines' AND indexname = 'ix_order_lines_clustering'");
        var definition = (string?)await clustering.ExecuteScalarAsync();
        Assert.That(definition, Is.Not.Null, "[ClusteringKey] materialized the ordered-read index");
        Assert.That(definition, Does.Contain("added_at DESC"), "the declared direction landed in the index");
    }
}
