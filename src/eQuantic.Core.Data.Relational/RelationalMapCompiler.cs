using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;

namespace eQuantic.Core.Data.Relational;

/// <summary>
///     Builds reader-direct projectors for the common map shapes — a constructor projection
///     (<c>p =&gt; new Row(p.Id, p.Name)</c>, records and anonymous types), a member-init projection
///     (<c>p =&gt; new Dto { Id = p.Id }</c>) and a single member read (<c>p =&gt; p.Name</c>) — where every
///     source is a plain selected column. The projector reads cells by ordinal through the same per-cell
///     pipeline entity materialization uses (converters, nullable unwrap, jsonb/array shaping) and invokes the
///     cached constructor/setters: <b>no expression compilation</b>, so there is no per-query JIT to amortize
///     (the lesson the translation layer already learned). Any other shape refuses with <c>null</c> and the
///     caller falls back to the entity-shell path — same results, one more hop.
/// </summary>
internal static class RelationalMapCompiler
{
    /// <summary>Builds the reader projector, or <c>null</c> when the map's shape is not reader-direct.</summary>
    /// <typeparam name="TEntity">The entity type the map reads from.</typeparam>
    /// <typeparam name="TResult">The projected result type.</typeparam>
    /// <param name="map">The projection.</param>
    /// <param name="selected">The columns the SELECT lists, in ordinal order.</param>
    public static Func<DbDataReader, TResult>? TryCompile<TEntity, TResult>(
        Expression<Func<TEntity, TResult>> map, IReadOnlyList<RelationalColumn> selected)
    {
        var entity = map.Parameters[0];

        switch (map.Body)
        {
            // p => new Row(p.Id, p.Name, ...) — records, anonymous types, positional DTOs.
            case NewExpression { Constructor: not null } projection:
            {
                var sources = new CellSource[projection.Arguments.Count];
                for (var index = 0; index < projection.Arguments.Count; index++)
                {
                    if (Source(projection.Arguments[index], entity, selected) is not { } source)
                    {
                        return null;
                    }

                    sources[index] = source;
                }

                var constructor = projection.Constructor;
                return reader =>
                {
                    var arguments = new object?[sources.Length];
                    for (var index = 0; index < sources.Length; index++)
                    {
                        arguments[index] = sources[index].Read(reader);
                    }

                    return (TResult)constructor.Invoke(arguments);
                };
            }

            // p => new Dto { Id = p.Id, ... } — member-init over a parameterless constructor.
            case MemberInitExpression { NewExpression.Arguments.Count: 0 } init:
            {
                var setters = new (CellSource Source, PropertyInfo Property)[init.Bindings.Count];
                for (var index = 0; index < init.Bindings.Count; index++)
                {
                    if (init.Bindings[index] is not MemberAssignment { Member: PropertyInfo property } assignment
                        || Source(assignment.Expression, entity, selected) is not { } source)
                    {
                        return null;
                    }

                    setters[index] = (source, property);
                }

                return reader =>
                {
                    var result = Activator.CreateInstance<TResult>()!;
                    foreach (var (source, property) in setters)
                    {
                        property.SetValue(result, source.Read(reader));
                    }

                    return result;
                };
            }

            // p => p.Name — a single column read.
            default:
            {
                if (Source(map.Body, entity, selected) is not { } single)
                {
                    return null;
                }

                return reader => (TResult)single.Read(reader)!;
            }
        }
    }

    /// <summary>The cell behind a map argument — a direct member access on the entity parameter, or nothing.</summary>
    private static CellSource? Source(Expression argument, ParameterExpression entity, IReadOnlyList<RelationalColumn> selected)
    {
        // Unwrap the conversions the compiler inserts around value-type members in object-typed slots.
        while (argument is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary
               && unary.Type.IsAssignableFrom(unary.Operand.Type))
        {
            argument = unary.Operand;
        }

        if (argument is not MemberExpression { Expression: ParameterExpression parameter } member
            || !ReferenceEquals(parameter, entity))
        {
            return null;
        }

        for (var ordinal = 0; ordinal < selected.Count; ordinal++)
        {
            if (string.Equals(selected[ordinal].Property.Name, member.Member.Name, StringComparison.OrdinalIgnoreCase))
            {
                return new CellSource(ordinal, selected[ordinal], member.Type);
            }
        }

        return null;
    }

    /// <summary>One selected column feeding the projection (NULL cells yield the member's default, as entity reads do).</summary>
    private sealed class CellSource(int ordinal, RelationalColumn column, Type memberType)
    {
        private readonly object? _nullValue =
            memberType.IsValueType && Nullable.GetUnderlyingType(memberType) is null
                ? Activator.CreateInstance(memberType)
                : null;

        public object? Read(DbDataReader reader) =>
            reader.IsDBNull(ordinal)
                ? _nullValue
                : RelationalMaterializer.Cell(column, reader.GetValue(ordinal), memberType);
    }
}
