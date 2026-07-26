using eQuantic.Core.Data.Evolution;

namespace eQuantic.Core.Data.Tests;

/// <summary>
///     What a comparison must get right, because the cost of getting it wrong is data: telling a rename from a
///     drop-and-add, refusing to add a member without saying what existing records hold, and refusing outright
///     the changes a store cannot make.
/// </summary>
[TestFixture]
public sealed class ModelDifferTests
{
    private const string Order = "Shop.Order";

    private static FieldSnapshot Field(string member, string name, string type = "System.String",
        bool nullable = false, string? defaultLiteral = null, params string[] previousNames) =>
        new(member, name, type)
        {
            Nullable = nullable,
            DefaultLiteral = defaultLiteral,
            PreviousNames = previousNames,
        };

    private static ModelSnapshot Snapshot(EntitySnapshot entity, string provider = "postgresql") =>
        new(provider, [entity]);

    private static EntitySnapshot Entity(params FieldSnapshot[] fields) =>
        new(Order, "orders", fields) { Keys = ["Id"] };

    // ---- nothing to do -----------------------------------------------------------------------------

    [Test]
    public void A_model_that_did_not_move_produces_nothing()
    {
        var snapshot = Snapshot(Entity(Field("Id", "id", "System.Guid")));

        var difference = ModelDiffer.Compare(snapshot, snapshot);

        Assert.That(difference.IsEmpty, Is.True);
    }

    [Test]
    public void The_first_comparison_maps_everything_as_new()
    {
        var after = Snapshot(Entity(Field("Id", "id", "System.Guid")));

        var difference = ModelDiffer.Compare(ModelSnapshot.Empty("postgresql"), after);

        Assert.That(difference.Changes.Single().Kind, Is.EqualTo(ModelChangeKind.AddCollection));
    }

    // ---- adding a member ---------------------------------------------------------------------------

    [Test]
    public void Adding_a_member_without_a_declared_value_asks_for_one()
    {
        var before = Snapshot(Entity(Field("Id", "id", "System.Guid")));
        var after = Snapshot(Entity(Field("Id", "id", "System.Guid"), Field("Tier", "tier", "System.Int32")));

        var change = ModelDiffer.Compare(before, after).Changes.Single(c => c.Kind == ModelChangeKind.AddField);

        Assert.That(change.Member, Is.EqualTo("Tier"));
        Assert.That(change.NeedsValue, Is.True, "every existing record would silently take default(T)");
    }

    [Test]
    public void Adding_a_member_with_a_declared_value_carries_it()
    {
        var before = Snapshot(Entity(Field("Id", "id", "System.Guid")));
        var after = Snapshot(Entity(
            Field("Id", "id", "System.Guid"),
            Field("Tier", "tier", "System.Int32", defaultLiteral: "1")));

        var change = ModelDiffer.Compare(before, after).Changes.Single(c => c.Kind == ModelChangeKind.AddField);

        Assert.That(change.NeedsValue, Is.False);
        Assert.That(change.DefaultLiteral, Is.EqualTo("1"));
    }

    [Test]
    public void Adding_a_member_that_accepts_null_needs_no_value()
    {
        var before = Snapshot(Entity(Field("Id", "id", "System.Guid")));
        var after = Snapshot(Entity(Field("Id", "id", "System.Guid"), Field("Notes", "notes", nullable: true)));

        var change = ModelDiffer.Compare(before, after).Changes.Single(c => c.Kind == ModelChangeKind.AddField);

        Assert.That(change.NeedsValue, Is.False, "null is a real answer for a nullable member");
    }

    // ---- renames -----------------------------------------------------------------------------------

    [Test]
    public void The_same_member_stored_under_a_new_name_is_a_rename()
    {
        var before = Snapshot(Entity(Field("Customer", "customer")));
        var after = Snapshot(Entity(Field("Customer", "client_name")));

        var change = ModelDiffer.Compare(before, after).Changes.Single();

        Assert.That(change.Kind, Is.EqualTo(ModelChangeKind.RenameField));
        Assert.That(change.From, Is.EqualTo("customer"));
        Assert.That(change.To, Is.EqualTo("client_name"));
    }

    [Test]
    public void A_renamed_member_that_declares_where_it_came_from_is_a_rename()
    {
        var before = Snapshot(Entity(Field("Customer", "customer")));
        var after = Snapshot(Entity(Field("Buyer", "buyer", previousNames: "customer")));

        var difference = ModelDiffer.Compare(before, after);

        var change = difference.Changes.Single();
        Assert.That(change.Kind, Is.EqualTo(ModelChangeKind.RenameField));
        Assert.That(change.From, Is.EqualTo("customer"));
        Assert.That(change.To, Is.EqualTo("buyer"));
        Assert.That(difference.Changes.Any(c => c.Kind == ModelChangeKind.DropField), Is.False,
            "the values move with the rename instead of being dropped");
    }

    [Test]
    public void A_renamed_member_that_declares_nothing_becomes_a_flagged_drop_and_add()
    {
        var before = Snapshot(Entity(Field("Customer", "customer")));
        var after = Snapshot(Entity(Field("Buyer", "buyer")));

        var difference = ModelDiffer.Compare(before, after);
        var dropped = difference.Changes.Single(c => c.Kind == ModelChangeKind.DropField);

        Assert.That(difference.Changes.Any(c => c.Kind == ModelChangeKind.AddField), Is.True);
        Assert.That(dropped.AmbiguousRenameHint, Is.Not.Null);
        Assert.That(dropped.AmbiguousRenameHint, Does.Contain("PreviousName"));
    }

    [Test]
    public void A_plain_drop_is_not_flagged_as_an_ambiguous_rename()
    {
        var before = Snapshot(Entity(Field("Id", "id", "System.Guid"), Field("Legacy", "legacy")));
        var after = Snapshot(Entity(Field("Id", "id", "System.Guid")));

        var dropped = ModelDiffer.Compare(before, after).Changes.Single();

        Assert.That(dropped.Kind, Is.EqualTo(ModelChangeKind.DropField));
        Assert.That(dropped.AmbiguousRenameHint, Is.Null, "nothing appeared that it could have become");
    }

    // ---- type and size -----------------------------------------------------------------------------

    [Test]
    public void A_member_stored_as_a_different_type_is_a_conversion()
    {
        var before = Snapshot(Entity(Field("Total", "total", "System.Int32")));
        var after = Snapshot(Entity(Field("Total", "total", "System.Decimal")));

        var change = ModelDiffer.Compare(before, after).Changes.Single();

        Assert.That(change.Kind, Is.EqualTo(ModelChangeKind.ConvertField));
        Assert.That(change.From, Is.EqualTo("System.Int32"));
        Assert.That(change.To, Is.EqualTo("System.Decimal"));
    }

    [Test]
    public void Resizing_a_member_is_its_own_change()
    {
        var before = Snapshot(new EntitySnapshot(Order, "orders",
            [new FieldSnapshot("Reference", "reference", "System.String") { Length = 50 }]) { Keys = ["Id"] });
        var after = Snapshot(new EntitySnapshot(Order, "orders",
            [new FieldSnapshot("Reference", "reference", "System.String") { Length = 200 }]) { Keys = ["Id"] });

        var change = ModelDiffer.Compare(before, after).Changes.Single();

        Assert.That(change.Kind, Is.EqualTo(ModelChangeKind.ChangeFacets));
        Assert.That(change.To, Is.EqualTo("(200)"));
    }

    // ---- collections -------------------------------------------------------------------------------

    [Test]
    public void An_entity_stored_under_a_new_name_is_a_rename_not_a_replacement()
    {
        var before = Snapshot(new EntitySnapshot(Order, "orders", []) { Keys = ["Id"] });
        var after = Snapshot(new EntitySnapshot(Order, "sale_orders", []) { Keys = ["Id"] });

        var change = ModelDiffer.Compare(before, after).Changes.Single();

        Assert.That(change.Kind, Is.EqualTo(ModelChangeKind.RenameCollection));
        Assert.That(change.To, Is.EqualTo("sale_orders"));
    }

    [Test]
    public void An_entity_that_is_no_longer_mapped_is_dropped()
    {
        var before = Snapshot(Entity(Field("Id", "id", "System.Guid")));
        var after = ModelSnapshot.Empty("postgresql");

        var change = ModelDiffer.Compare(before, after).Changes.Single();

        Assert.That(change.Kind, Is.EqualTo(ModelChangeKind.DropCollection));
    }

    // ---- refusals ----------------------------------------------------------------------------------

    [Test]
    public void Cassandra_refuses_a_moved_partition_key()
    {
        var before = new ModelSnapshot("cassandra",
            [new EntitySnapshot(Order, "orders", []) { Keys = ["Id"], PartitionKeys = ["TenantId"] }]);
        var after = new ModelSnapshot("cassandra",
            [new EntitySnapshot(Order, "orders", []) { Keys = ["Id"], PartitionKeys = ["TenantId", "Region"] }]);

        var refusal = ModelDiffer.Compare(before, after).Refusals.Single();

        Assert.That(refusal.Reason, Does.Contain("partition"));
        Assert.That(refusal.Alternative, Does.Contain("copy the data"));
    }

    [Test]
    public void Cassandra_refuses_a_moved_clustering_key()
    {
        var before = new ModelSnapshot("cassandra",
            [new EntitySnapshot(Order, "orders", []) { Keys = ["Id"], Clustering = [new ClusteringSnapshot("At", false)] }]);
        var after = new ModelSnapshot("cassandra",
            [new EntitySnapshot(Order, "orders", []) { Keys = ["Id"], Clustering = [new ClusteringSnapshot("At", true)] }]);

        Assert.That(ModelDiffer.Compare(before, after).Refusals, Is.Not.Empty);
    }

    [Test]
    public void A_relational_store_refuses_a_redefined_key()
    {
        var before = Snapshot(new EntitySnapshot(Order, "orders", []) { Keys = ["Id"] });
        var after = Snapshot(new EntitySnapshot(Order, "orders", []) { Keys = ["TenantId", "Code"] });

        var refusal = ModelDiffer.Compare(before, after).Refusals.Single();

        Assert.That(refusal.Reason, Does.Contain("key changed"));
    }

    [Test]
    public void Comparing_across_stores_is_refused_outright()
    {
        var before = new ModelSnapshot("postgresql", [new EntitySnapshot(Order, "orders", [])]);
        var after = new ModelSnapshot("mongodb", [new EntitySnapshot(Order, "orders", [])]);

        Assert.That(() => ModelDiffer.Compare(before, after), Throws.InvalidOperationException);
    }
}
