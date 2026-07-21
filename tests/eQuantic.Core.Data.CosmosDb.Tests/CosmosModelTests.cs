namespace eQuantic.Core.Data.CosmosDb.Tests;

/// <summary>Unit tests for the Cosmos model builder — pure configuration, no emulator.</summary>
[TestFixture]
public sealed class CosmosModelTests
{
    private static CosmosEntityConfiguration Config() =>
        new CosmosModelBuilder()
            .Entity<CosmosProduct>(entity => entity
                .Container("products")
                .PartitionKey(x => x.Category)
                .ConcurrencyToken(x => x.ETag))
            .Build()
            .For(typeof(CosmosProduct));

    [Test]
    public void Concurrency_token_reads_the_entitys_etag()
    {
        var product = CosmosProduct.New("Keyboard", "Peripherals", 1, 1m);
        product.ETag = "\"0x1\"";

        Assert.That(Config().GetETag(product), Is.EqualTo("\"0x1\""));
    }

    [Test]
    public void Missing_etag_reads_as_null_and_keeps_the_upsert_path()
    {
        var product = CosmosProduct.New("Keyboard", "Peripherals", 1, 1m);

        Assert.That(Config().GetETag(product), Is.Null);
    }

    [Test]
    public void Entities_without_a_token_declaration_read_null()
    {
        var bare = new CosmosModelBuilder()
            .Entity<CosmosProduct>(entity => entity.PartitionKey(x => x.Category))
            .Build()
            .For(typeof(CosmosProduct));

        var product = CosmosProduct.New("Keyboard", "Peripherals", 1, 1m);
        product.ETag = "\"0x1\"";

        Assert.That(bare.GetETag(product), Is.Null);
    }
}
