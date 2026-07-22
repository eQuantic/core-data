using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Domain.Entities;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>A full-lifecycle document: the <c>eQuantic.Core.Domain</c> interfaces drive the write conventions.</summary>
public sealed class Note : IEntity<string>, IEntityTimeMark, IEntityTimeTrack, IEntityTimeEnded
{
    public string Id { get; set; } = default!;

    public string Title { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>
///     Proves the lifecycle conventions against a real MongoDB: stamped <c>CreatedAt</c>/<c>UpdatedAt</c>,
///     soft deletes surviving as documents scoped out of reads, and set-based writes honouring both.
/// </summary>
[TestFixture]
public sealed class MongoLifecycleTests : MongoIntegrationTest
{
    [Test]
    public async Task Lifecycle_interfaces_stamp_soft_delete_and_scope_reads_by_convention()
    {
        using var db = NewDatabase();
        var notes = db.Resolve<IAsyncRepository<Note, string>>();
        var uow = Uow(db);

        var note = new Note { Id = Guid.NewGuid().ToString("N"), Title = "draft" };
        await notes.AddAsync(note);
        await uow.CommitAsync();
        Assert.That(note.CreatedAt, Is.Not.EqualTo(default(DateTime)), "CreatedAt stamped on insert");
        Assert.That(note.UpdatedAt, Is.Null, "UpdatedAt untouched on insert");

        note.Title = "revised";
        await notes.ModifyAsync(note);
        await uow.CommitAsync();
        Assert.That(note.UpdatedAt, Is.Not.Null, "UpdatedAt stamped on update");

        await notes.RemoveAsync(note);
        await uow.CommitAsync();
        Assert.That(await notes.GetAsync(note.Id), Is.Null, "the live-rows filter scopes reads");
        var all = await notes.GetAllAsync(new QueryOptions<Note>().IgnoringQueryFilters());
        Assert.That(all.Single().DeletedAt, Is.Not.Null, "the document survived as a soft delete");

        var second = new Note { Id = Guid.NewGuid().ToString("N"), Title = "bulk" };
        await notes.AddAsync(second);
        await uow.CommitAsync();
        Assert.That(await notes.DeleteManyAsync(x => x.Title == "bulk"), Is.EqualTo(1),
            "set-based deletes soft-delete too ($set DeletedAt)");
        Assert.That(await notes.CountAsync(), Is.Zero);

        var updated = await notes.UpdateManyAsync(
            x => x.Title == "revised", x => new Note { Title = "renamed" },
            CancellationToken.None);
        Assert.That(updated, Is.Zero, "set-based updates stay scoped to live documents");
    }
}
