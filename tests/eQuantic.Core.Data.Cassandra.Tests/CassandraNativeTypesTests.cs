using System.Text.Json;
using System.Text.Json.Nodes;
using eQuantic.Core.Data.Evolution;
using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using Microsoft.Extensions.DependencyInjection;
using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>A run's state, stored as its labels by the annotation on the enum.</summary>
[EnumType]
public enum RunState
{
    Queued,
    [StoredAs("in_progress")]
    InProgress,
    Completed,
}

/// <summary>A delivery channel, stored as its labels by the model builder's <c>Enum&lt;Channel&gt;()</c>.</summary>
public enum Channel
{
    Email,
    Sms,
}

/// <summary>A weekday, kept as a plain enum: stored as its number.</summary>
public enum Weekday
{
    Monday = 1,
    Tuesday = 2,
}

/// <summary>Enums stored as labels, JSON members, and members the store fills (by annotation), beside a client key.</summary>
public sealed class RunRecord : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public RunState State { get; set; }

    public RunState? Previous { get; set; }

    public Channel Channel { get; set; }

    public Weekday Day { get; set; }

    public JsonDocument? Payload { get; set; }

    public JsonNode? Steps { get; set; }

    public JsonElement? Meta { get; set; }

    [StoreGenerated]
    public long Sequence { get; set; }

    [StoreGenerated]
    public DateTime RecordedAt { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>Members the store fills, declared by the fluent model instead of annotations.</summary>
public sealed class Receipt : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Note { get; set; } = "";

    public int Number { get; set; }

    public DateTimeOffset IssuedAt { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A versioned row (lightweight-transaction writes) whose number and stamp the store assigns.</summary>
public sealed class StampedLedger : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Holder { get; set; } = "";

    public long Version { get; set; }

    [StoreGenerated]
    public long Number { get; set; }

    [StoreGenerated]
    public DateTime StampedAt { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>
///     Enums stored as labels, JSON members and store-generated members against a real Cassandra: the label as
///     <c>text</c> (written, read, filtered and set-based-updated), JSON as text holding the JSON, a sequence advanced
///     by a lightweight-transaction compare-and-set, <c>toTimestamp(now())</c> in the <c>INSERT</c> read back by the
///     row's key, and updates that never set what the store assigned.
/// </summary>
[TestFixture]
public sealed class CassandraNativeTypesTests : CassandraIntegrationTest
{
    /// <summary>
    ///     The model of these tests alone (the shared one is held to its own tables by the drift checks). The enum is
    ///     declared after the entity that uses it, and still lands on its column.
    /// </summary>
    private static void NativeTypesSchema(CassandraModelBuilder builder) => builder
        .Entity<RunRecord>(entity => entity
            .Table("run_record")
            .PartitionKey(x => x.Id))
        .Entity<Receipt>(entity => entity
            .Table("receipts")
            .PartitionKey(x => x.Id)
            .Generated(x => x.Number)
            .Generated(x => x.IssuedAt, StoreValue.Clock))
        .Entity<StampedLedger>(entity => entity
            .Table("stamped_ledgers")
            .PartitionKey(x => x.Id)
            .ConcurrencyToken(x => x.Version))
        .Enum<Channel>();

    private static CassandraTestDatabase NewNativeTypesDatabase() =>
        new(CassandraTestServer.Host, CassandraTestServer.Port, "ks_" + Guid.NewGuid().ToString("N"), NativeTypesSchema,
            [typeof(SchemaSetupMigration).Assembly]);

    /// <summary>A keyspace with the native-types tables created from the model (and nothing else).</summary>
    private static async Task<CassandraTestDatabase> NativeTypesDatabaseAsync()
    {
        var db = NewNativeTypesDatabase();
        await db.Resolve<IMigrationExecutor>().ApplyAsync(new MigrationBuilder()
            .For<RunRecord>(run => run.EnsureCollection())
            .For<Receipt>(receipt => receipt.EnsureCollection())
            .For<StampedLedger>(ledger => ledger.EnsureCollection())
            .Operations);
        return db;
    }

    private static IAsyncRepository<RunRecord, Guid> Runs(CassandraTestDatabase db) => db.Resolve<IAsyncRepository<RunRecord, Guid>>();

    private static async Task<Row> Raw(CassandraTestDatabase db, string columns, Guid id) =>
        (await db.Session.ExecuteAsync(new SimpleStatement($"SELECT {columns} FROM run_record WHERE id = ?", id))).First();

    private static QueryOptions<RunRecord> Filtering() => new QueryOptions<RunRecord>().AllowFiltering();

    [Test]
    public async Task The_migration_creates_text_columns_for_labels_and_json()
    {
        using var db = await NativeTypesDatabaseAsync();

        var types = (await db.Session.ExecuteAsync(new SimpleStatement(
                "SELECT column_name, type FROM system_schema.columns WHERE keyspace_name = ? AND table_name = 'run_record'", db.Keyspace)))
            .ToDictionary(row => row.GetValue<string>("column_name"), row => row.GetValue<string>("type"));

        Assert.That(types["state"], Is.EqualTo("text"), "an enum stored as labels is text");
        Assert.That(types["channel"], Is.EqualTo("text"), "declared with Enum<Channel>() after the entity, still labels");
        Assert.That(types["day"], Is.EqualTo("int"), "a plain enum is its number");
        Assert.That(types["payload"], Is.EqualTo("text"), "a JSON member is text holding its JSON");
        Assert.That(types["sequence"], Is.EqualTo("bigint"));
        Assert.That(types["recordedat"], Is.EqualTo("timestamp"));
    }

    [Test]
    public async Task A_keyspace_with_labels_and_store_generated_members_reports_no_drift()
    {
        using var db = await NativeTypesDatabaseAsync();
        var source = db.Resolve<IDatabaseSnapshotSource>();

        var report = DriftComparer.Compare(source.Expect(), await source.ObserveAsync());

        Assert.That(report.Findings, Is.Empty,
            "labels as text, a plain enum as its number and the members the store assigns must read as the model wrote them");
    }

    [Test]
    public async Task Enums_are_stored_as_labels()
    {
        using var db = await NativeTypesDatabaseAsync();
        var repo = Runs(db);
        var running = new RunRecord { State = RunState.InProgress, Channel = Channel.Sms, Day = Weekday.Tuesday };
        var done = new RunRecord { State = RunState.Completed, Previous = RunState.InProgress, Channel = Channel.Email, Day = Weekday.Monday };
        await repo.AddAsync(running);
        await repo.AddAsync(done);
        await Uow(db).CommitAsync();

        var raw = await Raw(db, "state, channel, day, previous", running.Id);
        Assert.That(raw.GetValue<string>("state"), Is.EqualTo("in_progress"), "the row holds the label");
        Assert.That(raw.GetValue<string>("channel"), Is.EqualTo("Sms"));
        Assert.That(raw.GetValue<int>("day"), Is.EqualTo(2), "a plain enum still holds its number");
        Assert.That(raw.IsNull("previous"), Is.True);

        var loaded = (await repo.GetAsync(running.Id))!;
        Assert.That(loaded.State, Is.EqualTo(RunState.InProgress));
        Assert.That(loaded.Previous, Is.Null);
        Assert.That(loaded.Channel, Is.EqualTo(Channel.Sms));
        Assert.That(loaded.Day, Is.EqualTo(Weekday.Tuesday));

        var ids = new[] { running.Id, done.Id };
        Assert.That((await repo.GetFilteredAsync(x => ids.Contains(x.Id) && x.State == RunState.InProgress, Filtering())).Single().Id,
            Is.EqualTo(running.Id));
        Assert.That((await repo.GetFilteredAsync(x => ids.Contains(x.Id) && x.Previous == RunState.InProgress, Filtering())).Single().Id,
            Is.EqualTo(done.Id));
        Assert.That((await repo.GetFilteredAsync(x => ids.Contains(x.Id) && (int)x.State == 1, Filtering())).Single().Id,
            Is.EqualTo(running.Id), "the compiler's own shape of a comparison with a member compares labels");
        Assert.That((await repo.GetFilteredAsync(x => ids.Contains(x.Id) && x.Day == Weekday.Monday, Filtering())).Single().Id,
            Is.EqualTo(done.Id), "a plain enum still filters by its number");

        // CQL has no '<>': the clause runs client-side, over members read back from their labels.
        Assert.That((await repo.GetFilteredAsync(x => ids.Contains(x.Id) && x.Channel != Channel.Sms,
            Filtering().AllowClientEvaluation())).Single().Id, Is.EqualTo(done.Id));

        loaded.State = RunState.Completed;
        await repo.ModifyAsync(loaded);
        await Uow(db).CommitAsync();
        Assert.That((await repo.GetAsync(running.Id))!.State, Is.EqualTo(RunState.Completed), "an update writes the label");

        var updated = await repo.UpdateManyAsync(x => x.Id == done.Id, x => new RunRecord { State = RunState.Queued });
        Assert.That(updated, Is.EqualTo(1L));
        Assert.That((await Raw(db, "state", done.Id)).GetValue<string>("state"), Is.EqualTo("Queued"),
            "a set-based update binds the label too");
    }

    [Test]
    public async Task Json_members_round_trip_as_json_text()
    {
        using var db = await NativeTypesDatabaseAsync();
        var repo = Runs(db);
        using var payload = JsonDocument.Parse("""{"name":"build","matrix":{"os":["linux","macos"]},"retries":2}""");
        var record = new RunRecord
        {
            Payload = payload,
            Steps = JsonNode.Parse("""[1,"two",{"three":3},null,true,2.5]"""),
            Meta = JsonDocument.Parse("\"just a string\"").RootElement.Clone(),
        };
        var empty = new RunRecord();
        await repo.AddAsync(record);
        await repo.AddAsync(empty);
        await Uow(db).CommitAsync();

        var raw = await Raw(db, "payload, meta", record.Id);
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(raw.GetValue<string>("payload")), JsonNode.Parse(payload.RootElement.GetRawText())),
            Is.True, "the column holds the JSON text");
        Assert.That(raw.GetValue<string>("meta"), Is.EqualTo("\"just a string\""), "a scalar is its JSON text");

        var loaded = (await repo.GetAsync(record.Id))!;
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(loaded.Payload!.RootElement.GetRawText()),
            JsonNode.Parse(payload.RootElement.GetRawText())), Is.True, "the object comes back equal");
        Assert.That(JsonNode.DeepEquals(loaded.Steps, record.Steps), Is.True, "the array comes back equal");
        Assert.That(loaded.Meta?.GetString(), Is.EqualTo("just a string"), "a scalar comes back equal");

        var blank = (await repo.GetAsync(empty.Id))!;
        Assert.That(blank.Payload, Is.Null);
        Assert.That(blank.Steps, Is.Null);
        Assert.That(blank.Meta, Is.Null);

        loaded.Steps = new JsonArray(JsonValue.Create("rewritten"));
        await repo.ModifyAsync(loaded);
        await Uow(db).CommitAsync();
        Assert.That((await repo.GetAsync(record.Id))!.Steps!.ToJsonString(), Is.EqualTo("""["rewritten"]"""));
    }

    [Test]
    public async Task Store_generated_members_are_the_stores()
    {
        using var db = await NativeTypesDatabaseAsync();
        var repo = Runs(db);
        var before = DateTime.UtcNow.AddMinutes(-1);
        var first = new RunRecord();
        var second = new RunRecord();
        await repo.AddAsync(first);
        await repo.AddAsync(second);
        await Uow(db).CommitAsync();

        Assert.That(first.Sequence, Is.GreaterThan(0L), "the sequence was read back onto the entity");
        Assert.That(second.Sequence, Is.Not.EqualTo(first.Sequence), "each row took its own number");
        Assert.That(first.RecordedAt, Is.GreaterThan(before), "the clock was read back onto the entity");
        Assert.That((await Raw(db, "sequence", first.Id)).GetValue<long>("sequence"), Is.EqualTo(first.Sequence));
        var counter = (await db.Session.ExecuteAsync(new SimpleStatement(
            $"SELECT value FROM {CassandraSequences.TableName} WHERE name = 'run_record.Sequence'"))).Single();
        Assert.That(counter.GetValue<long>("value"), Is.EqualTo(Math.Max(first.Sequence, second.Sequence)),
            "the sequence row holds the last number it handed out");

        var stored = (await repo.GetAsync(first.Id))!;
        Assert.That(stored.RecordedAt, Is.EqualTo(first.RecordedAt).Within(TimeSpan.FromMilliseconds(1)));

        stored.Sequence = 999_999;
        stored.RecordedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        stored.Channel = Channel.Sms;
        await repo.ModifyAsync(stored);
        await Uow(db).CommitAsync();

        var after = (await repo.GetAsync(first.Id))!;
        Assert.That(after.Channel, Is.EqualTo(Channel.Sms), "the update ran");
        Assert.That(after.Sequence, Is.EqualTo(first.Sequence), "the update left the sequence alone");
        Assert.That(after.RecordedAt, Is.EqualTo(first.RecordedAt).Within(TimeSpan.FromMilliseconds(1)));

        await repo.UpdateManyAsync(x => x.Id == first.Id, x => new RunRecord { Channel = Channel.Email });
        var afterSetBased = (await repo.GetAsync(first.Id))!;
        Assert.That(afterSetBased.Sequence, Is.EqualTo(first.Sequence), "a set-based update sets only what it names");
        Assert.That(afterSetBased.RecordedAt, Is.EqualTo(first.RecordedAt).Within(TimeSpan.FromMilliseconds(1)));
    }

    [Test]
    public async Task Concurrent_commits_never_take_the_same_number()
    {
        using var db = await NativeTypesDatabaseAsync();
        var records = new List<RunRecord>();

        // Four units of work over one session race for the same sequence, three rows each.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var scope = db.NewScope();
            var repo = scope.ServiceProvider.GetRequiredService<IAsyncRepository<RunRecord, Guid>>();
            var mine = Enumerable.Range(0, 3).Select(_ => new RunRecord()).ToList();
            foreach (var record in mine)
            {
                await repo.AddAsync(record);
            }

            await scope.ServiceProvider.GetRequiredService<eQuantic.Core.Data.Cassandra.Repository.CassandraDefaultUnitOfWork>().CommitAsync();
            lock (records)
            {
                records.AddRange(mine);
            }
        }));

        Assert.That(records.Select(record => record.Sequence).Distinct().Count(), Is.EqualTo(12), "twelve rows, twelve numbers");
        Assert.That(records.Max(record => record.Sequence), Is.EqualTo(12L), "and no number was skipped");
    }

    [Test]
    public async Task A_logged_batch_carries_the_stores_values()
    {
        using var db = await NativeTypesDatabaseAsync();
        var repo = Runs(db);
        var uow = Uow(db);
        var existing = new RunRecord();
        await repo.AddAsync(existing);
        await uow.CommitAsync();
        var number = existing.Sequence;

        await uow.BeginTransactionAsync();
        var inserted = new RunRecord();
        await repo.AddAsync(inserted);
        existing.Sequence = -1;
        existing.Channel = Channel.Sms;
        await repo.ModifyAsync(existing);
        await uow.CommitAsync();
        await uow.CommitTransactionAsync();

        Assert.That(inserted.Sequence, Is.GreaterThan(number));
        var stored = (await repo.GetAsync(inserted.Id))!;
        Assert.That(stored.Sequence, Is.EqualTo(inserted.Sequence), "the batch's insert carried the sequence's number");
        Assert.That(stored.RecordedAt, Is.EqualTo(inserted.RecordedAt).Within(TimeSpan.FromMilliseconds(1)));

        var kept = (await repo.GetAsync(existing.Id))!;
        Assert.That(kept.Channel, Is.EqualTo(Channel.Sms), "the batch's upsert ran");
        Assert.That(kept.Sequence, Is.EqualTo(number), "and kept the stored sequence");
    }

    [Test]
    public async Task Fluent_declarations_and_add_if_not_exists_generate_a_sequence_and_the_clock()
    {
        using var db = await NativeTypesDatabaseAsync();
        var repo = (eQuantic.Core.Data.Cassandra.Repository.CassandraRepository<Receipt, Guid>)db.Resolve<IAsyncRepository<Receipt, Guid>>();
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        var receipt = new Receipt { Note = "first", Number = -42, IssuedAt = DateTimeOffset.MinValue };

        Assert.That(await repo.AddIfNotExistsAsync(receipt), Is.True);

        Assert.That(receipt.Number, Is.EqualTo(1), "the store's number replaced the client's");
        Assert.That(receipt.IssuedAt, Is.GreaterThan(before));
        var loaded = (await repo.GetAsync(receipt.Id))!;
        Assert.That(loaded.Number, Is.EqualTo(1));
        Assert.That(loaded.IssuedAt, Is.EqualTo(receipt.IssuedAt));
    }

    [Test]
    public async Task A_versioned_row_is_inserted_with_the_stores_values_and_updated_without_them()
    {
        using var db = await NativeTypesDatabaseAsync();
        var repo = db.Resolve<IAsyncRepository<StampedLedger, Guid>>();
        var ledger = new StampedLedger { Holder = "a" };
        await repo.AddAsync(ledger);
        await Uow(db).CommitAsync();

        Assert.That(ledger.Version, Is.EqualTo(1L));
        Assert.That(ledger.Number, Is.GreaterThan(0L), "INSERT … IF NOT EXISTS carried the sequence's number");
        var number = ledger.Number;
        var stampedAt = ledger.StampedAt;

        ledger.Holder = "b";
        ledger.Number = 0;
        await repo.ModifyAsync(ledger);
        await Uow(db).CommitAsync();

        var loaded = (await repo.GetAsync(ledger.Id))!;
        Assert.That(loaded.Holder, Is.EqualTo("b"));
        Assert.That(loaded.Version, Is.EqualTo(2L));
        Assert.That(loaded.Number, Is.EqualTo(number), "UPDATE … IF version = old left the number alone");
        Assert.That(loaded.StampedAt, Is.EqualTo(stampedAt).Within(TimeSpan.FromMilliseconds(1)));
    }

    [Test]
    public void Explain_names_labels_json_and_store_generated_columns()
    {
        using var db = NewNativeTypesDatabase();
        var explained = db.Resolve<CassandraModel>().Explain();

        Assert.That(explained, Does.Contain("Channel -> stored as labels, text (Email, Sms)"));
        Assert.That(explained, Does.Contain("RunState -> stored as labels, text (Queued, InProgress 'in_progress', Completed)"));
        Assert.That(explained, Does.Contain("column: State \"State\" (text labels)"));
        Assert.That(explained, Does.Contain("column: Day \"Day\" (int the enum's number)"));
        Assert.That(explained, Does.Contain("column: Payload \"Payload\" (text JSON text)"));
        Assert.That(explained, Does.Contain("column: Sequence \"Sequence\" (bigint) store-generated (sequence: lightweight-transaction compare-and-set on \"equantic_sequences\" row \"run_record.Sequence\")"));
        Assert.That(explained, Does.Contain("column: RecordedAt \"RecordedAt\" (timestamp) store-generated (clock: toTimestamp(now()) in the INSERT, read back by the row's key)"));
    }

    [Test]
    public void A_store_generated_key_or_wrong_type_is_refused_at_model_build()
    {
        Assert.That(() => new CassandraModelBuilder().Entity<StampedLedger>(entity => entity.PartitionKey(x => x.Number).Generated(x => x.Number)),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("primary key column"));
        Assert.That(() => new CassandraModelBuilder().Entity<StampedLedger>(entity => entity.PartitionKey(x => x.Id).Generated(x => x.Holder)),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("neither"));
    }
}
