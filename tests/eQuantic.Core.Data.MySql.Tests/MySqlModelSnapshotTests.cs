using eQuantic.Core.Data.Evolution;
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.MySql.Extensions;
using eQuantic.Core.Data.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.MySql.Tests;

/// <summary>
///     What the MySQL and MariaDB snapshots record of an enum stored as its labels and of a store-generated member,
///     and what a generated migration does when either changes. MySQL is where a generated conversion would do the
///     most harm: an <c>ENUM</c> changed to or from a number reads the label's position, counted from 1, which is
///     neither the member's value nor its label.
/// </summary>
[TestFixture]
public sealed class MySqlModelSnapshotTests
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
    private static ModelSnapshot Describe(string system, bool labels = false, bool generated = false)
    {
        void Configure(RelationalModelBuilder builder)
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
        }

        var services = new ServiceCollection();
        const string connection = "Server=localhost;Database=snapshots;User ID=root";
        if (system == "mariadb")
        {
            services.AddMariaDbDatabase(connection, Configure);
        }
        else
        {
            services.AddMySqlDatabase(connection, Configure);
        }

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IModelSnapshotSource>().Describe();
    }

    private static string Migration(ModelSnapshot before, ModelSnapshot after) =>
        MigrationWriter.Write(ModelDiffer.Compare(before, after), "Evolve", "Shop.Migrations",
            new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc));

    private static readonly string[] Systems = ["mysql", "mariadb"];

    [TestCaseSource(nameof(Systems))]
    public void An_enum_is_recorded_as_its_number_or_as_its_labels(string system)
    {
        Assert.That(Describe(system).Entities.Single().Field("Stage")!.StoredType,
            Is.EqualTo(typeof(Stage).FullName));
        Assert.That(Describe(system, labels: true).Entities.Single().Field("Stage")!.StoredType,
            Is.EqualTo("labels:" + typeof(Stage).FullName));
        Assert.That(Describe(system, labels: true).Entities.Single().Field("Stage")!.Labels.Select(label => label.Label),
            Is.EqualTo(new[] { "Draft", "Published" }), "the labels the ENUM(...) lists, in its order");
    }

    [TestCaseSource(nameof(Systems))]
    public void Switching_an_enum_to_its_labels_is_handed_back_instead_of_converted(string system)
    {
        var before = Describe(system);
        var after = Describe(system, labels: true);

        var change = ModelDiffer.Compare(before, after).Changes.Single();
        Assert.That(change.Kind, Is.EqualTo(ModelChangeKind.ConvertField));

        var migration = Migration(before, after);
        Assert.That(migration, Does.Not.Contain(".ConvertField("));
        Assert.That(migration, Does.Contain("#error 'eQuantic.Core.Data.MySql.Tests.MySqlModelSnapshotTests+Release.Stage' " +
                                            "is now stored as the labels of"));
        Assert.That(migration, Does.Contain("read as the position of a label, counted from 1"));
    }

    [TestCaseSource(nameof(Systems))]
    public void Switching_an_enum_back_to_its_numbers_is_handed_back_instead_of_converted(string system)
    {
        var migration = Migration(Describe(system, labels: true), Describe(system));

        Assert.That(migration, Does.Not.Contain(".ConvertField("));
        Assert.That(migration, Does.Contain("rewrites each stored label as its number"));
    }

    [TestCaseSource(nameof(Systems))]
    public void A_store_generated_member_is_recorded_and_its_change_handed_back(string system)
    {
        var after = Describe(system, generated: true);
        var entity = after.Entities.Single();
        Assert.That(entity.Field("Number")!.Generated, Is.EqualTo("sequence"));
        Assert.That(entity.Field("StampedAt")!.Generated, Is.EqualTo("clock"));

        var migration = Migration(Describe(system), after);
        Assert.That(migration, Does.Not.Contain("migration.For<"));
        Assert.That(migration, Does.Contain("DEFAULT (UTC_TIMESTAMP(6)) and NOT NULL"));
        Assert.That(migration, Does.Contain(after.Provider == "mariadb"
            ? "CREATE SEQUENCE … START WITH the largest value + 1"
            : "sets the table's AUTO_INCREMENT to the largest value already stored + 1"));
    }
}
