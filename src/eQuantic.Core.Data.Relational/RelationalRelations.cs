using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Relational;

/// <summary>
///     What a statement needs to render a filter that crosses a relation as a correlated <c>EXISTS</c>: the model's
///     navigations (declared with <c>Reference(...)</c>/<c>Collection(...)</c>, or by the <c>{Nav}Id</c> /
///     <c>{Entity}Id</c> conventions an include follows) and the related entity's global filter, which the subquery
///     honours as a read of that entity would. One instance serves one statement: it numbers the subqueries' aliases.
/// </summary>
internal sealed class RelationalRelations(RelationalModel model, Func<Type, LambdaExpression?> scope)
{
    private const BindingFlags Members = BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance;
    private int _aliases;

    /// <summary>The model the related entities are mapped in.</summary>
    public RelationalModel Model { get; } = model;

    /// <summary>The related entity's global filter (its tenant scope, its live rows), or <c>null</c>.</summary>
    public LambdaExpression? Scope(Type entityType) => scope(entityType);

    /// <summary>A fresh alias for a subquery of the statement.</summary>
    public string NextAlias() => "eq_rel" + _aliases++;

    /// <summary>The navigation a member of a mapped entity is, or <c>null</c> when it is not one.</summary>
    public RelationNavigation? Navigation(Type owner, PropertyInfo member) => Navigation(Model, owner, member);

    /// <summary>The navigation a member of a mapped entity is in a model, or <c>null</c> when it is not one.</summary>
    public static RelationNavigation? Navigation(RelationalModel model, Type owner, PropertyInfo member)
    {
        if (!model.Configurations.TryGetValue(owner, out var ownerConfiguration))
        {
            return null;
        }

        var declared = ownerConfiguration.NavigationFor(member.Name);
        if (RelationCountInterpreter.ElementType(member.PropertyType) is { } element && model.Configurations.TryGetValue(element, out var elements))
        {
            var foreignKeyName = declared?.ForeignKey ?? owner.Name + "Id";
            var foreignKey = elements.ColumnFor(foreignKeyName)?.Property
                             ?? throw new NotSupportedException(
                                 $"'{owner.Name}.{member.Name}' is a collection of '{element.Name}', which has no '{foreignKeyName}' member " +
                                 $"naming its '{owner.Name}'; declare the navigation with Collection(...).");
            RequireSimpleKey(ownerConfiguration, member);
            return new RelationNavigation(owner, member, element, isCollection: true, ownerConfiguration.Key.Property, foreignKey);
        }

        if (!model.Configurations.TryGetValue(member.PropertyType, out var target))
        {
            return null;
        }

        var referenceKeyName = declared?.ForeignKey ?? member.Name + "Id";
        var referenceKey = owner.GetProperty(referenceKeyName, Members)
                           ?? throw new NotSupportedException(
                               $"'{owner.Name}.{member.Name}' references '{member.PropertyType.Name}', and '{owner.Name}' has no " +
                               $"'{referenceKeyName}' member naming it; declare the navigation with Reference(...).");
        RequireSimpleKey(target, member);
        return new RelationNavigation(owner, member, member.PropertyType, isCollection: false, referenceKey, target.Key.Property);
    }

    private static void RequireSimpleKey(RelationalEntityConfiguration configuration, PropertyInfo member)
    {
        if (configuration.HasCompositeKey)
        {
            throw new NotSupportedException(
                $"A filter across '{member.Name}' relates the rows by one key, and '{configuration.EntityType.Name}' has a composite key.");
        }
    }

    /// <summary>Whether a filter crosses a relation that reads <paramref name="table" /> — directly, or through a related row's own relations.</summary>
    public bool ReadsTable(LambdaExpression? filter, Type table) => ReadsTable(filter, table, 0);

    private bool ReadsTable(LambdaExpression? filter, Type table, int depth) =>
        filter is not null && depth < 8
                           && RelationPredicates.Find(PredicateNormalizer.Normalize(filter), Navigation).Any(relation =>
                               relation.Navigation.Target == table
                               || ReadsTable(relation.Condition, table, depth + 1)
                               || ReadsTable(Scope(relation.Navigation.Target), table, depth + 1));

    /// <summary>
    ///     The top-level conjuncts of the filter and the global filter that do <b>not</b> read <paramref name="table" />,
    ///     joined over the filter's parameter — what a write by locked keys still applies — or <c>null</c> when none is left.
    /// </summary>
    public LambdaExpression? Without(LambdaExpression filter, LambdaExpression? global, Type table)
    {
        var whole = And(filter, global)!;
        var kept = Conjuncts(whole.Body)
            .Where(conjunct => !ReadsTable(Expression.Lambda(conjunct, whole.Parameters), table))
            .ToList();
        return kept.Count == 0
            ? null
            : Expression.Lambda(whole.Type, kept.Aggregate(Expression.AndAlso), whole.Parameters);
    }

    private static IEnumerable<Expression> Conjuncts(Expression body) =>
        body is BinaryExpression { NodeType: ExpressionType.AndAlso } and ? Conjuncts(and.Left).Concat(Conjuncts(and.Right)) : [body];

    /// <summary>The conjunction of two conditions over the same entity (either may be <c>null</c>).</summary>
    public static LambdaExpression? And(LambdaExpression? left, LambdaExpression? right)
    {
        if (left is null || right is null)
        {
            return left ?? right;
        }

        var body = new Substitution(right.Parameters[0], left.Parameters[0]).Visit(right.Body);
        return Expression.Lambda(left.Type, Expression.AndAlso(left.Body, body), left.Parameters);
    }

    private sealed class Substitution(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
