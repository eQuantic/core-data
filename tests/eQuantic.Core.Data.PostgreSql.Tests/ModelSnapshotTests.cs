using eQuantic.Core.Data.Evolution;
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Relational.Evolution;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>
///     A snapshot is the other half of a comparison, so what matters is that it describes the model faithfully
///     and that describing the same model twice produces the same file — otherwise every regeneration would show
///     up as a change in review.
/// </summary>
[TestFixture]
public sealed class ModelSnapshotTests
{
    private sealed class Invoice : IEntity<Guid>
    {
        public Guid Id { get; set; }

        [Facet(Length = 64)]
        public string Reference { get; set; } = "";

        [Facet(Precision = 18, Scale = 2)]
        public decimal Total { get; set; }

        public string? Notes { get; set; }

        [ClusteringKey(Descending = true)]
        public DateTime IssuedAt { get; set; }

        [SearchIndex]
        public string Description { get; set; } = "";

        [ConcurrencyToken]
        public int Version { get; set; }

        public Guid GetKey() => Id;
        public void SetKey(Guid key) => Id = key;
    }

    private static ModelSnapshot Describe()
    {
        var dialect = new PostgreSqlDialect();
        var builder = new RelationalModelBuilder(dialect);
        builder.Entity<Invoice>(entity => entity.Table("invoices"));
        return new RelationalModelSnapshotSource(builder.Build(), dialect).Describe();
    }

    [Test]
    public void It_records_the_store_it_came_from()
    {
        Assert.That(Describe().Provider, Is.EqualTo("postgresql"));
    }

    [Test]
    public void It_records_the_collection_and_its_members()
    {
        var entity = Describe().For(typeof(Invoice).FullName!);

        Assert.That(entity, Is.Not.Null);
        Assert.That(entity!.Collection, Is.EqualTo("invoices"));
        Assert.That(entity.Fields.Select(field => field.Member),
            Is.EquivalentTo(new[] { "Id", "Reference", "Total", "Notes", "IssuedAt", "Description", "Version" }));
    }

    [Test]
    public void It_records_the_stored_name_a_member_maps_to()
    {
        var issued = Describe().For(typeof(Invoice).FullName!)!.Field("IssuedAt");

        Assert.That(issued!.Name, Is.EqualTo("issued_at"), "the dialect's naming convention is part of what was stored");
    }

    [Test]
    public void It_records_the_facets_that_size_a_column()
    {
        var entity = Describe().For(typeof(Invoice).FullName!)!;

        Assert.That(entity.Field("Reference")!.Length, Is.EqualTo(64));
        Assert.That(entity.Field("Total")!.Precision, Is.EqualTo(18));
        Assert.That(entity.Field("Total")!.Scale, Is.EqualTo(2));
    }

    [Test]
    public void It_distinguishes_a_member_that_accepts_null()
    {
        var entity = Describe().For(typeof(Invoice).FullName!)!;

        Assert.That(entity.Field("Notes")!.Nullable, Is.True);
        Assert.That(entity.Field("Total")!.Nullable, Is.False);
    }

    [Test]
    public void It_records_the_declarations_a_comparison_acts_on()
    {
        var entity = Describe().For(typeof(Invoice).FullName!)!;

        Assert.That(entity.Keys, Is.EqualTo(new[] { "Id" }));
        Assert.That(entity.ConcurrencyField, Is.EqualTo("Version"));
        Assert.That(entity.Clustering.Single().Member, Is.EqualTo("IssuedAt"));
        Assert.That(entity.Clustering.Single().Descending, Is.True);
        Assert.That(entity.Search.Single().Member, Is.EqualTo("Description"));
    }

    [Test]
    public void Describing_the_same_model_twice_writes_the_same_file()
    {
        var first = ModelSnapshotWriter.Write(Describe(), "Sample.Migrations");
        var second = ModelSnapshotWriter.Write(Describe(), "Sample.Migrations");

        Assert.That(second, Is.EqualTo(first),
            "an unchanged model must not show up as a change in review");
    }

    [Test]
    public void The_written_file_carries_what_the_model_declared()
    {
        var text = ModelSnapshotWriter.Write(Describe(), "Sample.Migrations");

        Assert.That(text, Does.Contain("public sealed class DataModelSnapshot : IModelSnapshotFile"));
        Assert.That(text, Does.Contain("\"invoices\""));
        Assert.That(text, Does.Contain("Length = 64"));
        Assert.That(text, Does.Contain("ConcurrencyField = \"Version\""));
        Assert.That(text, Does.Contain("new ClusteringSnapshot(\"IssuedAt\", true)"));
    }
}
