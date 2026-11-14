using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Core.Data.Modeling;

namespace eQuantic.Core.Data.Query;

/// <summary>
///     The order of an enum stored as its labels (<see cref="EnumTypeAttribute" />, or <c>Enum&lt;TEnum&gt;()</c> on a
///     model builder) is its members' <b>values</b>, as C# compares them: never the labels' text, which MySQL, SQL Server,
///     MongoDB, Cosmos DB and Cassandra would compare, nor the order PostgreSQL declared its enum type in. Every provider
///     asks this class instead: an ordered comparison (<c>x.Priority &gt;= Priority.Medium</c>) becomes membership in the
///     labels whose member satisfies it (<see cref="Satisfying" />), and an order by such a member orders by the value each
///     label stands for (<see cref="Ranks" />).
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class EnumOrder
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<(string Label, Enum Member, decimal Value)>> Cache = new();

    /// <summary>Whether <paramref name="op" /> orders (<c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>) rather than tests equality.</summary>
    /// <param name="op">The operator.</param>
    public static bool IsOrdered(ComparisonOperator op) =>
        op is ComparisonOperator.GreaterThan or ComparisonOperator.GreaterThanOrEqual
            or ComparisonOperator.LessThan or ComparisonOperator.LessThanOrEqual;

    /// <summary>
    ///     Each label of <paramref name="enumType" /> and the value of the member it stands for, ordered by that value (ties,
    ///     which only aliases make, in declaration order).
    /// </summary>
    /// <param name="enumType">The enum (nullable unwrapped).</param>
    public static IReadOnlyList<KeyValuePair<string, decimal>> Ranks(Type enumType) =>
        Members(enumType).Select(member => new KeyValuePair<string, decimal>(member.Label, member.Value)).ToList();

    /// <summary>The members of <paramref name="enumType" />, one per value, in the order of their values.</summary>
    /// <param name="enumType">The enum (nullable unwrapped).</param>
    public static IReadOnlyList<Enum> ByValue(Type enumType) =>
        Members(enumType).GroupBy(member => member.Value).Select(group => group.First().Member).ToList();

    /// <summary>
    ///     The members of <paramref name="enumType" /> whose value satisfies <c>member <paramref name="op" />
    ///     <paramref name="value" /></c>, one per value, in value order. The value may be the enum itself or its number (C#
    ///     compiles <c>x.Priority &gt;= Priority.Medium</c> into a comparison of numbers).
    /// </summary>
    /// <param name="enumType">The enum (nullable unwrapped).</param>
    /// <param name="op">An ordered operator (<see cref="IsOrdered" />).</param>
    /// <param name="value">The compared value.</param>
    public static IReadOnlyList<Enum> Satisfying(Type enumType, ComparisonOperator op, object value)
    {
        var bound = Number(value);
        return Members(enumType)
            .Where(member => op switch
            {
                ComparisonOperator.GreaterThan => member.Value > bound,
                ComparisonOperator.GreaterThanOrEqual => member.Value >= bound,
                ComparisonOperator.LessThan => member.Value < bound,
                ComparisonOperator.LessThanOrEqual => member.Value <= bound,
                _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Only an ordered comparison selects members by their value."),
            })
            .GroupBy(member => member.Value)
            .Select(group => group.First().Member)
            .ToList();
    }

    /// <summary>
    ///     The enum a selector reads when its body is a member (or a conversion of one, <c>x =&gt; (int)x.Priority</c>) whose
    ///     enum the store keeps as its labels; <c>null</c> otherwise.
    /// </summary>
    /// <param name="selector">A member selector.</param>
    /// <param name="isLabelEnum">Whether the store keeps an enum (nullable unwrapped) as its labels.</param>
    public static Type? LabelEnumOf(LambdaExpression selector, Func<Type, bool> isLabelEnum)
    {
        var body = selector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conversion)
        {
            body = conversion.Operand;
        }

        var type = Nullable.GetUnderlyingType(body.Type) ?? body.Type;
        return body is MemberExpression && type.IsEnum && isLabelEnum(type) ? type : null;
    }

    /// <summary>
    ///     What a minimum or a maximum over a member stored as labels answers, from the member's value: the member itself
    ///     when the selector reads the enum, its number when it converts it (<c>x =&gt; (int)x.Priority</c>), as C# does;
    ///     <c>default</c> when there is no value (nothing matched, or every match is null).
    /// </summary>
    /// <typeparam name="TResult">What the selector answers.</typeparam>
    /// <param name="enumType">The enum.</param>
    /// <param name="value">The member's value, or <c>null</c>.</param>
    public static TResult? FromValue<TResult>(Type enumType, decimal? value)
    {
        if (value is not { } number)
        {
            return default;
        }

        var target = Nullable.GetUnderlyingType(typeof(TResult)) ?? typeof(TResult);
        var underlying = Convert.ChangeType(number, Enum.GetUnderlyingType(enumType), CultureInfo.InvariantCulture);
        return (TResult)(target.IsEnum
            ? Enum.ToObject(target, underlying)
            : Convert.ChangeType(underlying, target, CultureInfo.InvariantCulture));
    }

    /// <summary>
    ///     The smallest or the largest value among members read back (an enum's members, nulls skipped), as C# compares them;
    ///     <c>null</c> when there is none.
    /// </summary>
    /// <param name="members">The members present.</param>
    /// <param name="largest">Whether the largest is asked for.</param>
    public static decimal? Extreme(IEnumerable<object?> members, bool largest)
    {
        var values = members.OfType<Enum>().Select(Number).ToList();
        return values.Count == 0 ? null : largest ? values.Max() : values.Min();
    }

    /// <summary>
    ///     The refusal of an arithmetic aggregate (a sum, an average) over a member stored as labels: the store holds its
    ///     labels, which have no arithmetic, and summing a label or its position would answer another question.
    /// </summary>
    /// <param name="enumType">The enum.</param>
    /// <param name="aggregate">The aggregate, as the message names it (<c>SumAsync</c>).</param>
    public static NotSupportedException ArithmeticRefusal(Type enumType, string aggregate) =>
        new($"{aggregate} over a member of '{enumType.Name}', which the store keeps as its labels: a label has no arithmetic, " +
            "and the store would sum the text or its position, never the members' values. Read the members and add their " +
            "values client-side, or store the enum as its number.");

    /// <summary>
    ///     Refuses an aggregate over a member stored as an enum's labels, in a group's projection or its <c>HAVING</c>, on a
    ///     group the store computes: the store would sum or average the labels, or take the minimum or the maximum of their
    ///     text, never of the members' values. A group computed client-side aggregates the members themselves and needs no
    ///     refusal; <c>MinAsync</c>/<c>MaxAsync</c> answer by value.
    /// </summary>
    /// <param name="group">The interpreted group.</param>
    /// <param name="having">The interpreted <c>HAVING</c>, or <c>null</c>.</param>
    /// <param name="labelEnumOf">The enum a member path is stored as the labels of, or <c>null</c>.</param>
    public static void RefuseLabelAggregates(GroupQuery group, GroupPredicate? having, Func<string, Type?> labelEnumOf)
    {
        var aggregated = group.Bindings.OfType<GroupAggregateBinding>().Select(binding => (binding.Aggregate, binding.Member))
            .Concat(having is null ? [] : HavingAggregates(having));
        foreach (var (aggregate, member) in aggregated)
        {
            if (member is not null && labelEnumOf(member) is { } labels)
            {
                throw aggregate is GroupAggregate.Sum or GroupAggregate.Average
                    ? ArithmeticRefusal(labels, $"A grouped {aggregate}")
                    : new NotSupportedException(
                        $"A grouped {aggregate} over '{member}', a member of '{labels.Name}' which the store keeps as its labels, would " +
                        "compare the labels' text, not the members' values. Ask MinAsync/MaxAsync with the group's filter, which answer " +
                        "by value, or group the rows client-side.");
            }
        }

        static IEnumerable<(GroupAggregate Aggregate, string? Member)> HavingAggregates(GroupPredicate node) => node switch
        {
            GroupComparison { Aggregate: { } aggregate } comparison => [(aggregate, comparison.Member)],
            GroupLogical logical => logical.Operands.SelectMany(HavingAggregates),
            _ => [],
        };
    }

    /// <summary>
    ///     The expression with every ordered comparison of a member stored as labels (<c>x.Priority &gt;= Priority.Medium</c>,
    ///     on a nullable member too, and under a negation) rewritten into membership in the members that satisfy it
    ///     (<c>new[] { Priority.Medium, Priority.High }.Contains(x.Priority)</c>), the shape a document store's LINQ translation
    ///     renders as an <c>$in</c> or an <c>ARRAY_CONTAINS</c> over the labels. A comparison with <c>null</c> is false, as in C#.
    ///     The same instance when there is none.
    /// </summary>
    /// <typeparam name="TExpression">The expression's type.</typeparam>
    /// <param name="expression">A predicate, or a whole query.</param>
    /// <param name="isLabelEnum">Whether the store keeps an enum (nullable unwrapped) as its labels.</param>
    public static TExpression RewriteComparisons<TExpression>(TExpression expression, Func<Type, bool> isLabelEnum)
        where TExpression : Expression =>
        (TExpression)new ComparisonRewriter(isLabelEnum).Visit(expression);

    private sealed class ComparisonRewriter(Func<Type, bool> isLabelEnum) : ExpressionVisitor
    {
        private static readonly MethodInfo Contains = typeof(Enumerable).GetMethods()
            .Single(method => method.Name == nameof(Enumerable.Contains) && method.GetParameters().Length == 2);

        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (Operator(node.NodeType) is not { } op)
            {
                return base.VisitBinary(node);
            }

            if (Member(node.Left) is { } left && Value(node.Right, out var right))
            {
                return Membership(left, op, right);
            }

            if (Member(node.Right) is { } member && Value(node.Left, out var value))
            {
                return Membership(member, Flip(op), value);
            }

            return base.VisitBinary(node);
        }

        private Expression Membership(Expression member, ComparisonOperator op, object? value)
        {
            var enumType = Nullable.GetUnderlyingType(member.Type) ?? member.Type;
            var satisfying = value is null ? [] : Satisfying(enumType, op, value);
            var array = Array.CreateInstance(member.Type, satisfying.Count);
            for (var index = 0; index < satisfying.Count; index++)
            {
                array.SetValue(satisfying[index], index);
            }

            return Expression.Call(Contains.MakeGenericMethod(member.Type), Expression.Constant(array), member);
        }

        /// <summary>The member a side reads (its compiler-inserted conversions stripped) when the store keeps its enum as labels.</summary>
        private Expression? Member(Expression side)
        {
            var unwrapped = Unwrap(side);
            var type = Nullable.GetUnderlyingType(unwrapped.Type) ?? unwrapped.Type;
            return unwrapped is MemberExpression && type.IsEnum && isLabelEnum(type) && Reads(unwrapped) ? unwrapped : null;
        }

        /// <summary>The value of a side that reads no parameter.</summary>
        private static bool Value(Expression side, out object? value)
        {
            value = null;
            if (Reads(side))
            {
                return false;
            }

            var unwrapped = Unwrap(side);
            value = unwrapped is ConstantExpression constant
                ? constant.Value
                : Expression.Lambda(Expression.Convert(unwrapped, typeof(object))).Compile(preferInterpretation: true).DynamicInvoke();
            return true;
        }

        private static Expression Unwrap(Expression expression)
        {
            while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conversion)
            {
                expression = conversion.Operand;
            }

            return expression;
        }

        private static bool Reads(Expression expression)
        {
            var finder = new ParameterFinder();
            finder.Visit(expression);
            return finder.Found;
        }

        private static ComparisonOperator? Operator(ExpressionType type) => type switch
        {
            ExpressionType.GreaterThan => ComparisonOperator.GreaterThan,
            ExpressionType.GreaterThanOrEqual => ComparisonOperator.GreaterThanOrEqual,
            ExpressionType.LessThan => ComparisonOperator.LessThan,
            ExpressionType.LessThanOrEqual => ComparisonOperator.LessThanOrEqual,
            _ => null,
        };

        private static ComparisonOperator Flip(ComparisonOperator op) => op switch
        {
            ComparisonOperator.GreaterThan => ComparisonOperator.LessThan,
            ComparisonOperator.GreaterThanOrEqual => ComparisonOperator.LessThanOrEqual,
            ComparisonOperator.LessThan => ComparisonOperator.GreaterThan,
            _ => ComparisonOperator.GreaterThanOrEqual,
        };

        private sealed class ParameterFinder : ExpressionVisitor
        {
            public bool Found { get; private set; }

            protected override Expression VisitParameter(ParameterExpression node)
            {
                Found = true;
                return node;
            }
        }
    }

    private static IReadOnlyList<(string Label, Enum Member, decimal Value)> Members(Type enumType)
    {
        var type = Nullable.GetUnderlyingType(enumType) ?? enumType;
        return Cache.GetOrAdd(type, static type =>
        {
            if (!type.IsEnum)
            {
                throw new ArgumentException($"'{type.Name}' is not an enum.", nameof(enumType));
            }

            var labels = EnumLabels.Of(type);
            return type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select((field, index) => (Field: field, Index: index, Member: (Enum)field.GetValue(null)!))
                .Select(entry => (Label: labels[entry.Field.Name], entry.Member, Value: Number(entry.Member), entry.Index))
                .OrderBy(entry => entry.Value)
                .ThenBy(entry => entry.Index)
                .Select(entry => (entry.Label, entry.Member, entry.Value))
                .ToList();
        });
    }

    /// <summary>The number an enum member (or a number standing for one) is.</summary>
    private static decimal Number(object value) =>
        value is Enum member
            ? Convert.ToDecimal(Convert.ChangeType(member, Enum.GetUnderlyingType(member.GetType()), CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture)
            : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
}
