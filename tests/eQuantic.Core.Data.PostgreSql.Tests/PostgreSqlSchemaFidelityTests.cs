using System.Linq.Expressions;
using System.Text.Json.Nodes;
using eQuantic.Core.Data.Evolution;
using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.PostgreSql.Extensions;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Relational.Migration;
using eQuantic.Core.Data.Repository;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>A table another tool created, mapped by name: what a drift check of a foreign schema meets.</summary>
[Entity("foreign_rows")]
public sealed class ForeignRow : IEntity<string>
{
    [EntityKey, StoredAs("id")]
    public string Id { get; set; } = "";

    [StoredAs("name"), Facet(Required = true)]
    public string Name { get; set; } = "";

    [StoredAs("quantity"), Facet(Required = true)]
    public int Quantity { get; set; }

    [StoredAs("seen"), Facet(Required = true), DateStorage(WithoutZone = true, Precision = 3)]
    public DateTime Seen { get; set; }

    [StoredAs("data")]
    public JsonNode? Data { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>
///     A <c>DateTime</c> member over a <c>date</c> column and <c>TimeSpan</c> members over <c>time</c> columns, as a schema
///     another tool created holds them: what Npgsql 10 reads as <c>DateOnly</c> and <c>TimeOnly</c>.
/// </summary>
[Entity("calendar_rows")]
public sealed class CalendarRow : IEntity<Guid>
{
    public Guid Id { get; set; }

    public DateTime Day { get; set; }

    public TimeSpan Starts { get; set; }

    public TimeSpan? Ends { get; set; }

    public DateTime? Holiday { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A member declared required, added to a table that already holds a row.</summary>
[Entity("legacy_stamps")]
public sealed class LegacyStamp : IEntity<Guid>
{
    public Guid Id { get; set; }

    public string Code { get; set; } = "";

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A date declaration on a member that is not a date.</summary>
public sealed class TextDate : IEntity<Guid>
{
    public Guid Id { get; set; }

    [DateStorage(WithoutZone = true)]
    public string When { get; set; } = "";

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A date declaration with more digits than any store keeps.</summary>
public sealed class FineDate : IEntity<Guid>
{
    public Guid Id { get; set; }

    [DateStorage(WithoutZone = true, Precision = 7)]
    public DateTime At { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>
///     What mapping a schema another tool created needs, against a real PostgreSQL: a JSON member that leaves the
///     other members alone, required columns, and dates stored without their zone.
/// </summary>
[TestFixture]
public sealed class PostgreSqlSchemaFidelityTests : PostgreSqlIntegrationTest
{
    private const string SaoPaulo = "America/Sao_Paulo";

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

        Assert.That(await NotNull(db, "stamp_row", "name"), Is.True, "[Facet(Required = true)] creates the column NOT NULL");
        Assert.That(await NotNull(db, "stamp_row", "created_at"), Is.True);
        Assert.That(await NotNull(db, "stamp_row", "note"), Is.False, "a member not declared required stays nullable");
        Assert.That(await NotNull(db, "stamp_row", "closed_at"), Is.False);
        Assert.That(async () => await Execute(db,
                $"INSERT INTO stamp_row (id, created_at) VALUES ('{Guid.NewGuid()}', now())"),
            Throws.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo("23502"));
    }

    [Test]
    public async Task Adding_a_required_column_without_a_default_is_refused_before_anything_runs()
    {
        using var db = await NewSchemaAsync();
        var executor = new RelationalMigrationExecutor(db.Resolve<System.Data.Common.DbDataSource>(), db.Resolve<SqlDialect>(),
            db.Resolve<RelationalModel>());
        var ran = false;
        var operations = new MigrationOperation[]
        {
            new RunOperation((_, _) =>
            {
                ran = true;
                return Task.CompletedTask;
            }),
            new AddFieldOperation(typeof(StampRow), (Expression<Func<StampRow, string>>)(x => x.Name)),
        };

        var refusal = Assert.ThrowsAsync<InvalidOperationException>(() => executor.ApplyAsync(operations));

        Assert.That(refusal!.Message, Does.Contain("StampRow.Name").And.Contain("DefaultValue"));
        Assert.That(ran, Is.False, "nothing runs when a step is refused");
    }

    [Test]
    public async Task Adding_a_required_column_with_a_default_fills_the_rows_already_there()
    {
        using var db = await NewSchemaAsync();
        await Execute(db, "CREATE TABLE legacy_stamps (id uuid PRIMARY KEY)");
        var existing = Guid.NewGuid();
        await Execute(db, $"INSERT INTO legacy_stamps (id) VALUES ('{existing}')");
        await using var services = Services(await ConnectionStringAsync(db), model => model
            .Entity<LegacyStamp>(entity => entity.Required(x => x.Code).Default(x => x.Code, "none")));
        var executor = new RelationalMigrationExecutor(services.GetRequiredService<System.Data.Common.DbDataSource>(),
            services.GetRequiredService<SqlDialect>(), services.GetRequiredService<RelationalModel>());

        await executor.ApplyAsync([new AddFieldOperation(typeof(LegacyStamp), (Expression<Func<LegacyStamp, string>>)(x => x.Code))]);

        Assert.That(await NotNull(db, "legacy_stamps", "code"), Is.True);
        Assert.That(await Scalar(db, $"SELECT code FROM legacy_stamps WHERE id = '{existing}'"), Is.EqualTo("none"));
    }

    [Test]
    public async Task The_session_time_zone_does_not_move_a_date_without_its_zone()
    {
        using var db = await NewSchemaAsync();
        var connectionString = await ConnectionStringAsync(db);
        var written = new DateTime(2026, 11, 15, 10, 20, 30, 123, DateTimeKind.Utc).AddTicks(4567);
        var row = new StampRow { Name = "sp", CreatedAt = written };

        await using (var saoPaulo = Services(connectionString + $";Timezone={SaoPaulo}", model => SchemaFidelitySchema.Configure(model)))
        await using (var scope = saoPaulo.CreateAsyncScope())
        {
            Assert.That(await SessionTimeZone(scope.ServiceProvider), Is.EqualTo(SaoPaulo));
            await scope.ServiceProvider.GetRequiredService<IAsyncRepository<StampRow, Guid>>().AddAsync(row);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync();
        }

        Assert.That(await Scalar(db, $"SELECT created_at::text FROM stamp_row WHERE id = '{row.Id}'"),
            Is.EqualTo("2026-11-15 10:20:30.123"), "the column holds the UTC wall time, rounded to the declared precision");

        using var read = db.NewScope();
        var back = await read.ServiceProvider.GetRequiredService<IAsyncRepository<StampRow, Guid>>().GetAsync(row.Id);
        Assert.That(back!.CreatedAt, Is.EqualTo(new DateTime(2026, 11, 15, 10, 20, 30, 123, DateTimeKind.Utc)));
        Assert.That(back.CreatedAt.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public async Task A_date_without_its_zone_is_rounded_half_away_from_zero_and_read_back_as_utc()
    {
        using var db = await NewSchemaAsync();
        var repo = db.Resolve<IAsyncRepository<StampRow, Guid>>();
        var local = new DateTime(2026, 11, 15, 7, 20, 30, 999, DateTimeKind.Unspecified).AddTicks(5000);
        var row = new StampRow
        {
            Name = "round",
            CreatedAt = new DateTime(2026, 11, 15, 10, 20, 30, 123, DateTimeKind.Utc).AddTicks(5000),
            ClosedAt = local,
            SeenAt = new DateTimeOffset(2026, 11, 15, 7, 20, 30, 250, TimeSpan.FromHours(-3)),
        };
        var empty = new StampRow { Name = "empty", CreatedAt = DateTime.UtcNow };

        await repo.AddAsync(row);
        await repo.AddAsync(empty);
        await Uow(db).CommitAsync();

        using var scope = db.NewScope();
        var reader = scope.ServiceProvider.GetRequiredService<IAsyncRepository<StampRow, Guid>>();
        var back = (await reader.GetAsync(row.Id))!;
        Assert.That(back.CreatedAt, Is.EqualTo(new DateTime(2026, 11, 15, 10, 20, 30, 124, DateTimeKind.Utc)),
            ".1235 rounds up, half away from zero");
        Assert.That(back.ClosedAt, Is.EqualTo(new DateTime(2026, 11, 15, 7, 20, 31, 0, DateTimeKind.Utc)),
            "an Unspecified value is taken as UTC, and .9995 carries into the next second");
        Assert.That(back.ClosedAt!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
        Assert.That(back.SeenAt, Is.EqualTo(new DateTimeOffset(2026, 11, 15, 10, 20, 30, 250, TimeSpan.Zero)));
        Assert.That(back.SeenAt!.Value.Offset, Is.EqualTo(TimeSpan.Zero), "a DateTimeOffset comes back with offset zero");
        var blank = (await reader.GetAsync(empty.Id))!;
        Assert.That(blank.ClosedAt, Is.Null);
        Assert.That(blank.SeenAt, Is.Null);
    }

    [Test]
    public async Task Filters_and_set_based_updates_compare_a_date_without_its_zone_as_utc()
    {
        using var db = await NewSchemaAsync();
        var connectionString = await ConnectionStringAsync(db);
        var ten = new StampRow { Name = "ten", CreatedAt = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc) };
        var eleven = new StampRow { Name = "eleven", CreatedAt = new DateTime(2026, 11, 15, 11, 0, 0, DateTimeKind.Utc) };

        await using var saoPaulo = Services(connectionString + $";Timezone={SaoPaulo}", model => SchemaFidelitySchema.Configure(model));
        await using (var scope = saoPaulo.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IAsyncRepository<StampRow, Guid>>();
            await repo.AddAsync(ten);
            await repo.AddAsync(eleven);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync();
        }

        await using (var scope = saoPaulo.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IAsyncRepository<StampRow, Guid>>();
            var halfPast = new DateTime(2026, 11, 15, 10, 30, 0, DateTimeKind.Utc);
            var before = await repo.GetFilteredAsync(x => x.CreatedAt < halfPast && (x.Name == "ten" || x.Name == "eleven"));
            Assert.That(before.Select(x => x.Name), Is.EqualTo(new[] { "ten" }));

            var closed = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
            await repo.UpdateManyAsync(x => x.Id == eleven.Id, x => new StampRow { ClosedAt = closed });
        }

        Assert.That(await Scalar(db, $"SELECT closed_at::text FROM stamp_row WHERE id = '{eleven.Id}'"),
            Is.EqualTo("2026-11-15 12:00:00"), "a set-based update writes UTC whatever the session's time zone");
    }

    [Test]
    public void A_date_declaration_that_cannot_apply_is_refused_when_the_model_is_built()
    {
        Assert.That(() => new ServiceCollection().AddPostgreSqlDatabase("Host=unused", model => model.Entity<TextDate>(_ => { })),
            Throws.InvalidOperationException.With.Message.Contain("TextDate.When").And.Message.Contain("DateTime or DateTimeOffset"));
        Assert.That(() => new ServiceCollection().AddPostgreSqlDatabase("Host=unused", model => model.Entity<FineDate>(_ => { })),
            Throws.InvalidOperationException.With.Message.Contain("FineDate.At").And.Message.Contain("precision of 7"));
        Assert.That(() => new ServiceCollection().AddPostgreSqlDatabase("Host=unused", model => model.Entity<MixedJsonRow>(entity => entity
                .Converts(x => x.At, at => at.Ticks, ticks => new DateTime(ticks, DateTimeKind.Utc))
                .DateStorage(x => x.At, withoutZone: true))),
            Throws.InvalidOperationException.With.Message.Contain("MixedJsonRow.At").And.Message.Contain("converter"));
    }

    [Test]
    public async Task Explain_names_required_members_and_dates_without_their_zone()
    {
        using var db = await NewSchemaAsync();

        var explained = db.Resolve<RelationalModel>().Explain(db.Resolve<SqlDialect>());

        Assert.That(explained, Does.Contain("Name \"name\" (text) required (NOT NULL)"));
        Assert.That(explained, Does.Contain(
            "CreatedAt \"created_at\" (timestamp(3) without time zone) without zone, UTC, 3 fractional digits required (NOT NULL)"));
        Assert.That(explained, Does.Contain("SeenAt \"seen_at\" (timestamp without time zone) without zone, UTC"));
    }

    [Test]
    public async Task A_table_another_tool_created_reports_nothing()
    {
        using var db = await NewSchemaAsync();
        await Execute(db, "CREATE TABLE foreign_rows (id text PRIMARY KEY, name text NOT NULL, quantity integer NOT NULL, " +
                          "seen timestamp(3) without time zone NOT NULL, data jsonb)");

        var findings = await DriftAsync(db);

        Assert.That(findings, Is.Empty, "a schema matching the model, types with precisions included, is silent");
    }

    [Test]
    public async Task A_precision_that_differs_is_one_type_finding()
    {
        using var db = await NewSchemaAsync();
        await Execute(db, "CREATE TABLE foreign_rows (id text PRIMARY KEY, name text NOT NULL, quantity integer NOT NULL, " +
                          "seen timestamp(6) without time zone NOT NULL, data jsonb)");

        var finding = (await DriftAsync(db)).Single();

        Assert.That(finding.Kind, Is.EqualTo(DriftKind.TypeDiffers));
        Assert.That(finding.Field, Is.EqualTo("seen"));
        Assert.That(finding.Expected, Is.EqualTo("timestamp(3) without time zone"));
        Assert.That(finding.Found, Is.EqualTo("timestamp(6) without time zone"));
    }

    [Test]
    public async Task Nullability_is_compared_with_what_the_model_declares()
    {
        using var db = await NewSchemaAsync();
        // name is required and nullable here; data is not declared required and NOT NULL here.
        await Execute(db, "CREATE TABLE foreign_rows (id text PRIMARY KEY, name text, quantity integer NOT NULL, " +
                          "seen timestamp(3) without time zone NOT NULL, data jsonb NOT NULL)");

        var findings = (await DriftAsync(db)).OrderBy(finding => finding.Field).ToList();

        Assert.That(findings.Select(finding => (finding.Kind, finding.Field, finding.Expected, finding.Found)), Is.EqualTo(new[]
        {
            (DriftKind.NullabilityDiffers, "data", "null allowed", "not null"),
            (DriftKind.NullabilityDiffers, "name", "not null", "null allowed"),
        }));
    }

    [Test]
    public async Task A_DateTime_over_a_date_column_and_a_TimeSpan_over_a_time_column_read_as_they_did_on_7_0()
    {
        using var db = await NewSchemaAsync();
        await Execute(db, "CREATE TABLE calendar_rows (id uuid PRIMARY KEY, day date NOT NULL, starts time NOT NULL, ends time, holiday date)");
        var raw = Guid.NewGuid();
        await Execute(db, $"INSERT INTO calendar_rows VALUES ('{raw}', '2026-11-15', '09:30:15.25', NULL, '2026-12-25')");
        await using var services = Services(await ConnectionStringAsync(db), model => model.Entity<CalendarRow>(_ => { }));
        var written = new CalendarRow
        {
            Id = Guid.NewGuid(), Day = new DateTime(2026, 11, 16), Starts = new TimeSpan(0, 8, 0, 0, 500), Ends = new TimeSpan(17, 45, 0),
        };

        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAsyncRepository<CalendarRow, Guid>>().AddAsync(written);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync();
        }

        await using (var scope = services.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IAsyncRepository<CalendarRow, Guid>>();
            var seeded = (await repo.GetAsync(raw))!;
            Assert.That(seeded.Day, Is.EqualTo(new DateTime(2026, 11, 15)), "a date reads as a DateTime at midnight");
            Assert.That(seeded.Day.Kind, Is.EqualTo(DateTimeKind.Unspecified), "with no kind, as Npgsql 9 read it");
            Assert.That(seeded.Starts, Is.EqualTo(new TimeSpan(0, 9, 30, 15, 250)), "a time reads as a TimeSpan");
            Assert.That(seeded.Ends, Is.Null);
            Assert.That(seeded.Holiday, Is.EqualTo(new DateTime(2026, 12, 25)));

            var back = (await repo.GetAsync(written.Id))!;
            Assert.That(back.Day, Is.EqualTo(written.Day));
            Assert.That(back.Starts, Is.EqualTo(written.Starts));
            Assert.That(back.Ends, Is.EqualTo(written.Ends));
            Assert.That(back.Holiday, Is.Null);

            var mapped = await repo.GetMappedAsync(x => new { x.Day, x.Starts }, new eQuantic.Core.Data.Repository.Options.QueryOptions<CalendarRow>().Where(x => x.Id == raw));
            Assert.That(mapped.Single().Day, Is.EqualTo(new DateTime(2026, 11, 15)), "a projection reads it the same way");
            Assert.That(mapped.Single().Starts, Is.EqualTo(new TimeSpan(0, 9, 30, 15, 250)));
            Assert.That(await ((eQuantic.Core.Data.Repository.Read.IAggregateReadRepository<CalendarRow>)repo).MaxAsync(x => x.Day),
                Is.EqualTo(new DateTime(2026, 11, 16)), "and so does an aggregate");
        }
    }

    [TestCase("timestamp(3) without time zone", "timestamp(3) without time zone")]
    [TestCase("TIMESTAMP(3) WITHOUT TIME ZONE", "timestamp(3) without time zone")]
    [TestCase("timestamp", "timestamp without time zone")]
    [TestCase("timestamptz", "timestamp with time zone")]
    [TestCase("timestamptz(3)", "timestamp(3) with time zone")]
    [TestCase("timestamp ( 3 )", "timestamp(3) without time zone")]
    [TestCase("time(6) with time zone", "time(6) with time zone")]
    [TestCase("timetz", "time with time zone")]
    [TestCase("time", "time without time zone")]
    [TestCase("timestamp(3) without time zone[]", "timestamp(3) without time zone[]")]
    [TestCase("character varying(200)", "varchar(200)")]
    [TestCase("numeric(18, 2)", "numeric(18,2)")]
    [TestCase("\"RunStatus\"", "runstatus")]
    public void A_type_with_a_facet_inside_its_name_is_spelt_whole(string written, string normalized) =>
        Assert.That(new PostgreSqlDialect().NormalizeStoredType(written), Is.EqualTo(normalized));

    private async Task<List<DriftFinding>> DriftAsync(PostgreSqlTestDatabase db)
    {
        await using var services = Services(await ConnectionStringAsync(db), model => model.Entity<ForeignRow>(_ => { }));
        var source = services.GetRequiredService<IDatabaseSnapshotSource>();
        return DriftComparer.Compare(source.Expect(), await source.ObserveAsync()).Findings.ToList();
    }

    private static ServiceProvider Services(string connectionString, Action<RelationalModelBuilder> model)
    {
        var services = new ServiceCollection();
        services.AddPostgreSqlDatabase(connectionString, builder => model(builder));
        services.AddPostgreSqlRepositories();
        return services.BuildServiceProvider();
    }

    private static Task<string> ConnectionStringAsync(PostgreSqlTestDatabase db)
    {
        var database = new NpgsqlConnectionStringBuilder(db.Resolve<NpgsqlDataSource>().ConnectionString).Database;
        return Task.FromResult(new NpgsqlConnectionStringBuilder(PostgreSqlTestServer.AdminConnectionString) { Database = database }.ConnectionString);
    }

    private static async Task<string> SessionTimeZone(IServiceProvider services)
    {
        await using var command = services.GetRequiredService<NpgsqlDataSource>().CreateCommand("SHOW TimeZone");
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> NotNull(PostgreSqlTestDatabase db, string table, string column) =>
        (bool)(await Scalar(db,
            $"SELECT attnotnull FROM pg_attribute WHERE attrelid = '{table}'::regclass AND attname = '{column}'"))!;

    private static async Task<object?> Scalar(PostgreSqlTestDatabase db, string sql)
    {
        await using var command = db.Resolve<NpgsqlDataSource>().CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }

    private static async Task Execute(PostgreSqlTestDatabase db, string sql)
    {
        await using var command = db.Resolve<NpgsqlDataSource>().CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
