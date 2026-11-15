using System.Data.Common;
using System.Text.Json.Nodes;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.MySql.Extensions;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace eQuantic.Core.Data.MySql.Tests;

/// <summary>A <c>DateTime</c> over a <c>DATE</c> column and <c>TimeSpan</c>s over <c>TIME</c> columns, keyed by a <c>char(36)</c> Guid.</summary>
[Modeling.Entity("calendar_rows")]
public sealed class CalendarRow : IEntity<Guid>
{
    public Guid Id { get; set; }

    public DateTime Day { get; set; }

    public TimeSpan Starts { get; set; }

    public TimeSpan? Ends { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>
///     What mapping a schema another tool created needs, against a real MySQL: a JSON member that leaves the
///     other members alone, required columns, and dates stored without their zone.
/// </summary>
[TestFixture]
public sealed class MySqlSchemaFidelityTests : MySqlIntegrationTest
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
        Assert.That(async () => await Scalar(db, $"INSERT INTO stamp_row (id, created_at) VALUES ('{Guid.NewGuid()}', UTC_TIMESTAMP())"),
            Throws.InstanceOf<DbException>(), "the store refuses a row that leaves a required column null");
    }

    [Test]
    public async Task A_date_without_its_zone_has_its_declared_precision_and_comes_back_as_utc()
    {
        using var db = await NewSchemaAsync();
        Assert.That(await Scalar(db, Type("created_at")), Is.EqualTo("datetime(3)"));
        Assert.That(await Scalar(db, Type("seen_at")), Is.EqualTo("datetime(6)"), "no precision keeps the store's own");

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
        Assert.That(explained, Does.Contain("CreatedAt \"created_at\" (datetime(3)) without zone, UTC, 3 fractional digits"));
    }

    [Test]
    public async Task A_DateTime_over_a_DATE_column_and_a_TimeSpan_over_a_TIME_column_read_as_they_did_on_7_0()
    {
        var name = "db_" + Guid.NewGuid().ToString("N");
        await using (var admin = new MySqlConnection(MySqlTestServer.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new MySqlCommand($"CREATE DATABASE {name}", admin);
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new MySqlConnectionStringBuilder(MySqlTestServer.AdminConnectionString) { Database = name }.ConnectionString;
        var raw = Guid.NewGuid();
        await using (var connection = new MySqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var table = new MySqlCommand(
                "CREATE TABLE calendar_rows (id char(36) PRIMARY KEY, day date NOT NULL, starts time(3) NOT NULL, ends time(3) NULL)", connection);
            await table.ExecuteNonQueryAsync();
            await using var insert = new MySqlCommand(
                $"INSERT INTO calendar_rows VALUES ('{raw}', '2026-11-15', '09:30:15.250', NULL)", connection);
            await insert.ExecuteNonQueryAsync();
        }

        var services = new ServiceCollection();
        services.AddMySqlDatabase(connectionString, model => model.Entity<CalendarRow>(_ => { }));
        services.AddMySqlRepositories();
        await using var provider = services.BuildServiceProvider();
        var written = new CalendarRow { Id = Guid.NewGuid(), Day = new DateTime(2026, 11, 16), Starts = new TimeSpan(0, 8, 0, 0, 500), Ends = new TimeSpan(17, 45, 0) };
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAsyncRepository<CalendarRow, Guid>>().AddAsync(written);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IAsyncRepository<CalendarRow, Guid>>();
            var seeded = (await repo.GetAsync(raw))!;
            Assert.That(seeded.Id, Is.EqualTo(raw), "a char(36) key reads as the Guid it holds");
            Assert.That(seeded.Day, Is.EqualTo(new DateTime(2026, 11, 15)));
            Assert.That(seeded.Starts, Is.EqualTo(new TimeSpan(0, 9, 30, 15, 250)));
            Assert.That(seeded.Ends, Is.Null);

            var back = (await repo.GetAsync(written.Id))!;
            Assert.That(back.Day, Is.EqualTo(written.Day));
            Assert.That(back.Starts, Is.EqualTo(written.Starts));
            Assert.That(back.Ends, Is.EqualTo(written.Ends));
            Assert.That(await ((eQuantic.Core.Data.Repository.Read.IAggregateReadRepository<CalendarRow>)repo).MaxAsync(x => x.Day),
                Is.EqualTo(new DateTime(2026, 11, 16)));
        }
    }

    private static string Nullable(string table, string column) =>
        $"SELECT is_nullable FROM information_schema.columns WHERE table_schema = database() AND table_name = '{table}' AND column_name = '{column}'";

    private static string Type(string column) =>
        $"SELECT column_type FROM information_schema.columns WHERE table_schema = database() AND table_name = 'stamp_row' AND column_name = '{column}'";

    private static async Task<object?> Scalar(MySqlTestDatabase db, string sql)
    {
        await using var command = db.Resolve<DbDataSource>().CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }
}
