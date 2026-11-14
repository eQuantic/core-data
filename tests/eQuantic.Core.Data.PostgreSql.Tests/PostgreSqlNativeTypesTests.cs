using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>
///     Proves, against a real PostgreSQL, the three column shapes a schema made by another tool holds: native enum
///     types, JSON members holding any JSON, and columns the store fills itself.
/// </summary>
[TestFixture]
public sealed class PostgreSqlNativeTypesTests : PostgreSqlIntegrationTest
{
    private static IAsyncRepository<RunRecord, Guid> Runs(PostgreSqlTestDatabase db) =>
        db.Resolve<IAsyncRepository<RunRecord, Guid>>();

    private static async Task<object?> Scalar(PostgreSqlTestDatabase db, string sql)
    {
        await using var command = db.Resolve<DbDataSource>().CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }

    [Test]
    public async Task The_migration_creates_the_enum_types_and_columns_of_those_types()
    {
        using var db = await NewSchemaAsync();

        Assert.That(await Scalar(db,
                "SELECT string_agg(enumlabel, ',' ORDER BY enumsortorder) FROM pg_enum WHERE enumtypid = '\"RunState\"'::regtype"),
            Is.EqualTo("Queued,in_progress,Completed"), "the type holds each member's [StoredAs] label or name, in order");
        Assert.That(await Scalar(db,
                "SELECT string_agg(enumlabel, ',' ORDER BY enumsortorder) FROM pg_enum WHERE enumtypid = 'channel'::regtype"),
            Is.EqualTo("Email,Sms"), "an enum declared without a name takes the convention's");

        var types = new Dictionary<string, string>();
        await using (var command = db.Resolve<DbDataSource>().CreateCommand(
                         "SELECT a.attname, format_type(a.atttypid, a.atttypmod) FROM pg_attribute a " +
                         "WHERE a.attrelid = 'run_record'::regclass AND a.attnum > 0 AND NOT a.attisdropped"))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                types[reader.GetString(0)] = reader.GetString(1);
            }
        }

        Assert.That(types["state"], Is.EqualTo("\"RunState\""));
        Assert.That(types["previous"], Is.EqualTo("\"RunState\""));
        Assert.That(types["history"], Is.EqualTo("\"RunState\"[]"), "an enum list is an array of the enum type");
        Assert.That(types["channel"], Is.EqualTo("channel"));
        Assert.That(types["day"], Is.EqualTo("integer"), "an enum not declared native is still its number");
        Assert.That(types["payload"], Is.EqualTo("jsonb"));
        Assert.That(types["steps"], Is.EqualTo("jsonb"));
        Assert.That(types["meta"], Is.EqualTo("jsonb"));
        Assert.That(types["sequence"], Is.EqualTo("integer"));
    }

    [Test]
    public async Task Creating_an_enum_type_that_exists_leaves_it_as_it_is()
    {
        using var db = await NewSchemaAsync();
        var dialect = db.Resolve<SqlDialect>();
        var declaration = dialect.NativeEnums.Single(native => native.EnumType == typeof(RunState));

        await using (var command = db.Resolve<DbDataSource>().CreateCommand(dialect.CreateEnumTypeSql(declaration)!))
        {
            await command.ExecuteNonQueryAsync();
        }

        Assert.That(await Scalar(db, "SELECT count(*) FROM pg_enum WHERE enumtypid = '\"RunState\"'::regtype"),
            Is.EqualTo(3L));
    }

    [Test]
    public async Task A_native_enum_is_written_as_its_label_and_read_and_filtered_as_the_enum()
    {
        using var db = await NewSchemaAsync();
        var repo = Runs(db);

        var running = new RunRecord
        {
            State = RunState.InProgress, History = [RunState.Queued, RunState.InProgress], Channel = Channel.Sms,
            Day = Weekday.Tuesday,
        };
        var done = new RunRecord
        {
            State = RunState.Completed, Previous = RunState.InProgress, History = [RunState.Completed],
            Channel = Channel.Email, Day = Weekday.Monday,
        };
        await repo.AddAsync(running);
        await repo.AddAsync(done);
        await Uow(db).CommitAsync();

        Assert.That(await Scalar(db, $"SELECT state::text FROM run_record WHERE id = '{running.Id}'"),
            Is.EqualTo("in_progress"), "the row holds the label");
        Assert.That(await Scalar(db, $"SELECT day FROM run_record WHERE id = '{running.Id}'"), Is.EqualTo(2),
            "a plain enum still holds its number");

        var loaded = (await repo.GetAsync(running.Id))!;
        Assert.That(loaded.State, Is.EqualTo(RunState.InProgress));
        Assert.That(loaded.Previous, Is.Null);
        Assert.That(loaded.History, Is.EqualTo(new[] { RunState.Queued, RunState.InProgress }));
        Assert.That(loaded.Channel, Is.EqualTo(Channel.Sms));
        Assert.That(loaded.Day, Is.EqualTo(Weekday.Tuesday));
        Assert.That((await repo.GetAsync(done.Id))!.Previous, Is.EqualTo(RunState.InProgress));

        Assert.That((await repo.GetFilteredAsync(x => x.State == RunState.InProgress)).Single().Id, Is.EqualTo(running.Id));
        Assert.That((await repo.GetFilteredAsync(x => x.Previous == RunState.InProgress)).Single().Id, Is.EqualTo(done.Id));
        Assert.That((await repo.GetFilteredAsync(x => x.History.Contains(RunState.Queued))).Single().Id,
            Is.EqualTo(running.Id), "membership in an enum array binds the enum");
        Assert.That((await repo.GetFilteredAsync(x => x.Channel != Channel.Sms)).Single().Id, Is.EqualTo(done.Id));

        loaded.State = RunState.Completed;
        loaded.Previous = RunState.InProgress;
        await repo.ModifyAsync(loaded);
        await Uow(db).CommitAsync();
        Assert.That((await repo.GetFilteredAsync(x => x.State == RunState.Completed)).Count(), Is.EqualTo(2),
            "an update writes the label");

        var updated = await repo.UpdateManyAsync(x => x.Channel == Channel.Email, x => new RunRecord { State = RunState.Queued });
        Assert.That(updated, Is.EqualTo(1L));
        Assert.That((await repo.GetAsync(done.Id))!.State, Is.EqualTo(RunState.Queued), "a set-based update binds the enum too");
    }

    [Test]
    public async Task A_json_member_round_trips_any_json()
    {
        using var db = await NewSchemaAsync();
        var repo = Runs(db);

        using var payload = JsonDocument.Parse("""{"name":"build","matrix":{"os":["linux","macos"]},"retries":2}""");
        var record = new RunRecord
        {
            Payload = payload,
            Steps = JsonNode.Parse("""[1,"two",{"three":3},null,true]"""),
            Meta = JsonDocument.Parse("\"just a string\"").RootElement.Clone(),
        };
        var empty = new RunRecord();
        await repo.AddAsync(record);
        await repo.AddAsync(empty);
        await Uow(db).CommitAsync();

        Assert.That(await Scalar(db, $"SELECT jsonb_typeof(steps) FROM run_record WHERE id = '{record.Id}'"),
            Is.EqualTo("array"), "an array is stored as an array, not dropped and not wrapped");
        Assert.That(await Scalar(db, $"SELECT payload -> 'matrix' -> 'os' ->> 1 FROM run_record WHERE id = '{record.Id}'"),
            Is.EqualTo("macos"), "the store can read inside the document");

        var loaded = (await repo.GetAsync(record.Id))!;
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(loaded.Payload!.RootElement.GetRawText()), JsonNode.Parse(payload.RootElement.GetRawText())),
            Is.True, "the object comes back equal");
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
    public async Task A_store_generated_column_takes_the_stores_value_and_an_update_never_writes_it()
    {
        using var db = await NewSchemaAsync();
        var repo = Runs(db);

        var before = DateTime.UtcNow.AddMinutes(-1);
        var first = new RunRecord { Sequence = 0 };
        var second = new RunRecord { Sequence = 0 };
        await repo.AddAsync(first);
        await repo.AddAsync(second);
        await Uow(db).CommitAsync();

        Assert.That(first.Sequence, Is.GreaterThan(0), "the identity was read back onto the entity");
        Assert.That(second.Sequence, Is.Not.EqualTo(first.Sequence), "each row took its own value");
        Assert.That(first.RecordedAt, Is.GreaterThan(before), "the default was read back onto the entity");
        Assert.That(await Scalar(db, $"SELECT sequence FROM run_record WHERE id = '{first.Id}'"), Is.EqualTo(first.Sequence));

        var stored = (await repo.GetAsync(first.Id))!;
        Assert.That(stored.RecordedAt, Is.EqualTo(first.RecordedAt).Within(TimeSpan.FromMilliseconds(1)));

        stored.Sequence = 999;
        stored.RecordedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        stored.Channel = Channel.Sms;
        await repo.ModifyAsync(stored);
        await Uow(db).CommitAsync();

        var after = (await repo.GetAsync(first.Id))!;
        Assert.That(after.Channel, Is.EqualTo(Channel.Sms), "the update ran");
        Assert.That(after.Sequence, Is.EqualTo(first.Sequence), "the update left the generated column alone");
        Assert.That(after.RecordedAt, Is.EqualTo(first.RecordedAt).Within(TimeSpan.FromMilliseconds(1)));
    }

    [Test]
    public async Task A_bulk_load_leaves_store_generated_columns_to_the_store()
    {
        using var db = await NewSchemaAsync();

        var loaded = await Uow(db).BulkInsertAsync(Enumerable.Range(0, 5).Select(_ => new RunRecord
        {
            State = RunState.Queued, History = [RunState.Queued], Sequence = 0,
        }));

        Assert.That(loaded, Is.EqualTo(5L));
        Assert.That(await Scalar(db, "SELECT count(DISTINCT sequence) FROM run_record WHERE sequence > 0"), Is.EqualTo(5L),
            "every row took the identity's next value instead of the member's 0");
        Assert.That(await Scalar(db, "SELECT count(*) FROM run_record WHERE state = 'Queued' AND recorded_at IS NOT NULL"),
            Is.EqualTo(5L));
    }

    [Test]
    public async Task A_generated_key_is_read_back_with_the_store_generated_columns_after_it()
    {
        using var db = await NewSchemaAsync();
        var repo = db.Resolve<IAsyncRepository<Receipt, long>>();

        var first = new Receipt { Note = "first" };
        var second = new Receipt { Note = "second" };
        await repo.AddAsync(first);
        await repo.AddAsync(second);
        await Uow(db).CommitAsync();

        Assert.That(first.Id, Is.GreaterThan(0L));
        Assert.That(second.Id, Is.Not.EqualTo(first.Id));
        Assert.That(first.Number, Is.GreaterThan(0L));
        Assert.That(second.Number, Is.Not.EqualTo(first.Number));
        Assert.That(first.IssuedAt, Is.Not.EqualTo(default(DateTime)));

        var stored = (await repo.GetAsync(second.Id))!;
        Assert.That(stored.Number, Is.EqualTo(second.Number), "each value went to its own member");
        Assert.That(stored.Note, Is.EqualTo("second"));
    }

    [Test]
    public void Explain_names_the_native_enums_and_the_store_generated_columns()
    {
        var dialect = new PostgreSqlDialect();
        var builder = new RelationalModelBuilder(dialect);
        TestSchema.Configure(builder);
        var report = builder.Build().Explain(dialect);

        Assert.That(report, Does.Contain("RunState -> stored as labels, \"RunState\" (Queued, InProgress 'in_progress', Completed)"),
            "an enum declared by [EnumType] on its type is declared when a column first uses it");
        Assert.That(report, Does.Contain("Channel -> stored as labels, \"channel\""));
        Assert.That(report, Does.Contain("column: Sequence \"sequence\" (integer) store-generated (sequence)"));
        Assert.That(report, Does.Contain("column: RecordedAt \"recorded_at\" (timestamp with time zone) store-generated (clock)"));
        Assert.That(report, Does.Contain("store-generated (default now())"), "the SQL escape hatch is named as written");
    }

    [Test]
    public void A_model_refuses_what_it_cannot_store_correctly()
    {
        var dialect = new PostgreSqlDialect();

        Assert.That(() => new RelationalModelBuilder(dialect)
                .Entity<RunRecord>(entity => entity.Generated(x => x.Payload)).Build(),
            Throws.InvalidOperationException.With.Message.Contains("a sequence for an integral member"));
        Assert.That(() => new RelationalModelBuilder(dialect)
                .Entity<Receipt>(entity => entity.Generated(x => x.Id)).Build(),
            Throws.InvalidOperationException.With.Message.Contains("cannot also be declared store-generated"));
        Assert.That(() => new RelationalModelBuilder(dialect)
                .Entity<Audited>(entity => entity.Generated(x => x.Audit.Serial)).Build(),
            Throws.InvalidOperationException.With.Message.Contains("is a member of a member"), "read by its last segment, it would declare the entity's own Serial");
        Assert.That(() => new RelationalModelBuilder(dialect).Enum<RunState>("RunState").Enum<RunState>("run_state"),
            Throws.InvalidOperationException.With.Message.Contains("already declared stored as labels"));
    }

    /// <summary>An entity with a member of its own and an embedded one of the same name.</summary>
    public sealed class Audited : IEntity<long>
    {
        public long Id { get; set; }

        public string Category { get; set; } = "";

        public long Serial { get; set; }

        public AuditTrail Audit { get; set; } = new();

        public long GetKey() => Id;

        public void SetKey(long key) => Id = key;
    }

    /// <summary>An embedded document whose member shares a name with one of its owner's.</summary>
    public sealed class AuditTrail
    {
        public long Serial { get; set; }
    }
}
