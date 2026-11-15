using System.Text.Json.Nodes;
using eQuantic.Core.Data.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.SqlServer.Tests;

/// <summary>
///     What mapping a schema another tool created needs, against a real SQL Server: a JSON member that leaves the
///     other members alone, required columns, and dates stored without their zone.
/// </summary>
[TestFixture]
public sealed class SqlServerSchemaFidelityTests : SqlServerIntegrationTest
{
    [Test]
    public async Task The_other_members_of_an_entity_with_a_JsonNode_member_keep_their_own_types()
    {
        using var db = await NewSchemaAsync();
        var repo = db.Resolve<IAsyncRepository<MixedJsonRow, Guid>>();
        var at = new DateTime(2026, 11, 15, 10, 20, 30, DateTimeKind.Utc);
        var row = new MixedJsonRow { Count = 7, Name = "seven", Active = true, At = at, Data = JsonNode.Parse("""{"a":[1,2]}""") };

        await repo.AddAsync(row);
        await Uow(db).CommitAsync();

        using var scope = db.NewScope();
        var read = await scope.ServiceProvider.GetRequiredService<IAsyncRepository<MixedJsonRow, Guid>>().GetAsync(row.Id);
        Assert.That(read, Is.Not.Null);
        Assert.That(read!.Count, Is.EqualTo(7));
        Assert.That(read.Name, Is.EqualTo("seven"));
        Assert.That(read.Active, Is.True);
        Assert.That(read.At, Is.EqualTo(at));
        Assert.That(JsonNode.DeepEquals(read.Data, row.Data), Is.True);
        Assert.That(EntityAccessors.For(typeof(MixedJsonRow))!.Get(row, nameof(MixedJsonRow.Count)), Is.TypeOf<int>(),
            "the generated accessor reads an int member as an int, not as a JsonValue");
    }
}
