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

    /// <summary>An enum stored as its number unless the model declares it with <c>Enum&lt;TEnum&gt;()</c>.</summary>
    public enum Stage
    {
        Draft,
        Published,
    }

    /// <summary>An enum stored as its labels wherever it is used, by the annotation on it.</summary>
    [EnumType]
    public enum Tier
    {
        Basic,
        Gold,
    }

    private sealed class Release : IEntity<Guid>
    {
        public Guid Id { get; set; }

        public Stage Stage { get; set; }

        public Stage? Previous { get; set; }

        public List<Stage> History { get; set; } = [];

        public Tier Tier { get; set; }

        public long Number { get; set; }

        public DateTime StampedAt { get; set; }

        public DateTime IssuedAt { get; set; }

        public Guid GetKey() => Id;
        public void SetKey(Guid key) => Id = key;
    }

    private static ModelSnapshot DescribeRelease(bool labels = false, bool generated = false)
    {
        var dialect = new PostgreSqlDialect();
        var builder = new RelationalModelBuilder(dialect);
        if (labels)
        {
            builder.Enum<Stage>();
        }

        builder.Entity<Release>(entity =>
        {
            entity.Table("releases");
            if (generated)
            {
                entity.Generated(x => x.Number)
                    .Generated(x => x.StampedAt)
                    .Generated(x => x.IssuedAt, "now()");
            }
        });
        return new RelationalModelSnapshotSource(builder.Build(), dialect).Describe();
    }

    private static string Migration(ModelSnapshot before, ModelSnapshot after) =>
        MigrationWriter.Write(ModelDiffer.Compare(before, after), "Evolve", "Shop.Migrations",
            new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc));

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

    [Test]
    public void An_enum_stored_as_its_number_is_recorded_as_the_enum()
    {
        var entity = DescribeRelease().For(typeof(Release).FullName!)!;

        Assert.That(entity.Field("Stage")!.StoredType, Is.EqualTo(typeof(Stage).FullName));
        Assert.That(entity.Field("Previous")!.StoredType, Is.EqualTo(typeof(Stage).FullName));
    }

    [Test]
    public void An_enum_stored_as_its_labels_is_recorded_as_its_labels()
    {
        var entity = DescribeRelease(labels: true).For(typeof(Release).FullName!)!;

        Assert.That(entity.Field("Stage")!.StoredType, Is.EqualTo("labels:" + typeof(Stage).FullName),
            "the CLR type is the same either way; what the table holds is not");
        Assert.That(entity.Field("Previous")!.StoredType, Is.EqualTo("labels:" + typeof(Stage).FullName));
        Assert.That(entity.Field("History")!.StoredType, Is.EqualTo("labels:" + typeof(Stage).FullName + "[]"),
            "an array column of the enum holds its labels too");
        Assert.That(entity.Field("Tier")!.StoredType, Is.EqualTo("labels:" + typeof(Tier).FullName),
            "[EnumType] on an enum the model never declares fluently");
    }

    [Test]
    public void Switching_an_enum_to_its_labels_is_handed_back_instead_of_converted()
    {
        var difference = ModelDiffer.Compare(DescribeRelease(), DescribeRelease(labels: true));

        Assert.That(difference.Changes.Select(change => (change.Kind, change.Member)), Is.EquivalentTo(new[]
        {
            (ModelChangeKind.ConvertField, "History"),
            (ModelChangeKind.ConvertField, "Previous"),
            (ModelChangeKind.ConvertField, "Stage"),
        }));

        var migration = Migration(DescribeRelease(), DescribeRelease(labels: true));
        Assert.That(migration, Does.Not.Contain(".ConvertField("),
            "ALTER COLUMN … TYPE would cast the number, never map it to its label");
        Assert.That(migration.Split('\n').Count(line => line.StartsWith("#error ")), Is.EqualTo(3));
        Assert.That(migration, Does.Contain("CREATE TYPE … AS ENUM"));
        Assert.That(migration, Does.Contain("rewrites each number in the stored arrays as its label"));
    }

    [Test]
    public void A_store_generated_member_is_recorded_with_what_the_store_assigns()
    {
        var entity = DescribeRelease(generated: true).For(typeof(Release).FullName!)!;

        Assert.That(entity.Field("Number")!.Generated, Is.EqualTo("sequence"));
        Assert.That(entity.Field("StampedAt")!.Generated, Is.EqualTo("clock"));
        Assert.That(entity.Field("IssuedAt")!.Generated, Is.EqualTo("default now()"));
        Assert.That(entity.Field("Stage")!.Generated, Is.Null, "the application writes it");
    }

    [Test]
    public void Making_members_store_generated_is_handed_back_instead_of_ignored()
    {
        var difference = ModelDiffer.Compare(DescribeRelease(), DescribeRelease(generated: true));

        Assert.That(difference.Changes.Select(change => change.Kind), Is.All.EqualTo(ModelChangeKind.ChangeGeneration));
        Assert.That(difference.Changes.Select(change => change.Member),
            Is.EquivalentTo(new[] { "IssuedAt", "Number", "StampedAt" }));

        var migration = Migration(DescribeRelease(), DescribeRelease(generated: true));
        Assert.That(migration, Does.Not.Contain("migration.For<"), "no operation makes a column store-generated");
        Assert.That(migration, Does.Contain("ADD GENERATED BY DEFAULT AS IDENTITY (START WITH the largest value + 1)"));
        Assert.That(migration, Does.Contain("DEFAULT now() and NOT NULL"));
    }

    [Test]
    public void The_written_file_carries_the_labels_and_the_generation()
    {
        var text = ModelSnapshotWriter.Write(DescribeRelease(labels: true, generated: true), "Sample.Migrations");

        Assert.That(text, Does.Contain($"\"labels:{typeof(Stage).FullName}\""));
        Assert.That(text, Does.Contain("Generated = \"sequence\""));
        Assert.That(text, Does.Contain("Generated = \"default now()\""));
    }

    [Test]
    public void An_enum_stored_as_its_labels_records_every_label_with_its_member_and_number()
    {
        var entity = DescribeRelease(labels: true).For(typeof(Release).FullName!)!;

        Assert.That(entity.Field("Stage")!.Labels, Is.EqualTo(new[]
        {
            new LabelSnapshot("Draft", 0, "Draft"),
            new LabelSnapshot("Published", 1, "Published"),
        }));
        Assert.That(entity.Field("History")!.Labels, Has.Count.EqualTo(2), "an array of the enum holds the same labels");
        Assert.That(DescribeRelease().For(typeof(Release).FullName!)!.Field("Stage")!.Labels, Is.Empty,
            "an enum stored as its number has no labels to record");
    }

    private sealed class Counter : IEntity<long>
    {
        public long Id { get; set; }

        public string Label { get; set; } = "";

        public long GetKey() => Id;
        public void SetKey(long key) => Id = key;
    }

    private static ModelSnapshot DescribeCounter(bool generated)
    {
        var dialect = new PostgreSqlDialect();
        var builder = new RelationalModelBuilder(dialect);
        builder.Entity<Counter>(entity => entity.Table("counters").Key(x => x.Id, generated));
        return new RelationalModelSnapshotSource(builder.Build(), dialect).Describe();
    }

    [Test]
    public void A_key_the_store_starts_numbering_is_handed_back_instead_of_ignored()
    {
        var difference = ModelDiffer.Compare(DescribeCounter(generated: false), DescribeCounter(generated: true));

        Assert.That(difference.Changes.Single().Kind, Is.EqualTo(ModelChangeKind.ChangeKeyGeneration));
        Assert.That(Migration(DescribeCounter(generated: false), DescribeCounter(generated: true)),
            Does.Contain("ADD GENERATED BY DEFAULT AS IDENTITY (START WITH the largest key + 1)"));
    }
}
