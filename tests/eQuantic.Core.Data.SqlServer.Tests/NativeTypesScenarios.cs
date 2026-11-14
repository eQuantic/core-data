using System.Text.Json;
using System.Text.Json.Nodes;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.SqlServer.Tests;

/// <summary>
///     The scenarios SQL Server has to pass, as MySQL and MariaDB do for enums stored as labels, JSON members and store-generated
///     members: the same assertions, each run against its own server.
/// </summary>
internal static class NativeTypesScenarios
{
    /// <summary>An enum is written as its label, read back as the enum, filtered on and updated.</summary>
    public static async Task EnumsAreStoredAsLabels(IAsyncRepository<RunRecord, Guid> repo, Func<Task> commit,
        Func<string, Task<object?>> scalar)
    {
        var running = new RunRecord { State = RunState.InProgress, Channel = Channel.Sms, Day = Weekday.Tuesday };
        var done = new RunRecord { State = RunState.Completed, Previous = RunState.InProgress, Channel = Channel.Email, Day = Weekday.Monday };
        await repo.AddAsync(running);
        await repo.AddAsync(done);
        await commit();

        Assert.That(await scalar($"SELECT state FROM run_record WHERE id = '{running.Id}'"), Is.EqualTo("in_progress"),
            "the row holds the label");
        Assert.That(Convert.ToInt32(await scalar($"SELECT day FROM run_record WHERE id = '{running.Id}'")), Is.EqualTo(2),
            "a plain enum still holds its number");

        var loaded = (await repo.GetAsync(running.Id))!;
        Assert.That(loaded.State, Is.EqualTo(RunState.InProgress));
        Assert.That(loaded.Previous, Is.Null);
        Assert.That(loaded.Channel, Is.EqualTo(Channel.Sms));
        Assert.That(loaded.Day, Is.EqualTo(Weekday.Tuesday));

        var ids = new[] { running.Id, done.Id };
        Assert.That((await repo.GetFilteredAsync(x => ids.Contains(x.Id) && x.State == RunState.InProgress)).Single().Id,
            Is.EqualTo(running.Id));
        Assert.That((await repo.GetFilteredAsync(x => ids.Contains(x.Id) && x.Previous == RunState.InProgress)).Single().Id,
            Is.EqualTo(done.Id));
        Assert.That((await repo.GetFilteredAsync(x => ids.Contains(x.Id) && x.Channel != Channel.Sms)).Single().Id,
            Is.EqualTo(done.Id));

        loaded.State = RunState.Completed;
        await repo.ModifyAsync(loaded);
        await commit();
        Assert.That((await repo.GetAsync(running.Id))!.State, Is.EqualTo(RunState.Completed), "an update writes the label");

        var updated = await repo.UpdateManyAsync(x => x.Id == done.Id, x => new RunRecord { State = RunState.Queued });
        Assert.That(updated, Is.EqualTo(1L));
        Assert.That(await scalar($"SELECT state FROM run_record WHERE id = '{done.Id}'"), Is.EqualTo("Queued"),
            "a set-based update binds the label too");
    }

    /// <summary>A JSON member holds an object, an array or a scalar, and gives it back equal.</summary>
    public static async Task JsonMembersRoundTrip(IAsyncRepository<RunRecord, Guid> repo, Func<Task> commit)
    {
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
        await commit();

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
        await commit();
        Assert.That((await repo.GetAsync(record.Id))!.Steps!.ToJsonString(), Is.EqualTo("""["rewritten"]"""));
    }

    /// <summary>The store numbers and stamps the row, the values come back on insert, and an update leaves them.</summary>
    public static async Task StoreGeneratedMembersAreTheStores(IAsyncRepository<RunRecord, Guid> repo, Func<Task> commit,
        Func<string, Task<object?>> scalar)
    {
        var before = DateTime.UtcNow.AddMinutes(-1);
        var first = new RunRecord();
        var second = new RunRecord();
        await repo.AddAsync(first);
        await repo.AddAsync(second);
        await commit();

        Assert.That(first.Sequence, Is.GreaterThan(0L), "the sequence was read back onto the entity");
        Assert.That(second.Sequence, Is.Not.EqualTo(first.Sequence), "each row took its own number");
        Assert.That(first.RecordedAt, Is.GreaterThan(before), "the clock was read back onto the entity");
        Assert.That(Convert.ToInt64(await scalar($"SELECT sequence FROM run_record WHERE id = '{first.Id}'")),
            Is.EqualTo(first.Sequence));

        var stored = (await repo.GetAsync(first.Id))!;
        Assert.That(stored.RecordedAt, Is.EqualTo(first.RecordedAt).Within(TimeSpan.FromMilliseconds(1)));

        stored.Sequence = 999_999;
        stored.RecordedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        stored.Channel = Channel.Sms;
        await repo.ModifyAsync(stored);
        await commit();

        var after = (await repo.GetAsync(first.Id))!;
        Assert.That(after.Channel, Is.EqualTo(Channel.Sms), "the update ran");
        Assert.That(after.Sequence, Is.EqualTo(first.Sequence), "the update left the sequence alone");
        Assert.That(after.RecordedAt, Is.EqualTo(first.RecordedAt).Within(TimeSpan.FromMilliseconds(1)));
    }
}
