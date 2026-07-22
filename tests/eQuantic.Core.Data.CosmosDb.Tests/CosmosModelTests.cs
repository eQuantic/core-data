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

    [Test]
    public void EntityKey_annotation_declares_the_document_id()
    {
        var configuration = new CosmosModelBuilder()
            .Entity<AnnotatedTicket>(_ => { })
            .Build()
            .For(typeof(AnnotatedTicket));

        Assert.That(configuration.GetId(new AnnotatedTicket { Code = "T-42", Region = "br" }), Is.EqualTo("T-42"),
            "[EntityKey] picked the id member — no fluent Id(...) needed");
        Assert.That(configuration.IdDescription, Does.Contain("Code").And.Contain("EntityKey"));
    }

    [Test]
    public void Explain_reports_the_mapping_decisions()
    {
        var model = new CosmosModelBuilder()
            .Entity<AnnotatedTicket>(_ => { })
            .Build();

        var report = model.Explain();
        Assert.That(report, Does.Contain("container \"annotated_tickets\""));
        Assert.That(report, Does.Contain("partition key: \"/region\""));
        Assert.That(report, Does.Contain("id: Code ([EntityKey])"));
        Assert.That(report, Does.Contain("default TTL: 3600s"));
        Assert.That(report, Does.Contain("concurrency token: _etag"));
    }
}

/// <summary>An entity modeled entirely by the store-neutral annotations — id, partition, TTL and concurrency.</summary>
[eQuantic.Core.Data.Modeling.Entity("annotated_tickets")]
[eQuantic.Core.Data.Modeling.TimeToLive(3600)]
public sealed class AnnotatedTicket
{
    [eQuantic.Core.Data.Modeling.EntityKey]
    public string Code { get; set; } = "";

    [eQuantic.Core.Data.Modeling.PartitionKey]
    public string Region { get; set; } = "";

    [eQuantic.Core.Data.Modeling.ConcurrencyToken]
    [System.Text.Json.Serialization.JsonPropertyName("_etag")]
    public string? ETag { get; set; }
}
