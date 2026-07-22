using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>
///     Proves the <c>[SearchIndex]</c> → GIN trigram bridge against a real PostgreSQL: the migration's
///     <c>EnsureCollection()</c> materializes the <c>pg_trgm</c> index, and substring matches keep their
///     semantics (they simply stop scanning).
/// </summary>
[TestFixture]
public sealed class PostgreSqlSearchIndexTests : PostgreSqlIntegrationTest
{
    [Test]
    public async Task EnsureCollection_materializes_the_trigram_index_and_like_still_matches()
    {
        using var db = await NewSchemaAsync();

        var dataSource = db.Resolve<System.Data.Common.DbDataSource>();
        await using var indexes = dataSource.CreateCommand(
            "SELECT indexdef FROM pg_indexes WHERE tablename = 'articles' AND indexname = 'ix_articles_title_search'");
        var definition = (string?)await indexes.ExecuteScalarAsync();
        Assert.That(definition, Is.Not.Null, "[SearchIndex] materialized an index through EnsureCollection()");
        Assert.That(definition, Does.Contain("USING gin").And.Contain("gin_trgm_ops"),
            "the index is a GIN trigram (pg_trgm), which serves leading-wildcard LIKE");

        var repo = db.Resolve<IAsyncRepository<Article, Guid>>();
        await repo.AddAsync(new Article { Title = "Guia de PostgreSQL avançado" });
        await repo.AddAsync(new Article { Title = "Cassandra na prática" });
        await Uow(db).CommitAsync();

        var matches = await repo.GetFilteredAsync(x => x.Title.Contains("PostgreSQL"));
        Assert.That(matches.Single().Title, Does.Contain("PostgreSQL"),
            "Contains keeps its semantics — the index only changes the plan");
    }
}
