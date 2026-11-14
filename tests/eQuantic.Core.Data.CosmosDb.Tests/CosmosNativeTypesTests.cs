using System.Text.Json;
using System.Text.Json.Nodes;
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Write;
using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb.Tests;

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

/// <summary>Enums stored as labels, JSON members, and members the store fills (by annotation). <c>Category</c> partitions.</summary>
public sealed class RunRecord : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Category { get; set; } = "";

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

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>Members the store fills, declared by the fluent model instead of annotations.</summary>
public sealed class Receipt : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Category { get; set; } = "";

    public string Note { get; set; } = "";

    public int Number { get; set; }

    public DateTimeOffset IssuedAt { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>A stamp the store dates, in a container a test removes to make the read of the server's clock fail.</summary>
public sealed class ClockStamp : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Category { get; set; } = "";

    public DateTime At { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>
///     Enums stored as labels, JSON members and store-generated members against the Cosmos DB emulator: the label as
///     a JSON string (in the document, in the SDK's LINQ translation and in a patch), JSON embedded in the document,
///     a sequence from a <c>Patch</c> <c>Increment</c> on a counter item and the server's clock from
///     <c>GetCurrentDateTime()</c>, and a replace that keeps what the store generated.
/// </summary>
[TestFixture]
public sealed class CosmosNativeTypesTests : CosmosIntegrationTest
{
    private IAsyncRepository<RunRecord, string> Runs => Resolve<IAsyncRepository<RunRecord, string>>();

    private async Task<JsonNode> Raw(string id) =>
        (await Database.GetContainer(CosmosTestServer.ContainerName).ReadItemAsync<JsonNode>(id, new PartitionKey(Partition))).Resource;

    [Test]
    public async Task Enums_are_stored_as_labels()
    {
        var running = new RunRecord { Category = Partition, State = RunState.InProgress, Channel = Channel.Sms, Day = Weekday.Tuesday };
        var done = new RunRecord
        {
            Category = Partition, State = RunState.Completed, Previous = RunState.InProgress, Channel = Channel.Email, Day = Weekday.Monday,
        };
        await Runs.AddAsync(running);
        await Runs.AddAsync(done);
        await Uow.CommitAsync();

        var raw = await Raw(running.Id);
        Assert.That(raw["state"]?.GetValue<string>(), Is.EqualTo("in_progress"), "the document holds the label");
        Assert.That(raw["channel"]?.GetValue<string>(), Is.EqualTo("Sms"), "the fluent declaration stores the label too");
        Assert.That(raw["day"]?.GetValue<int>(), Is.EqualTo(2), "a plain enum still holds its number");
        Assert.That(raw["previous"], Is.Null);
        Assert.That((await Raw(done.Id))["previous"]?.GetValue<string>(), Is.EqualTo("in_progress"));

        var loaded = (await Runs.GetAsync(running.Id))!;
        Assert.That(loaded.State, Is.EqualTo(RunState.InProgress));
        Assert.That(loaded.Previous, Is.Null);
        Assert.That(loaded.Channel, Is.EqualTo(Channel.Sms));
        Assert.That(loaded.Day, Is.EqualTo(Weekday.Tuesday));

        // The SDK's LINQ translation asks the provider's serializer for each constant: labels on both sides. (A list:
        // C# 14 binds Contains on an array to MemoryExtensions over a span, which the SDK cannot evaluate.)
        var ids = new List<string> { running.Id, done.Id };
        Assert.That((await Runs.GetFilteredAsync(x => ids.Contains(x.Id) && x.State == RunState.InProgress)).Single().Id,
            Is.EqualTo(running.Id));
        Assert.That((await Runs.GetFilteredAsync(x => ids.Contains(x.Id) && x.Previous == RunState.InProgress)).Single().Id,
            Is.EqualTo(done.Id));
        Assert.That((await Runs.GetFilteredAsync(x => ids.Contains(x.Id) && x.Channel != Channel.Sms)).Single().Id,
            Is.EqualTo(done.Id));
        Assert.That((await Runs.GetFilteredAsync(x => ids.Contains(x.Id) && (int)x.State == 1)).Single().Id,
            Is.EqualTo(running.Id), "the compiler's own shape of a comparison with a member compares labels");
        Assert.That((await Runs.GetFilteredAsync(x => ids.Contains(x.Id) && x.Day == Weekday.Tuesday)).Single().Id,
            Is.EqualTo(running.Id), "a plain enum still filters by its number");

        loaded.State = RunState.Completed;
        await Runs.ModifyAsync(loaded);
        await Uow.CommitAsync();
        Assert.That((await Runs.GetAsync(running.Id))!.State, Is.EqualTo(RunState.Completed), "an update writes the label");
        Assert.That((await Raw(running.Id))["state"]?.GetValue<string>(), Is.EqualTo("Completed"));

        var updated = await Runs.UpdateManyAsync(x => x.Id == done.Id && x.Previous == RunState.InProgress,
            x => new RunRecord { State = RunState.Queued });
        Assert.That(updated, Is.EqualTo(1L));
        Assert.That((await Raw(done.Id))["state"]?.GetValue<string>(), Is.EqualTo("Queued"), "a set-based update patches the label too");
    }

    [Test]
    public async Task Json_members_are_embedded_and_round_trip()
    {
        using var payload = JsonDocument.Parse("""{"name":"build","matrix":{"os":["linux","macos"]},"retries":2}""");
        var record = new RunRecord
        {
            Category = Partition,
            Payload = payload,
            Steps = JsonNode.Parse("""[1,"two",{"three":3},null,true,2.5]"""),
            Meta = JsonDocument.Parse("\"just a string\"").RootElement.Clone(),
        };
        var empty = new RunRecord { Category = Partition };
        await Runs.AddAsync(record);
        await Runs.AddAsync(empty);
        await Uow.CommitAsync();

        var raw = await Raw(record.Id);
        Assert.That(raw["payload"], Is.InstanceOf<JsonObject>(), "an object is embedded as an object");
        Assert.That(raw["payload"]!["matrix"]!["os"]![1]!.GetValue<string>(), Is.EqualTo("macos"));
        Assert.That(raw["steps"], Is.InstanceOf<JsonArray>(), "an array is embedded as an array");
        Assert.That(raw["meta"]!.GetValue<string>(), Is.EqualTo("just a string"), "a scalar is embedded as the scalar");

        var loaded = (await Runs.GetAsync(record.Id))!;
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(loaded.Payload!.RootElement.GetRawText()),
            JsonNode.Parse(payload.RootElement.GetRawText())), Is.True, "the object comes back equal");
        Assert.That(JsonNode.DeepEquals(loaded.Steps, record.Steps), Is.True, "the array comes back equal");
        Assert.That(loaded.Meta?.GetString(), Is.EqualTo("just a string"), "a scalar comes back equal");

        var blank = (await Runs.GetAsync(empty.Id))!;
        Assert.That(blank.Payload, Is.Null);
        Assert.That(blank.Steps, Is.Null);
        Assert.That(blank.Meta, Is.Null);

        loaded.Steps = new JsonArray(JsonValue.Create("rewritten"));
        await Runs.ModifyAsync(loaded);
        await Uow.CommitAsync();
        Assert.That((await Runs.GetAsync(record.Id))!.Steps!.ToJsonString(), Is.EqualTo("""["rewritten"]"""));
    }

    [Test]
    public async Task Store_generated_members_are_the_stores()
    {
        var before = DateTime.UtcNow.AddMinutes(-1);
        var first = new RunRecord { Category = Partition };
        var second = new RunRecord { Category = Partition };
        await Runs.AddAsync(first);
        await Runs.AddAsync(second);
        await Uow.CommitAsync();

        Assert.That(first.Sequence, Is.GreaterThan(0L), "the sequence was read back onto the entity");
        Assert.That(second.Sequence, Is.Not.EqualTo(first.Sequence), "each document took its own number");
        Assert.That(first.RecordedAt, Is.GreaterThan(before), "the clock was read back onto the entity");
        Assert.That(second.RecordedAt, Is.EqualTo(first.RecordedAt), "one commit, one read of the server's clock");

        var raw = await Raw(first.Id);
        Assert.That(raw["sequence"]!.GetValue<long>(), Is.EqualTo(first.Sequence));
        var counter = (await Database.GetContainer(CosmosSequences.ContainerName)
            .ReadItemAsync<JsonNode>("products.sequence", new PartitionKey("products.sequence"))).Resource;
        Assert.That(counter["value"]!.GetValue<long>(), Is.GreaterThanOrEqualTo(Math.Max(first.Sequence, second.Sequence)),
            "the counter item holds the last number it handed out");

        var stored = (await Runs.GetAsync(first.Id))!;
        Assert.That(stored.RecordedAt, Is.EqualTo(first.RecordedAt).Within(TimeSpan.FromMilliseconds(1)));

        stored.Sequence = 999_999;
        stored.RecordedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        stored.Channel = Channel.Sms;
        await Runs.ModifyAsync(stored);
        await Uow.CommitAsync();

        var after = (await Runs.GetAsync(first.Id))!;
        Assert.That(after.Channel, Is.EqualTo(Channel.Sms), "the update ran");
        Assert.That(after.Sequence, Is.EqualTo(first.Sequence), "the update left the sequence alone");
        Assert.That(after.RecordedAt, Is.EqualTo(first.RecordedAt).Within(TimeSpan.FromMilliseconds(1)));

        var updated = await Runs.UpdateManyAsync(x => x.Id == first.Id, x => new RunRecord { Channel = Channel.Email });
        Assert.That(updated, Is.EqualTo(1L));
        var afterSetBased = (await Runs.GetAsync(first.Id))!;
        Assert.That(afterSetBased.Sequence, Is.EqualTo(first.Sequence), "a set-based update patches only what it names");
        Assert.That(afterSetBased.RecordedAt, Is.EqualTo(first.RecordedAt).Within(TimeSpan.FromMilliseconds(1)));
    }

    [Test]
    public async Task A_transactional_batch_carries_the_stores_values()
    {
        var existing = new RunRecord { Category = Partition };
        await Runs.AddAsync(existing);
        await Uow.CommitAsync();

        await Uow.BeginTransactionAsync();
        var inserted = new RunRecord { Category = Partition };
        await Runs.AddAsync(inserted);
        existing.Sequence = -1;
        existing.Channel = Channel.Sms;
        await Runs.ModifyAsync(existing);
        await Uow.CommitAsync();
        await Uow.CommitTransactionAsync();

        Assert.That(inserted.Sequence, Is.GreaterThan(0L));
        var stored = (await Runs.GetAsync(inserted.Id))!;
        Assert.That(stored.Sequence, Is.EqualTo(inserted.Sequence), "the batch's insert carried the counter's number");
        Assert.That(stored.RecordedAt, Is.EqualTo(inserted.RecordedAt).Within(TimeSpan.FromMilliseconds(1)));

        var kept = (await Runs.GetAsync(existing.Id))!;
        Assert.That(kept.Channel, Is.EqualTo(Channel.Sms), "the batch's replace ran");
        Assert.That(kept.Sequence, Is.Not.EqualTo(-1L).And.GreaterThan(0L), "and kept the stored sequence");
    }

    [Test]
    public async Task Fluent_declarations_generate_a_sequence_and_the_clock()
    {
        var repo = Resolve<IAsyncRepository<Receipt, string>>();
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        var receipt = new Receipt { Category = Partition, Note = "first", Number = -42, IssuedAt = DateTimeOffset.MinValue };
        await repo.AddAsync(receipt);
        await Uow.CommitAsync();

        Assert.That(receipt.Number, Is.GreaterThan(0), "the store's number replaced the client's");
        Assert.That(receipt.IssuedAt, Is.GreaterThan(before));
        var loaded = (await repo.GetAsync(receipt.Id))!;
        Assert.That(loaded.Number, Is.EqualTo(receipt.Number));
        Assert.That(loaded.IssuedAt, Is.EqualTo(receipt.IssuedAt));
    }

    [Test]
    public async Task Each_immediate_insert_reads_the_servers_clock_when_it_runs()
    {
        // One unit of work, three immediate writes far apart: each takes the clock of its own moment, never one an
        // earlier write of the same unit of work read.
        var upserts = Resolve<IUpsertRepository<Receipt, string>>();
        var first = new Receipt { Category = Partition, Note = "first" };
        Assert.That(await upserts.InsertManyAsync([first]), Is.EqualTo(1L));

        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var second = new Receipt { Category = Partition, Note = "second" };
        Assert.That(await upserts.InsertManyAsync([second]), Is.EqualTo(1L));
        Assert.That(second.IssuedAt - first.IssuedAt, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1)),
            "the second insert read the clock again");

        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var third = await upserts.UpsertAsync(new Receipt { Category = Partition, Note = "third" });
        Assert.That(third.IssuedAt - second.IssuedAt, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1)),
            "an upsert that inserts reads the clock again too");

        var stored = (await Resolve<IAsyncRepository<Receipt, string>>().GetAsync(second.Id))!;
        Assert.That(stored.IssuedAt, Is.EqualTo(second.IssuedAt), "the document holds the clock the entity was given");
    }

    [Test]
    public async Task A_failed_read_of_the_servers_clock_is_not_kept()
    {
        // The clock is read by a query scoped to the entity's container: while the container is not there, the read
        // fails. Once it is, the same unit of work reads the clock again instead of answering the old failure.
        var container = Database.GetContainer(CosmosTestServer.ClockProbeContainer);
        using (await container.DeleteContainerStreamAsync())
        {
        }

        var upserts = Resolve<IUpsertRepository<ClockStamp, string>>();
        var failed = Assert.ThrowsAsync<CosmosException>(() => upserts.InsertManyAsync([new ClockStamp { Category = Partition }]));
        Assert.That(failed!.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.NotFound));

        await Database.CreateContainerIfNotExistsAsync(new ContainerProperties(CosmosTestServer.ClockProbeContainer, "/category"));
        try
        {
            var before = DateTime.UtcNow.AddMinutes(-1);
            var stamp = new ClockStamp { Category = Partition };
            Assert.That(await upserts.InsertManyAsync([stamp]), Is.EqualTo(1L));
            Assert.That(stamp.At, Is.GreaterThan(before), "the insert took the server's clock");
        }
        finally
        {
            using (await container.DeleteContainerStreamAsync())
            {
            }
        }
    }

    [Test]
    public void Explain_names_labels_and_store_generated_members()
    {
        var explained = Resolve<CosmosModel>().Explain();

        Assert.That(explained, Does.Contain("Channel -> stored as labels, a JSON string (Email, Sms)"));
        Assert.That(explained, Does.Contain("RunState -> stored as labels, a JSON string (Queued, InProgress 'in_progress', Completed)"));
        Assert.That(explained, Does.Contain("store-generated: Sequence \"sequence\" (sequence: Patch Increment on \"_sequences\" item \"products.sequence\")"));
        Assert.That(explained, Does.Contain("store-generated: RecordedAt \"recordedAt\" (clock: GetCurrentDateTime(), read once per commit)"));
        Assert.That(explained, Does.Not.Contain("Weekday -> stored as labels"), "a plain enum keeps its number");
    }

    [Test]
    public void A_store_generated_id_partition_key_or_wrong_type_is_refused_at_model_build()
    {
        Assert.That(() => new CosmosModelBuilder().Entity<Numbered>(entity => entity.PartitionKey(x => x.Category).Generated(x => x.Id)),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("the document's id"));
        Assert.That(() => new CosmosModelBuilder().Entity<Numbered>(entity => entity.PartitionKey(x => x.Shard).Generated(x => x.Shard)),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("partition key level"));
        Assert.That(() => new CosmosModelBuilder().Entity<Numbered>(entity => entity.PartitionKey(x => x.Category).Generated(x => x.Category)),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("neither"));
    }

    /// <summary>An entity whose numeric id and partition someone tries to declare store-generated.</summary>
    public sealed class Numbered : IEntity<long>
    {
        public long Id { get; set; }

        public string Category { get; set; } = "";

        public long Shard { get; set; }

        public long GetKey() => Id;

        public void SetKey(long key) => Id = key;
    }
}
