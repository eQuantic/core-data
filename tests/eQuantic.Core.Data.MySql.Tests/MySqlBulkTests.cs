using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.MySql.Tests;

/// <summary>Proves the MySQL bulk-load path (MySqlConnector's <c>MySqlBulkCopy</c>) against a real database.</summary>
[TestFixture]
public sealed class MySqlBulkTests : MySqlIntegrationTest
{
    [Test]
    public async Task Bulk_insert_loads_through_the_bulk_copy_api()
    {
        using var db = await NewSchemaAsync();

        var loaded = await Uow(db).BulkInsertAsync(Enumerable.Range(0, 250)
            .Select(index => new SaleOrder
            {
                Id = Guid.NewGuid(),
                Customer = $"bulk-{index:D3}",
                Status = "bulk",
                Total = index,
                Quantity = 1,
            }));

        Assert.That(loaded, Is.EqualTo(250));

        var repo = db.Resolve<IAsyncRepository<SaleOrder, Guid>>();
        Assert.That(await repo.CountAsync(new QueryOptions<SaleOrder>().Where(x => x.Status == "bulk")),
            Is.EqualTo(250), "the bulk-loaded rows read back through the ordinary repository");
    }
}
