using System.Data.Common;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.SqlServer.Tests;

/// <summary>
///     Enums stored as labels, JSON members and store-generated members on SQL Server, which has no enum or JSON
///     type: the labels and the JSON live in text held to them by a <c>CHECK</c>, a sequence object numbers the
///     rows (the one identity column a table may have can be the key), and <c>OUTPUT INSERTED</c> reads it all back.
/// </summary>
[TestFixture]
public sealed class SqlServerNativeTypesTests : SqlServerIntegrationTest
{
    private static IAsyncRepository<RunRecord, Guid> Runs(SqlServerTestDatabase db) =>
        db.Resolve<IAsyncRepository<RunRecord, Guid>>();

    private static Func<string, Task<object?>> Scalar(SqlServerTestDatabase db) => async sql =>
    {
        await using var command = db.Resolve<DbDataSource>().CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    };

    [Test]
    public async Task The_store_holds_the_text_to_the_labels_and_to_json()
    {
        using var db = await NewSchemaAsync();
        var scalar = Scalar(db);

        Assert.That(async () => await scalar($"INSERT INTO run_record (id, state, channel, day) VALUES ('{Guid.NewGuid()}', 'Lost', 'Sms', 1)"),
            Throws.InstanceOf<DbException>(), "a value outside the labels is refused by the CHECK");
        Assert.That(async () => await scalar($"INSERT INTO run_record (id, state, channel, day, payload) VALUES ('{Guid.NewGuid()}', 'Queued', 'Sms', 1, 'not json')"),
            Throws.InstanceOf<DbException>(), "text that is not JSON is refused by the ISJSON check");
        Assert.That(await scalar("SELECT count(*) FROM sys.sequences WHERE name = 'run_record_sequence_seq'"), Is.EqualTo(1));
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
    public async Task Store_generated_members_are_read_back_with_output_inserted()
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
        Assert.That(await Scalar(db)("SELECT count(DISTINCT sequence) FROM run_record WHERE sequence > 0 " +
                                    "AND state = 'in_progress' AND recorded_at IS NOT NULL"), Is.EqualTo(5));
    }

    [Test]
    public async Task A_generated_key_is_read_back_with_the_store_generated_members_after_it()
    {
        using var db = await NewSchemaAsync();
        var repo = db.Resolve<IAsyncRepository<Receipt, long>>();

        var first = new Receipt { Note = "first" };
        var second = new Receipt { Note = "second" };
        await repo.AddAsync(first);
        await repo.AddAsync(second);
        await Uow(db).CommitAsync();

        Assert.That(first.Id, Is.GreaterThan(0L));
        Assert.That(first.Number, Is.GreaterThan(0L), "the sequence object numbered it beside the identity key");
        Assert.That(second.Number, Is.Not.EqualTo(first.Number));
        Assert.That(first.IssuedAt, Is.Not.EqualTo(default(DateTime)));
        Assert.That((await repo.GetAsync(second.Id))!.Number, Is.EqualTo(second.Number));
    }
}
