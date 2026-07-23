using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>
///     Proves the source-generated accessors are live in this suite: the generator (referenced as an analyzer,
///     the way the shipped package wires it) registered accessors for the test entities, so every read and
///     write in the other 50+ tests runs through generated code instead of reflection — the suite itself is
///     the correctness proof; these tests pin the registration and the contract.
/// </summary>
[TestFixture]
public sealed class PostgreSqlGeneratedAccessorTests
{
    [Test]
    public void The_generator_registered_accessors_for_the_test_entities()
    {
        Assert.That(EntityAccessors.For(typeof(SaleOrder)), Is.Not.Null,
            "the module initializer the generator emits must have registered SaleOrder");
        Assert.That(EntityAccessors.For(typeof(OrderLine)), Is.Not.Null);
        Assert.That(EntityAccessors.For(typeof(Article)), Is.Not.Null);
    }

    [Test]
    public void Generated_accessors_create_read_and_write_without_reflection()
    {
        var accessor = EntityAccessors.For(typeof(Article))!;

        var article = (Article)accessor.Create();
        accessor.Set(article, nameof(Article.Title), "generated");

        Assert.That(article.Title, Is.EqualTo("generated"));
        Assert.That(accessor.Get(article, nameof(Article.Title)), Is.EqualTo("generated"));
        Assert.That(accessor.Get(article, "NoSuchMember"), Is.Null, "unknown members read as null, matching reflection misses");
    }
}
