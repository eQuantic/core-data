using eQuantic.Core.Data.Cassandra.Extensions;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Data.Repository.Read;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>
///     Exercises SASI-backed <c>LIKE</c> pushdown against a real cluster: a column the model declares with
///     <c>SearchIndex(...)</c> serves <c>StartsWith</c>/<c>EndsWith</c>/<c>Contains</c> and <c>Db.Like</c>
///     natively (the migration creates the index, no scan opt-in needed), while undeclared columns and
///     unservable patterns keep degrading to the gated client-side residual.
/// </summary>
[TestFixture]
public sealed class CassandraSearchTests : CassandraIntegrationTest
{
    private static Reading At(int sensor, int hour, string quality) => new()
    {
        SensorId = sensor,
        At = new DateTime(2026, 1, 1, hour, 0, 0, DateTimeKind.Utc),
        Value = 1d,
        Quality = quality,
    };

    [Test]
    public async Task Search_indexed_column_serves_string_matches_natively()
    {
        using var db = await NewSchemaAsync();
        await Seed(db, At(1, 0, "excellent"), At(1, 1, "good"), At(2, 0, "poor"));
        var repo = ReadingRepo(db);

        // No .AllowFiltering(), no .AllowClientEvaluation() — the SASI index serves the match.
        Assert.That((await repo.GetFilteredAsync(x => x.Quality.StartsWith("go"))).Single().Quality, Is.EqualTo("good"));
        Assert.That((await repo.GetFilteredAsync(x => x.Quality.EndsWith("ent"))).Single().Quality, Is.EqualTo("excellent"));
        Assert.That((await repo.GetFilteredAsync(x => x.Quality.Contains("oo"))).Select(x => x.Quality),
            Is.EquivalentTo(new[] { "good", "poor" }));
        Assert.That((await repo.GetFilteredAsync(x => Db.Like(x.Quality, "%cell%"))).Single().Quality, Is.EqualTo("excellent"),
            "Db.Like patterns pass through to the SASI index");
    }

    [Test]
    public async Task Explain_shows_the_native_like_without_gates()
    {
        using var db = await NewSchemaAsync();
        var plan = ((IExplainableRepository<Reading>)ReadingRepo(db))
            .Explain(new QueryOptions<Reading>().Where(x => x.Quality.StartsWith("go")));

        Assert.That(plan.Statement, Does.Contain("Quality LIKE ?"));
        Assert.That(plan.ClientEvaluation, Is.False, "the index serves the match — nothing runs client-side");
    }

    [Test]
    public async Task Undeclared_columns_and_unservable_patterns_stay_gated_residual()
    {
        using var db = await NewSchemaAsync();
        await Seed(db, new Account { Id = Guid.NewGuid(), Owner = "alice", Balance = 1m });
        var accounts = AccountRepo(db);

        Assert.That(async () => await accounts.GetFilteredAsync(x => x.Owner.StartsWith("al")),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("AllowClientEvaluation"),
            "a column without a search index keeps the honest residual gate");

        var matched = await accounts.GetFilteredAsync(x => x.Owner.StartsWith("al"),
            new QueryOptions<Account>().AllowClientEvaluation().AllowFiltering());
        Assert.That(matched.Single().Owner, Is.EqualTo("alice"));

        await Seed(db, At(9, 0, "100%"));
        var literal = await ReadingRepo(db).GetFilteredAsync(x => x.Quality.Contains("%"),
            new QueryOptions<Reading>().AllowClientEvaluation().AllowFiltering());
        Assert.That(literal.Single().Quality, Is.EqualTo("100%"),
            "a literal wildcard cannot push down (no escape in SASI LIKE) and runs client-side");
    }
}
