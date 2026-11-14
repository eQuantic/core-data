using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Core.Data.Cassandra.Repository;
using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>
///     Prepares a filter for the CQL planner: an array's <c>Contains</c> normalized, and each test across a relation
///     resolved into the related keys — a relation crosses tables, which CQL cannot join: the related rows' keys are read
///     first (<see cref="CassandraInclude.RelatedKeysAsync{TTarget}" />, one read per test, under the read's opt-ins), then
///     an <c>IN</c> over them, which the planner pushes down where the table's key serves it and evaluates client-side
///     otherwise (behind the same opt-ins as any filter).
///     <para>
///         A navigation follows the conventions an include follows, over entities the model registers: a collection of rows
///         holding a foreign key back to the owner (<c>[ForeignKey("Owner")]</c> or <c>{Owner}Id</c>), naming the owner's
///         key column, or a reference whose foreign key the owner holds (<c>{Nav}Id</c>), naming the related row's key column.
///     </para>
/// </summary>
internal static class CassandraPredicates
{
    /// <summary>The navigations of the entities a unit of work maps.</summary>
    public static Func<Type, PropertyInfo, RelationNavigation?> Navigations(CassandraUnitOfWork unitOfWork) => (owner, member) =>
    {
        if (member.PropertyType == typeof(string) || unitOfWork.ConfigurationOf(owner) is null)
        {
            return null;
        }

        if (RelationCountInterpreter.ElementType(member.PropertyType) is { IsClass: true } element && element != typeof(string))
        {
            return unitOfWork.ConfigurationOf(element) is not null && CassandraInclude.ForeignKey(element, owner.Name) is { } foreignKey
                ? new RelationNavigation(owner, member, element, isCollection: true, CassandraInclude.Key(unitOfWork, owner), foreignKey)
                : null;
        }

        return unitOfWork.ConfigurationOf(member.PropertyType) is not null && CassandraInclude.ForeignKey(owner, member.Name) is { } referenceKey
            ? new RelationNavigation(owner, member, member.PropertyType, isCollection: false, referenceKey,
                CassandraInclude.Key(unitOfWork, member.PropertyType))
            : null;
    };

    /// <summary>
    ///     A read's filter ready for the planner: normalized, and each relation resolved into the related keys, read under the
    ///     opt-ins of <paramref name="options" />.
    /// </summary>
    public static async Task<Expression<Func<T, bool>>> ResolveAsync<T>(Expression<Func<T, bool>> filter, CassandraUnitOfWork unitOfWork,
        object? options, CancellationToken cancellationToken) =>
        await RelationPredicates.ResolveAsync(PredicateNormalizer.Normalize(filter), Navigations(unitOfWork), Keys(unitOfWork, options),
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    ///     A set-based write's filter ready for the planner: normalized, and each relation resolved into the related keys. A
    ///     write carries no opt-ins, so each related read is partition-scoped or refused: by the relation's condition, or by
    ///     what <paramref name="scope" /> answers for a relation of the written entity — the values the rows the write finds
    ///     relate by (<see cref="RelationNavigation.OwnerKey" />), <c>null</c> when the write's filter does not name its
    ///     partition. A relation inside a relation's condition is read by its condition alone.
    /// </summary>
    public static async Task<Expression<Func<T, bool>>> ResolveForWriteAsync<T>(Expression<Func<T, bool>> filter,
        CassandraUnitOfWork unitOfWork, Func<RelationPredicate, Task<IReadOnlyCollection<object?>?>>? scope, CancellationToken cancellationToken)
    {
        var normalized = PredicateNormalizer.Normalize(filter);
        var navigations = Navigations(unitOfWork);
        var relations = RelationPredicates.Find(normalized, navigations);

        // The keys are read one relation at a time, in the order Find answers them.
        var next = 0;
        return await RelationPredicates.ResolveAsync(normalized, navigations, (target, condition, member, token) =>
        {
            var relation = relations[next++];
            return Read(target, unitOfWork, condition, member, null, true,
                scope is null ? null : () => scope(relation), token);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The filter with every relation replaced by an empty key list, for a plan that runs nothing.</summary>
    public static Expression<Func<T, bool>> Unresolved<T>(Expression<Func<T, bool>> filter, CassandraUnitOfWork unitOfWork) =>
        RelationPredicates.ResolveAsync(PredicateNormalizer.Normalize(filter), Navigations(unitOfWork),
            (_, _, _, _) => Task.FromResult<IReadOnlyCollection<object?>>([]), CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>What a filter's relations read first, for a plan (no read runs).</summary>
    public static IEnumerable<string> Notes(LambdaExpression filter, CassandraUnitOfWork unitOfWork) =>
        RelationPredicates.Find(PredicateNormalizer.Normalize(filter), Navigations(unitOfWork)).Select(relation =>
            $"'{relation.Text}' resolves first: the related table ('{unitOfWork.ConfigurationOf(relation.Navigation.Target)!.TableName}') is " +
            $"read for the distinct {relation.Navigation.TargetKey.Name} of the rows that match (under this read's opt-ins), and this read " +
            $"takes {relation.Navigation.OwnerKey.Name} {(relation.Negated ? "NOT IN (client-side)" : "IN")} those keys; Explain runs nothing, " +
            "so the list shows empty here.");

    private static RelatedKeysReader Keys(CassandraUnitOfWork unitOfWork, object? options) => (target, condition, member, cancellationToken) =>
        Read(target, unitOfWork, condition, member, options, false, null, cancellationToken);

    private static Task<IReadOnlyCollection<object?>> Read(Type target, CassandraUnitOfWork unitOfWork, LambdaExpression? condition,
        PropertyInfo member, object? options, bool write, Func<Task<IReadOnlyCollection<object?>?>>? scope, CancellationToken cancellationToken) =>
        (Task<IReadOnlyCollection<object?>>)typeof(CassandraPredicates)
            .GetMethod(nameof(DistinctAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(target)
            .Invoke(null, [unitOfWork, condition, member, options, write, scope, cancellationToken])!;

    private static async Task<IReadOnlyCollection<object?>> DistinctAsync<TTarget>(CassandraUnitOfWork unitOfWork, LambdaExpression? condition,
        PropertyInfo member, object? options, bool write, Func<Task<IReadOnlyCollection<object?>?>>? scope,
        CancellationToken cancellationToken) where TTarget : class
    {
        var typed = (Expression<Func<TTarget, bool>>?)condition;
        var resolved = typed is null ? null
            : write ? await ResolveForWriteAsync(typed, unitOfWork, null, cancellationToken).ConfigureAwait(false)
            : await ResolveAsync(typed, unitOfWork, options, cancellationToken).ConfigureAwait(false);
        return await CassandraInclude.RelatedKeysAsync(unitOfWork, resolved, member, options,
            $"the related read of '{typeof(TTarget).Name}' a relation test makes", write, scope).ConfigureAwait(false);
    }
}
