using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>The reporting shape a raw query projects — nobody's entity, matched by column name.</summary>
public sealed class CategoryTotal
{
    public string Category { get; set; } = "";
    public long Orders { get; set; }
    public decimal Total { get; set; }
}

/// <summary>
///     Proves the bulk-load path (PostgreSQL binary <c>COPY</c>) and the typed raw-SQL escape hatch against a
///     real database: rows land with their lifecycle stamps, and arbitrary result shapes materialize by name
///     (snake_case included).
/// </summary>
[TestFixture]
public sealed class PostgreSqlBulkAndRawSqlTests : PostgreSqlIntegrationTest
{
    [Test]
    public async Task Bulk_insert_loads_through_copy_and_stamps_the_lifecycle()
    {
        using var db = await NewSchemaAsync();
        var uow = Uow(db);

        var articles = Enumerable.Range(0, 500)
            .Select(index => new Article { Title = $"bulk-{index:D3}" })
            .ToList();

        var loaded = await uow.BulkInsertAsync(articles);
        Assert.That(loaded, Is.EqualTo(500), "COPY reports the rows it streamed");

        var repo = db.Resolve<IAsyncRepository<Article, Guid>>();
        var found = await repo.GetFilteredAsync(x => x.Title.StartsWith("bulk-"));
        Assert.That(found.Count(), Is.EqualTo(500), "the rows are readable through the ordinary repository");
    }

    [Test]
    public async Task Bulk_insert_joins_an_open_transaction_and_rolls_back_with_it()
    {
        using var db = await NewSchemaAsync();
        var uow = Uow(db);

        await uow.BeginTransactionAsync();
        await uow.BulkInsertAsync(Enumerable.Range(0, 10).Select(index => new Article { Title = $"rolled-{index}" }));
        await uow.RollbackTransactionAsync();

        var repo = db.Resolve<IAsyncRepository<Article, Guid>>();
        Assert.That(await repo.CountAsync(new QueryOptions<Article>().Where(x => x.Title.StartsWith("rolled-"))),
            Is.Zero, "the bulk load was part of the transaction that rolled back");
    }

    [Test]
    public async Task Raw_sql_materializes_an_arbitrary_shape_by_column_name()
    {
        using var db = await NewSchemaAsync();
        var uow = Uow(db);
        var repo = db.Resolve<IAsyncRepository<SaleOrder, Guid>>();

        await repo.AddAsync(new SaleOrder { Id = Guid.NewGuid(), Customer = "ana", Total = 100m, Status = "open" });
        await repo.AddAsync(new SaleOrder { Id = Guid.NewGuid(), Customer = "ana", Total = 50m, Status = "open" });
        await repo.AddAsync(new SaleOrder { Id = Guid.NewGuid(), Customer = "bia", Total = 30m, Status = "open" });
        await uow.CommitAsync();

        var totals = await uow.QueryAsync<CategoryTotal>(
            """
            SELECT customer AS category, COUNT(*) AS orders, SUM(total) AS total
            FROM sale_orders WHERE total >= @p0 GROUP BY customer ORDER BY customer
            """,
            [30m]);

        Assert.That(totals.Select(row => row.Category), Is.EqualTo(new[] { "ana", "bia" }));
        Assert.That(totals[0].Orders, Is.EqualTo(2));
        Assert.That(totals[0].Total, Is.EqualTo(150m));
    }

    [Test]
    public async Task Raw_sql_binds_snake_case_columns_to_pascal_case_members()
    {
        using var db = await NewSchemaAsync();
        var repo = db.Resolve<IAsyncRepository<SaleOrder, Guid>>();
        await repo.AddAsync(new SaleOrder { Id = Guid.NewGuid(), Customer = "ana", Total = 10m, Status = "open" });
        await Uow(db).CommitAsync();

        var rows = await Uow(db).QueryAsync<CategoryTotal>(
            "SELECT customer AS category, 1::bigint AS orders, total FROM sale_orders");

        Assert.That(rows.Single().Category, Is.EqualTo("ana"));
        Assert.That(rows.Single().Total, Is.EqualTo(10m), "an unaliased column still binds its member");
    }

    [Test]
    public async Task Raw_execute_reports_affected_rows()
    {
        using var db = await NewSchemaAsync();
        var repo = db.Resolve<IAsyncRepository<Article, Guid>>();
        await repo.AddAsync(new Article { Title = "raw-exec" });
        await Uow(db).CommitAsync();

        var affected = await Uow(db).ExecuteAsync(
            "UPDATE articles SET title = @p0 WHERE title = @p1", ["raw-exec-updated", "raw-exec"]);

        Assert.That(affected, Is.EqualTo(1));
    }
}
