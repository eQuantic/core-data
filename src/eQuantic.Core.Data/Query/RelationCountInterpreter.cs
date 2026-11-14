using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace eQuantic.Core.Data.Query;

/// <summary>
///     One relation count a projection asks for: <c>x.Versions.Count()</c>, <c>x.Versions.Count(v =&gt; v.DeletedAt == null)</c>,
///     <c>LongCount</c>, or the collection's <c>Count</c> property.
/// </summary>
/// <param name="Navigation">The collection navigation counted.</param>
/// <param name="ElementType">The related entity type.</param>
/// <param name="Predicate">The filter the counted entities match (<c>Func&lt;TElement, bool&gt;</c>), or <c>null</c> for all.</param>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed record RelationCount(PropertyInfo Navigation, Type ElementType, LambdaExpression? Predicate);

/// <summary>
///     A projection whose relation counts were taken out: <see cref="Counts" /> are computed by the store, and
///     <see cref="Projection" /> is the original map with each count read from the array the store's counts fill,
///     in the same order.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TResult">The projected type.</typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class RelationCountProjection<TEntity, TResult>
{
    internal RelationCountProjection(IReadOnlyList<RelationCount> counts, Expression<Func<TEntity, long[], TResult>> projection,
        IReadOnlyCollection<string>? members)
    {
        Counts = counts;
        Projection = projection;
        Members = members;
    }

    /// <summary>The counts, in the order the array of <see cref="Projection" /> holds them.</summary>
    public IReadOnlyList<RelationCount> Counts { get; }

    /// <summary>The map with every count replaced by <c>counts[i]</c>, converted to the type the map expects.</summary>
    public Expression<Func<TEntity, long[], TResult>> Projection { get; }

    /// <summary>
    ///     The entity members the projection still reads once the counts are out (first segments), or <c>null</c>
    ///     when it uses the entity whole.
    /// </summary>
    public IReadOnlyCollection<string>? Members { get; }
}

/// <summary>
///     Finds the relation counts in a projection (<c>x =&gt; new { x.Id, Versions = x.Versions.Count(v =&gt; v.DeletedAt == null) }</c>)
///     so a provider can have the store count them — a correlated subquery, a <c>$lookup</c> with <c>$count</c>, a
///     grouped count — instead of loading the related entities to count them in memory, which a projection over an
///     entity whose navigations are not loaded would silently answer with zero.
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class RelationCountInterpreter
{
    /// <summary>
    ///     Takes the relation counts out of <paramref name="map" />, or answers <c>null</c> when it has none. A count
    ///     is a call to <c>Count</c>/<c>LongCount</c> (with or without a predicate) or a read of the <c>Count</c>
    ///     property, on a member of the entity the provider says is a relation.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TResult">The projected type.</typeparam>
    /// <param name="map">The projection.</param>
    /// <param name="isRelation">Whether a member of the entity is a collection of related entities the store counts.</param>
    public static RelationCountProjection<TEntity, TResult>? Interpret<TEntity, TResult>(
        Expression<Func<TEntity, TResult>> map, Func<PropertyInfo, bool> isRelation)
    {
        var entity = map.Parameters[0];
        var counts = Expression.Parameter(typeof(long[]), "counts");
        var rewriter = new Rewriter(entity, counts, isRelation);
        var body = rewriter.Visit(map.Body);
        if (rewriter.Found.Count == 0)
        {
            return null;
        }

        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var whole = !MemberCollector.TryCollect(entity, body, members);
        return new RelationCountProjection<TEntity, TResult>(rewriter.Found,
            Expression.Lambda<Func<TEntity, long[], TResult>>(body, entity, counts), whole ? null : members);
    }

    /// <summary>Whether the type is a collection (not text or bytes), and its element type.</summary>
    /// <param name="type">The member type.</param>
    public static Type? ElementType(Type type)
    {
        if (type == typeof(string) || type == typeof(byte[]))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        return type.GetInterfaces()
            .Append(type)
            .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

    private sealed class Rewriter(ParameterExpression entity, ParameterExpression counts, Func<PropertyInfo, bool> isRelation)
        : ExpressionVisitor
    {
        public List<RelationCount> Found { get; } = [];

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(Enumerable)
                && node.Method.Name is nameof(Enumerable.Count) or nameof(Enumerable.LongCount)
                && node.Arguments.Count is 1 or 2
                && Relation(node.Arguments[0]) is { } navigation)
            {
                var predicate = node.Arguments.Count == 2
                    ? RelationPredicates.Condition(node.Arguments[1], $"{navigation.Name}.{node.Method.Name}")
                    : null;
                return Slot(navigation, predicate, node.Type);
            }

            return base.VisitMethodCall(node);
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            // x.Versions.Count (List<T>, ICollection<T>, IReadOnlyCollection<T>) and x.Versions.Length (arrays).
            if (node.Member.Name is "Count" or "Length"
                && node.Member is PropertyInfo { PropertyType: var type } && (type == typeof(int) || type == typeof(long))
                && node.Expression is not null && Relation(node.Expression) is { } navigation)
            {
                return Slot(navigation, null, node.Type);
            }

            return base.VisitMember(node);
        }

        private PropertyInfo? Relation(Expression source)
        {
            while (source is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.TypeAs } unary)
            {
                source = unary.Operand;
            }

            return source is MemberExpression { Member: PropertyInfo property } member
                   && ReferenceEquals(member.Expression, entity)
                   && ElementType(property.PropertyType) is not null
                   && isRelation(property)
                ? property
                : null;
        }

        private Expression Slot(PropertyInfo navigation, LambdaExpression? predicate, Type type)
        {
            Found.Add(new RelationCount(navigation, ElementType(navigation.PropertyType)!, predicate));
            var read = Expression.ArrayIndex(counts, Expression.Constant(Found.Count - 1));
            return type == typeof(long) ? read : Expression.Convert(read, type);
        }

    }

    /// <summary>Collects the first-segment members a body reads from the entity; false when it uses the entity whole.</summary>
    private sealed class MemberCollector : ExpressionVisitor
    {
        private readonly ParameterExpression _entity;
        private readonly HashSet<string> _members;
        private bool _whole;

        private MemberCollector(ParameterExpression entity, HashSet<string> members)
        {
            _entity = entity;
            _members = members;
        }

        public static bool TryCollect(ParameterExpression entity, Expression body, HashSet<string> members)
        {
            var collector = new MemberCollector(entity, members);
            collector.Visit(body);
            return !collector._whole;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            Expression? root = node;
            MemberExpression? closest = null;
            while (root is MemberExpression member)
            {
                closest = member;
                root = member.Expression;
            }

            if (ReferenceEquals(root, _entity))
            {
                _members.Add(closest!.Member.Name);
                return node;
            }

            return base.VisitMember(node);
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (ReferenceEquals(node, _entity))
            {
                _whole = true;
            }

            return node;
        }
    }
}
