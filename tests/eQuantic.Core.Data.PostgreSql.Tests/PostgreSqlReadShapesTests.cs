using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>
///     The read shapes against a real PostgreSQL: a shaped include ranked with <c>ROW_NUMBER()</c>, relation counts as
///     correlated subqueries, nulls placed with <c>NULLS FIRST</c>/<c>NULLS LAST</c>, a page without its total, and
///     <c>SELECT DISTINCT</c>.
/// </summary>
[TestFixture]
public sealed class PostgreSqlReadShapesTests : PostgreSqlIntegrationTest
{
    private static async Task<(PostgreSqlTestDatabase Db, ShapeStore Store)> NewStoreAsync()
    {
        var commands = new CommandCapture();
        var db = await NewSchemaAsync(commands.Register);
        return (db, new ShapeStore(type => Resolve(db, type), () => Uow(db).CommitAsync(), commands));
    }

    private static object Resolve(PostgreSqlTestDatabase db, Type type) =>
        typeof(PostgreSqlTestDatabase).GetMethod(nameof(PostgreSqlTestDatabase.Resolve))!.MakeGenericMethod(type).Invoke(db, null)!;

    [Test]
    public async Task An_include_is_filtered_ordered_and_limited_per_parent()
    {
        var (db, store) = await NewStoreAsync();
        using (db)
        {
            await ReadShapeScenarios.TheLastThreeNewestFirst(store);
        }
    }

    [Test]
    public async Task A_relation_is_counted_not_loaded()
    {
        var (db, store) = await NewStoreAsync();
        using (db)
        {
            await ReadShapeScenarios.CountedNotLoaded(store);
        }
    }

    [Test]
    public async Task Nulls_are_ordered_first_or_last()
    {
        var (db, store) = await NewStoreAsync();
        using (db)
        {
            await ReadShapeScenarios.NullsFirstOrLast(store);
        }
    }

    [Test]
    public async Task A_page_is_read_without_its_total()
    {
        var (db, store) = await NewStoreAsync();
        using (db)
        {
            await ReadShapeScenarios.TakeTenWithoutTotal(store);
        }
    }

    [Test]
    public async Task A_skip_without_a_take_returns_every_row_after_it()
    {
        var (db, store) = await NewStoreAsync();
        using (db)
        {
            await ReadShapeScenarios.SkipWithoutTakeAndTakeWithoutSkip(store);
        }
    }

    [Test]
    public async Task A_take_of_zero_returns_nothing_without_asking_the_store()
    {
        var (db, store) = await NewStoreAsync();
        using (db)
        {
            await ReadShapeScenarios.TakeZero(store);
        }
    }

    [Test]
    public async Task Includes_of_more_parents_than_one_statement_binds()
    {
        var (db, store) = await NewStoreAsync();
        using (db)
        {
            await ReadShapeScenarios.IncludesPastTheParameterLimit(store);
        }
    }

    [Test]
    public async Task Distinct_values_of_two_members()
    {
        var (db, store) = await NewStoreAsync();
        using (db)
        {
            await ReadShapeScenarios.DistinctOfTwoMembers(store);
        }
    }

    [Test]
    public async Task The_closed_generic_registration_is_a_distinct_reader_too()
    {
        using var db = await NewSchemaAsync(services =>
            eQuantic.Core.Data.PostgreSql.Extensions.PostgreSqlServiceCollectionExtensions.AddPostgreSqlRepository<ShapeComment, Guid>(services));
        var repo = db.Resolve<IAsyncRepository<ShapeComment, Guid>>();
        await repo.AddAsync(new ShapeComment { Repository = "aot", IssueNumber = 7 });
        await Uow(db).CommitAsync();

        var distinct = repo as eQuantic.Core.Data.Repository.Read.IDistinctReadRepository<ShapeComment>;
        Assert.That(distinct, Is.Not.Null, "the capability is cast to, as every read capability is, on the AOT path too");
        Assert.That(await distinct!.GetDistinctAsync(x => x.IssueNumber, x => x.Repository == "aot"), Is.EqualTo(new[] { 7 }));
    }

    [Test]
    public async Task Explain_renders_nulls_natively_and_names_the_shaped_include()
    {
        using var db = await NewSchemaAsync();
        var plan = ((eQuantic.Core.Data.Repository.Read.IExplainableRepository<ShapeRun>)db.Resolve<IAsyncRepository<ShapeRun, Guid>>())
            .Explain(new QueryOptions<ShapeRun>()
                .OrderBy(x => x.Name, NullsOrder.First)
                .Include(x => x.Steps, steps => steps.OrderByDescending(s => s.StartedAt).Take(3)));

        Assert.That(plan.Statement, Does.Contain("ORDER BY \"name\" ASC NULLS FIRST"));
        Assert.That(plan.Notes, Has.Some.Contains("ROW_NUMBER()"));
    }
}
