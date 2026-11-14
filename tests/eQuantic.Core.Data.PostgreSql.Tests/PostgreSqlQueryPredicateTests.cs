using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>
///     The query predicates against a real PostgreSQL: filters across relations as correlated <c>EXISTS</c> in reads,
///     counts and set-based writes, text matched with <c>lower()</c> on both sides or <c>ILIKE</c>, and <c>Contains</c> over an array or a
///     list as <c>IN</c>.
/// </summary>
[TestFixture]
public sealed class PostgreSqlQueryPredicateTests : PostgreSqlIntegrationTest
{
    private PostgreSqlTestDatabase _db = null!;

    [SetUp]
    public async Task CreateDatabase() => _db = await NewSchemaAsync();

    [TearDown]
    public void DropDatabase() => _db?.Dispose();

    private IServiceScope NewScope() => _db.NewScope();

    [Test]
    public Task Any_with_a_condition() => QueryPredicateScenarios.AnyWithACondition(NewScope);

    [Test]
    public Task A_reference_member_in_a_set_based_delete() => QueryPredicateScenarios.ReferenceMemberInSetBasedDelete(NewScope);

    [Test]
    public Task No_related_row() => QueryPredicateScenarios.NoRelatedRow(NewScope);

    [Test]
    public void A_relation_test_reading_the_entity_is_refused() => QueryPredicateScenarios.CorrelationIsRefused(NewScope);

    [Test]
    public void Explain_names_the_correlated_exists() => QueryPredicateScenarios.ExplainNamesTheMechanism(NewScope);

    [Test]
    public Task Text_is_matched_without_regard_to_case() => QueryPredicateScenarios.TextWithoutRegardToCase(NewScope);

    [Test]
    public Task An_array_and_a_list_are_tested_with_Contains() => QueryPredicateScenarios.ArrayAndListContains(NewScope);
}
