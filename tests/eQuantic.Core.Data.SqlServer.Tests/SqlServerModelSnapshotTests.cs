using eQuantic.Core.Data.Evolution;
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.SqlServer.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.SqlServer.Tests;

/// <summary>
///     What the SQL Server snapshot records of an enum stored as its labels (text held to them by a <c>CHECK</c>)
///     and of a store-generated member (a sequence object, or the clock as a default), and what a generated
///     migration does when either changes.
/// </summary>
[TestFixture]
public sealed class SqlServerModelSnapshotTests
{
    /// <summary>An enum stored as its number unless the model declares it with <c>Enum&lt;TEnum&gt;()</c>.</summary>
    public enum Stage
    {
        Draft,
        Published,
    }

    private sealed class Release : IEntity<Guid>
    {
        public Guid Id { get; set; }

        public Stage Stage { get; set; }

        public long Number { get; set; }

        public DateTime StampedAt { get; set; }

        public Guid GetKey() => Id;
        public void SetKey(Guid key) => Id = key;
    }

    /// <summary>The snapshot the application's registration describes (nothing connects to describe a model).</summary>
    private static ModelSnapshot Describe(bool labels = false, bool generated = false)
    {
        var services = new ServiceCollection();
        services.AddSqlServerDatabase("Server=localhost;Database=snapshots;Trusted_Connection=True", builder =>
        {
            if (labels)
            {
                builder.Enum<Stage>();
            }

            builder.Entity<Release>(entity =>
            {
                entity.Table("releases");
                if (generated)
                {
                    entity.Generated(x => x.Number).Generated(x => x.StampedAt);
                }
            });
        });

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IModelSnapshotSource>().Describe();
    }

    private static string Migration(ModelSnapshot before, ModelSnapshot after) =>
        MigrationWriter.Write(ModelDiffer.Compare(before, after), "Evolve", "Shop.Migrations",
            new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc));

    [Test]
    public void An_enum_is_recorded_as_its_number_or_as_its_labels()
    {
        Assert.That(Describe().Entities.Single().Field("Stage")!.StoredType, Is.EqualTo(typeof(Stage).FullName));
        Assert.That(Describe(labels: true).Entities.Single().Field("Stage")!.StoredType,
            Is.EqualTo("labels:" + typeof(Stage).FullName));
    }

    [Test]
    public void Switching_an_enum_to_its_labels_is_handed_back_instead_of_converted()
    {
        var change = ModelDiffer.Compare(Describe(), Describe(labels: true)).Changes.Single();
        Assert.That(change.Kind, Is.EqualTo(ModelChangeKind.ConvertField));

        var migration = Migration(Describe(), Describe(labels: true));
        Assert.That(migration, Does.Not.Contain(".ConvertField("),
            "ALTER COLUMN to nvarchar would write the number 1 as '1', which the CHECK then refuses");
        Assert.That(migration, Does.Contain("adds the CHECK that holds it to the labels"));
    }

    [Test]
    public void Switching_an_enum_back_to_its_numbers_is_handed_back_instead_of_converted()
    {
        var migration = Migration(Describe(labels: true), Describe());

        Assert.That(migration, Does.Not.Contain(".ConvertField("));
        Assert.That(migration, Does.Contain("drops the CHECK holding the column to the labels"));
    }

    [Test]
    public void A_store_generated_member_is_recorded_and_its_change_handed_back()
    {
        var after = Describe(generated: true);
        Assert.That(after.Entities.Single().Field("Number")!.Generated, Is.EqualTo("sequence"));
        Assert.That(after.Entities.Single().Field("StampedAt")!.Generated, Is.EqualTo("clock"));

        var migration = Migration(Describe(), after);
        Assert.That(migration, Does.Not.Contain("migration.For<"));
        Assert.That(migration, Does.Contain("<table>_<column>_seq starting above the largest value already stored"));
        Assert.That(migration, Does.Contain("DEFAULT SYSUTCDATETIME()"));
    }
}
