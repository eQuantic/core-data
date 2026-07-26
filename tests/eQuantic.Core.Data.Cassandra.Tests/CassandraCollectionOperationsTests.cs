using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Cassandra.Migration;
using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>
///     What Cassandra will and will not do to a whole table. It drops one; it cannot rename one, and it has no
///     sized types to resize — and in both refusals it says what to do instead rather than failing obscurely.
/// </summary>
[TestFixture]
public sealed class CassandraCollectionOperationsTests : CassandraIntegrationTest
{
    private static async Task ApplyAsync(CassandraTestDatabase db, Action<IMigrationBuilder> configure)
    {
        var builder = new MigrationBuilder();
        configure(builder);
        await new CassandraMigrationExecutor(db.Resolve<ISession>(), db.Resolve<CassandraModel>())
            .ApplyAsync(builder.Operations);
    }

    private static async Task<bool> ExistsAsync(CassandraTestDatabase db, string table)
    {
        var session = db.Resolve<ISession>();
        var rows = await session.ExecuteAsync(new SimpleStatement(
            "SELECT table_name FROM system_schema.tables WHERE keyspace_name = ? AND table_name = ?",
            session.Keyspace, table));
        return rows.Any();
    }

    [Test]
    public async Task Dropping_a_table_removes_it()
    {
        using var db = await NewSchemaAsync();
        Assert.That(await ExistsAsync(db, "accounts"), Is.True);

        await ApplyAsync(db, migration => migration.For<Account>(a => a.DropCollection("accounts")));

        Assert.That(await ExistsAsync(db, "accounts"), Is.False);
    }

    [Test]
    public async Task Dropping_a_table_that_is_not_there_is_not_an_error()
    {
        using var db = await NewSchemaAsync();

        Assert.DoesNotThrowAsync(() =>
            ApplyAsync(db, migration => migration.For<Account>(a => a.DropCollection("never_existed"))),
            "a migration gets re-run; the second run must not fail on work the first one did");
    }

    [Test]
    public async Task Renaming_a_table_is_refused_with_what_to_do_instead()
    {
        using var db = await NewSchemaAsync();

        var failure = Assert.ThrowsAsync<NotSupportedException>(() =>
            ApplyAsync(db, migration => migration.For<Account>(a => a.RenameCollection("accounts", "accounts_v2"))));

        Assert.That(failure!.Message, Does.Contain("copy the data"),
            "a refusal that does not say what to do instead is just a wall");
    }

    [Test]
    public async Task Resizing_is_refused_because_there_is_nothing_to_resize()
    {
        using var db = await NewSchemaAsync();

        var failure = Assert.ThrowsAsync<NotSupportedException>(() =>
            ApplyAsync(db, migration => migration.For<Account>(a => a.ResizeField(x => x.Owner))));

        Assert.That(failure!.Message, Does.Contain("no sized types"));
    }
}
