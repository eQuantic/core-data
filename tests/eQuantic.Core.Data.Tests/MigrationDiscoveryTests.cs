using eQuantic.Core.Data.Migration;

namespace eQuantic.Core.Data.Tests;

/// <summary>
///     The shared migration resolution every provider's runner funnels through: explicit registrations
///     (AOT-safe, no reflection) merged with assembly scanning, ordered by timestamp, one instance per id —
///     and a loud failure when two different migrations claim the same id.
/// </summary>
[TestFixture]
public sealed class MigrationDiscoveryTests
{
    [Test]
    public void Explicit_registrations_resolve_without_scanning()
    {
        var source = new MigrationSource().Add<SecondMigration>().Add<FirstMigration>();

        var pending = MigrationDiscovery.Pending([], source);

        Assert.That(pending.Select(entry => entry.Attribute.Title),
            Is.EqualTo(new[] { "First", "Second" }), "registration order does not matter — the timestamp orders them");
        Assert.That(pending.All(entry => entry.Instance is not null), Is.True,
            "the registered instances are used as-is; nothing is activated by reflection");
    }

    [Test]
    public void The_same_migration_registered_twice_counts_once()
    {
        var source = new MigrationSource().Add<FirstMigration>().Add<FirstMigration>();

        var pending = MigrationDiscovery.Pending([], source);

        Assert.That(pending, Has.Count.EqualTo(1),
            "one entry per migration id — the same migration seen twice is not applied twice");
    }

    [Test]
    public void Two_different_migrations_sharing_an_id_throw()
    {
        var source = new MigrationSource().Add<FirstMigration>().Add<ClashingMigration>();

        var error = Assert.Throws<InvalidOperationException>(() => MigrationDiscovery.Pending([], source))!;
        Assert.That(error.Message, Does.Contain("share the id"));
    }

    [Test]
    public void An_instance_may_carry_constructor_arguments()
    {
        var source = new MigrationSource().Add(new ParameterizedMigration("configured"));

        var pending = MigrationDiscovery.Pending([], source);

        Assert.That(((ParameterizedMigration)pending.Single(entry => entry.Attribute.Title == "Parameterized").Instance).Tag,
            Is.EqualTo("configured"));
    }
}

[Migration("First", 2026, 1, 1, 0, 0, 0)]
public sealed class FirstMigration : eQuantic.Core.Data.Migration.Migration
{
    public override void Up(IMigrationBuilder migration)
    {
    }
}

[Migration("Second", 2026, 1, 2, 0, 0, 0)]
public sealed class SecondMigration : eQuantic.Core.Data.Migration.Migration
{
    public override void Up(IMigrationBuilder migration)
    {
    }
}

/// <summary>Same title and timestamp as <see cref="FirstMigration" /> — therefore the same id.</summary>
[Migration("First", 2026, 1, 1, 0, 0, 0)]
public sealed class ClashingMigration : eQuantic.Core.Data.Migration.Migration
{
    public override void Up(IMigrationBuilder migration)
    {
    }
}

[Migration("Parameterized", 2026, 1, 3, 0, 0, 0)]
public sealed class ParameterizedMigration(string tag) : eQuantic.Core.Data.Migration.Migration
{
    public string Tag { get; } = tag;

    public override void Up(IMigrationBuilder migration)
    {
    }
}
