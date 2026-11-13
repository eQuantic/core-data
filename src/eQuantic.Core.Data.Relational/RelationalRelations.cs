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

    /// <summary>The conjunction of two conditions over the same entity (either may be <c>null</c>).</summary>
    public static LambdaExpression? And(LambdaExpression? left, LambdaExpression? right)
    {
        if (left is null || right is null)
        {
            return left ?? right;
        }

        var body = new Substitution(right.Parameters[0], left.Parameters[0]).Visit(right.Body);
        return Expression.Lambda(Expression.AndAlso(left.Body, body), left.Parameters);
    }

    private sealed class Substitution(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
