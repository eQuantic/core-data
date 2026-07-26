using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>
///     The vocabulary a model comparison needs to tell a rename from a drop-and-add, and to know what existing
///     records should hold when a member arrives. Both are declarations about the model's <b>history</b>, which
///     the model itself cannot infer.
/// </summary>
[TestFixture]
public sealed class ModelEvolutionVocabularyTests
{
    private sealed class Annotated : IEntity<Guid>
    {
        public Guid Id { get; set; }

        [PreviousName("client_name")]
        public string Customer { get; set; } = "";

        [PreviousName("state")]
        [PreviousName("status_code")]
        public string Status { get; set; } = "";

        [DefaultValue("pending")]
        public string Stage { get; set; } = "";

        public decimal Total { get; set; }

        public Guid GetKey() => Id;
        public void SetKey(Guid key) => Id = key;
    }

    private sealed class Fluent : IEntity<Guid>
    {
        public Guid Id { get; set; }
        public string Customer { get; set; } = "";
        public int Tier { get; set; }
        public Guid GetKey() => Id;
        public void SetKey(Guid key) => Id = key;
    }

    private static RelationalEntityConfiguration Configure<TEntity>(Action<RelationalEntityBuilder<TEntity>> configure)
        where TEntity : class
    {
        var builder = new RelationalModelBuilder(new PostgreSqlDialect());
        builder.Entity(configure);
        return builder.Build().For(typeof(TEntity));
    }

    private static RelationalColumn Column(RelationalEntityConfiguration configuration, string member) =>
        configuration.Columns.Single(column => column.Property.Name == member);

    [Test]
    public void An_attribute_records_the_name_a_member_used_to_have()
    {
        var configuration = Configure<Annotated>(_ => { });

        Assert.That(Column(configuration, nameof(Annotated.Customer)).PreviousNames, Is.EqualTo(new[] { "client_name" }));
    }

    [Test]
    public void A_member_renamed_more_than_once_keeps_every_name()
    {
        var configuration = Configure<Annotated>(_ => { });

        Assert.That(Column(configuration, nameof(Annotated.Status)).PreviousNames, Is.EquivalentTo(new[] { "state", "status_code" }));
    }

    [Test]
    public void An_attribute_records_what_existing_records_should_hold()
    {
        var configuration = Configure<Annotated>(_ => { });

        Assert.That(Column(configuration, nameof(Annotated.Stage)).DefaultValue, Is.EqualTo("pending"));
    }

    [Test]
    public void A_member_that_declares_neither_says_so()
    {
        var configuration = Configure<Annotated>(_ => { });
        var total = Column(configuration, nameof(Annotated.Total));

        Assert.That(total.PreviousNames, Is.Empty);
        Assert.That(total.DefaultValue, Is.Null, "the engine never invents the value existing records take");
    }

    [Test]
    public void The_fluent_surface_declares_the_same_things()
    {
        var configuration = Configure<Fluent>(entity => entity
            .PreviousName(x => x.Customer, "client_name")
            .Default(x => x.Tier, 1));

        Assert.That(Column(configuration, nameof(Fluent.Customer)).PreviousNames, Is.EqualTo(new[] { "client_name" }));
        Assert.That(Column(configuration, nameof(Fluent.Tier)).DefaultValue, Is.EqualTo(1));
    }

    [Test]
    public void Fluent_renames_accumulate()
    {
        var configuration = Configure<Fluent>(entity => entity
            .PreviousName(x => x.Customer, "client_name")
            .PreviousName(x => x.Customer, "buyer"));

        Assert.That(Column(configuration, nameof(Fluent.Customer)).PreviousNames, Is.EquivalentTo(new[] { "client_name", "buyer" }));
    }
}
