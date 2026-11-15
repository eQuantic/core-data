using System.Data.Common;
using System.Text.Json.Nodes;
using eQuantic.Core.Data.Relational;
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

    [Test]
    public async Task A_required_column_is_NOT_NULL_and_the_store_refuses_a_row_without_it()
    {
        using var db = await NewSchemaAsync();

        Assert.That(await Scalar(db, Nullable("stamp_row", "name")), Is.EqualTo("NO"), "[Facet(Required = true)] creates the column NOT NULL");
        Assert.That(await Scalar(db, Nullable("stamp_row", "created_at")), Is.EqualTo("NO"));
        Assert.That(await Scalar(db, Nullable("stamp_row", "note")), Is.EqualTo("YES"), "a member not declared required stays nullable");
        Assert.That(await Scalar(db, Nullable("stamp_row", "closed_at")), Is.EqualTo("YES"));
        Assert.That(async () => await Scalar(db, $"INSERT INTO stamp_row (id, created_at) VALUES ('{Guid.NewGuid()}', SYSUTCDATETIME())"),
            Throws.InstanceOf<DbException>(), "the store refuses a row that leaves a required column null");
    }

    [Test]
    public async Task A_date_without_its_zone_has_its_declared_precision_and_comes_back_as_utc()
    {
        using var db = await NewSchemaAsync();
        Assert.That(await Scalar(db, Type("created_at")), Is.EqualTo("datetime2(3)"));
        Assert.That(await Scalar(db, Type("seen_at")), Is.EqualTo("datetime2(7)"), "no precision keeps the store's own");

        var repo = db.Resolve<IAsyncRepository<StampRow, Guid>>();
        var row = new StampRow
        {
            Name = "round",
            CreatedAt = new DateTime(2026, 11, 15, 10, 20, 30, 123, DateTimeKind.Utc).AddTicks(5000),
            ClosedAt = new DateTime(2026, 11, 15, 7, 20, 30, 999, DateTimeKind.Unspecified).AddTicks(5000),
            SeenAt = new DateTimeOffset(2026, 11, 15, 7, 20, 30, 250, TimeSpan.FromHours(-3)),
        };
        await repo.AddAsync(row);
        await Uow(db).CommitAsync();

        using var scope = db.NewScope();
        var back = (await scope.ServiceProvider.GetRequiredService<IAsyncRepository<StampRow, Guid>>().GetAsync(row.Id))!;
        Assert.That(back.CreatedAt, Is.EqualTo(new DateTime(2026, 11, 15, 10, 20, 30, 124, DateTimeKind.Utc)),
            ".1235 rounds up, half away from zero, as on every engine");
        Assert.That(back.CreatedAt.Kind, Is.EqualTo(DateTimeKind.Utc));
        Assert.That(back.ClosedAt, Is.EqualTo(new DateTime(2026, 11, 15, 7, 20, 31, 0, DateTimeKind.Utc)));
        Assert.That(back.SeenAt, Is.EqualTo(new DateTimeOffset(2026, 11, 15, 10, 20, 30, 250, TimeSpan.Zero)));
        Assert.That(back.SeenAt!.Value.Offset, Is.EqualTo(TimeSpan.Zero));

        var halfPast = new DateTime(2026, 11, 15, 10, 20, 30, 500, DateTimeKind.Utc);
        var found = await scope.ServiceProvider.GetRequiredService<IAsyncRepository<StampRow, Guid>>()
            .GetFilteredAsync(x => x.Id == row.Id && x.CreatedAt < halfPast);
        Assert.That(found.Count(), Is.EqualTo(1), "a filter compares the member as UTC");
    }

    [Test]
    public async Task Explain_names_required_members_and_dates_without_their_zone()
    {
        using var db = await NewSchemaAsync();

        var explained = db.Resolve<RelationalModel>().Explain(db.Resolve<SqlDialect>());

        Assert.That(explained, Does.Contain("required (NOT NULL)"));
        Assert.That(explained, Does.Contain("CreatedAt \"created_at\" (datetime2(3)) without zone, UTC, 3 fractional digits"));
    }

    private static string Nullable(string table, string column) =>
        $"SELECT CASE WHEN is_nullable = 1 THEN 'YES' ELSE 'NO' END FROM sys.columns WHERE object_id = OBJECT_ID('{table}') AND name = '{column}'";

    private static string Type(string column) =>
        $"SELECT ty.name + '(' + CAST(c.scale AS varchar(11)) + ')' FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID('stamp_row') AND c.name = '{column}'";

    private static async Task<object?> Scalar(SqlServerTestDatabase db, string sql)
    {
        await using var command = db.Resolve<DbDataSource>().CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }
}
