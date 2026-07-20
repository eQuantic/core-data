using eQuantic.Core.Data.Migration;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>
///     Unit tests for the fluent migration builder (core contracts) — that the typed authoring surface records
///     the right provider-agnostic operations. No database required.
/// </summary>
[TestFixture]
public sealed class MigrationBuilderTests
{
    [Test]
    public void For_records_each_operation_in_order()
    {
        var builder = new MigrationBuilder();
        builder.For<Product>(product => product
            .EnsureCollection()
            .Index(x => x.Category)
            .CompositeIndex(keys => keys.Descending(x => x.Price).Ascending(x => x.Name), unique: true)
            .ConvertField(x => x.Quantity, MigrationFieldType.String, MigrationFieldType.Int32)
            .RenameField(x => x.Name, "DisplayName")
            .Update(x => x.Category == "old", update => update.Set(x => x.Category, "new")));

        Assert.That(builder.Operations.Select(operation => operation.GetType()), Is.EqualTo(new[]
        {
            typeof(EnsureCollectionOperation),
            typeof(EnsureIndexOperation),
            typeof(EnsureIndexOperation),
            typeof(ConvertFieldOperation),
            typeof(RenameFieldOperation),
            typeof(UpdateOperation),
        }));
    }

    [Test]
    public void Index_records_uniqueness_direction_and_entity()
    {
        var builder = new MigrationBuilder();
        builder.For<Product>(product => product.Index(x => x.Category, descending: true, unique: true));

        var index = (EnsureIndexOperation)builder.Operations.Single();
        Assert.That(index.Unique, Is.True);
        Assert.That(index.Keys.Single().Descending, Is.True);
        Assert.That(index.EntityType, Is.EqualTo(typeof(Product)));
    }

    [Test]
    public void CompositeIndex_records_keys_in_declared_order()
    {
        var builder = new MigrationBuilder();
        builder.For<Product>(product => product.CompositeIndex(keys => keys.Descending(x => x.Price).Ascending(x => x.Name)));

        var index = (EnsureIndexOperation)builder.Operations.Single();
        Assert.That(index.Keys, Has.Count.EqualTo(2));
        Assert.That(index.Keys[0].Descending, Is.True);
        Assert.That(index.Keys[1].Descending, Is.False);
    }

    [Test]
    public void ConvertField_records_the_from_and_to_types()
    {
        var builder = new MigrationBuilder();
        builder.For<Product>(product => product.ConvertField(x => x.Quantity, MigrationFieldType.String, MigrationFieldType.Int32));

        var convert = (ConvertFieldOperation)builder.Operations.Single();
        Assert.That(convert.From, Is.EqualTo(MigrationFieldType.String));
        Assert.That(convert.To, Is.EqualTo(MigrationFieldType.Int32));
    }

    [Test]
    public void Update_records_the_predicate_and_the_assignments()
    {
        var builder = new MigrationBuilder();
        builder.For<Product>(product => product.Update(
            x => x.Category == "old",
            update => update.Set(x => x.Category, "new").Set(x => x.Quantity, 0)));

        var update = (UpdateOperation)builder.Operations.Single();
        Assert.That(update.Predicate, Is.Not.Null);
        Assert.That(update.Sets, Has.Count.EqualTo(2));
    }

    [Test]
    public void Run_records_a_run_operation()
    {
        var builder = new MigrationBuilder();
        builder.Run((_, _) => Task.CompletedTask);

        Assert.That(builder.Operations.Single(), Is.TypeOf<RunOperation>());
    }
}
