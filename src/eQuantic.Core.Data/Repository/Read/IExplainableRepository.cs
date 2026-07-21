using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.Repository.Read;

/// <summary>
///     A read repository that can explain — without executing — what a shaped query will actually do: the native
///     statement, what is pushed down versus evaluated client-side, and the cost flags. Providers opt in; use it
///     in code review and diagnostics to make a query's cost visible before it ships.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public interface IExplainableRepository<TEntity>
    where TEntity : class
{
    /// <summary>
    ///     Builds the execution plan for a read shaped by <paramref name="options" />. No I/O is performed and no
    ///     cost gate is enforced — a plan that would be rejected at execution time is still returned, with its
    ///     required opt-ins listed in <see cref="QueryPlan.Notes" />.
    /// </summary>
    /// <param name="options">The query shaping, or <c>null</c> for an unshaped read.</param>
    /// <returns>The plan.</returns>
    QueryPlan Explain(QueryOptions<TEntity>? options = null);
}
