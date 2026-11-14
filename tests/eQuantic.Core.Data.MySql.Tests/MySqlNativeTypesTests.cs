using System.Data.Common;
using eQuantic.Core.Data.MySql.Extensions;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.MySql.Tests;

/// <summary>
///     Enums stored as labels, JSON members and store-generated members on MySQL: an <c>ENUM(...)</c> column, a
///     <c>JSON</c> column, <c>AUTO_INCREMENT</c> and <c>UTC_TIMESTAMP(6)</c> read back by the row's key in the
///     same batch (MySQL has no <c>RETURNING</c>).
/// </summary>
[TestFixture]
public sealed class MySqlNativeTypesTests : MySqlIntegrationTest
{
    private static IAsyncRepository<RunRecord, Guid> Runs(MySqlTestDatabase db) =>
        db.Resolve<IAsyncRepository<RunRecord, Guid>>();

    private static Func<string, Task<object?>> Scalar(MySqlTestDatabase db) => async sql =>
    {
        await using var command = db.Resolve<DbDataSource>().CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    };

    [Test]
    public async Task The_migration_creates_an_enum_column_and_a_json_column()
    {
        using var db = await NewSchemaAsync();
        var scalar = Scalar(db);

        Assert.That(await scalar("SELECT column_type FROM information_schema.columns WHERE table_schema = database() " +
                                 "AND table_name = 'run_record' AND column_name = 'state'"),
            Is.EqualTo("enum('Queued','in_progress','Completed')"), "the column holds the labels and nothing else");
        Assert.That(await scalar("SELECT column_type FROM information_schema.columns WHERE table_schema = database() " +
                                 "AND table_name = 'run_record' AND column_name = 'payload'"), Is.EqualTo("json"));
        Assert.That(await scalar("SELECT extra FROM information_schema.columns WHERE table_schema = database() " +
                                 "AND table_name = 'run_record' AND column_name = 'sequence'"), Is.EqualTo("auto_increment"));

        Assert.That(async () => await scalar($"INSERT INTO run_record (id, state, channel, day) VALUES ('{Guid.NewGuid()}', 'Lost', 'Sms', 1)"),
            Throws.InstanceOf<DbException>(), "a value outside the labels is refused by the store");
    }

    [Test]
    public async Task Enums_are_stored_as_labels()
    {
        using var db = await NewSchemaAsync();
        await NativeTypesScenarios.EnumsAreStoredAsLabels(Runs(db), () => Uow(db).CommitAsync(), Scalar(db));
    }

    [Test]
    public async Task Json_members_round_trip()
    {
        using var db = await NewSchemaAsync();
        await NativeTypesScenarios.JsonMembersRoundTrip(Runs(db), () => Uow(db).CommitAsync());
    }

    [Test]
    public async Task Store_generated_members_are_read_back_by_the_key()
    {
        using var db = await NewSchemaAsync();
        await NativeTypesScenarios.StoreGeneratedMembersAreTheStores(Runs(db), () => Uow(db).CommitAsync(), Scalar(db));
    }

    [Test]
    public async Task A_bulk_load_leaves_store_generated_members_to_the_store()
    {
        using var db = await NewSchemaAsync();

        var loaded = await Uow(db).BulkInsertAsync(Enumerable.Range(0, 5).Select(_ => new RunRecord { State = RunState.InProgress }));

        Assert.That(loaded, Is.EqualTo(5L));
        Assert.That(Convert.ToInt64(await Scalar(db)("SELECT count(DISTINCT sequence) FROM run_record WHERE sequence > 0 " +
                                                    "AND state = 'in_progress' AND recorded_at IS NOT NULL")), Is.EqualTo(5L));
    }

    [Test]
    public void A_generated_key_beside_store_generated_members_is_refused_at_model_build()
    {
        // The model is built when the database is registered, so the refusal comes before any connection.
        Assert.That(() => new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddMySqlDatabase(
                "Server=unused", model => model.Entity<Receipt>(entity => entity
                    .Key(x => x.Id, generated: true)
                    .Generated(x => x.Number))),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("reads it back by the row's key"));
    }
}
