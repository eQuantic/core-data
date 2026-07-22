using System.Collections.Generic;

namespace eQuantic.Core.Data.Query;

/// <summary>A comparison operator in the dialect-agnostic filter model.</summary>
public enum ComparisonOperator
{
    /// <summary><c>=</c></summary>
    Equal,

    /// <summary><c>&lt;&gt;</c></summary>
    NotEqual,

    /// <summary><c>&gt;</c></summary>
    GreaterThan,

    /// <summary><c>&gt;=</c></summary>
    GreaterThanOrEqual,

    /// <summary><c>&lt;</c></summary>
    LessThan,

    /// <summary><c>&lt;=</c></summary>
    LessThanOrEqual,
}

/// <summary>A logical combinator in the dialect-agnostic filter model.</summary>
public enum LogicalOperator
{
    /// <summary>Conjunction.</summary>
    And,

    /// <summary>Disjunction.</summary>
    Or,

    /// <summary>Negation (single operand).</summary>
    Not,
}

/// <summary>
///     The dialect-agnostic intermediate representation of a filter, produced by <see cref="FilterInterpreter" />
///     from a typed predicate and rendered to a target query (CQL, SQL, …) by a provider. Members are referenced
///     by their resolved path so a renderer can map them to columns.
/// </summary>
public abstract class QueryFilter;

/// <summary>A member compared to a constant: <c>member op value</c>.</summary>
/// <param name="member">The member path (e.g. <c>Total</c> or <c>Customer.Name</c>).</param>
/// <param name="op">The comparison operator.</param>
/// <param name="value">The compared value.</param>
public sealed class ComparisonFilter(string member, ComparisonOperator op, object? value) : QueryFilter
{
    /// <summary>The member path.</summary>
    public string Member { get; } = member;

    /// <summary>The comparison operator.</summary>
    public ComparisonOperator Operator { get; } = op;

    /// <summary>The compared value.</summary>
    public object? Value { get; } = value;
}

/// <summary>A substring position in the dialect-agnostic filter model.</summary>
public enum StringOperator
{
    /// <summary>The member starts with the value.</summary>
    StartsWith,

    /// <summary>The member ends with the value.</summary>
    EndsWith,

    /// <summary>The member contains the value.</summary>
    Contains,
}

/// <summary>
///     A substring test over a string member (<c>member LIKE 'value%'</c> and friends). Case sensitivity follows
///     the store's collation — the same rule string equality already follows.
/// </summary>
/// <param name="member">The member path.</param>
/// <param name="op">The substring position.</param>
/// <param name="value">The tested substring.</param>
public sealed class StringFilter(string member, StringOperator op, string value) : QueryFilter
{
    /// <summary>The member path.</summary>
    public string Member { get; } = member;

    /// <summary>The substring position.</summary>
    public StringOperator Operator { get; } = op;

    /// <summary>The tested substring.</summary>
    public string Value { get; } = value;
}

/// <summary>A member constrained to a set of values: <c>member IN (values)</c>.</summary>
/// <param name="member">The member path.</param>
/// <param name="values">The candidate values.</param>
public sealed class InFilter(string member, IReadOnlyList<object?> values) : QueryFilter
{
    /// <summary>The member path.</summary>
    public string Member { get; } = member;

    /// <summary>The candidate values.</summary>
    public IReadOnlyList<object?> Values { get; } = values;
}

/// <summary>
///     A membership test over a collection member: <c>member CONTAINS value</c>, or (<paramref name="key" />)
///     <c>member CONTAINS KEY value</c> for a map's key.
/// </summary>
/// <param name="member">The collection member path.</param>
/// <param name="value">The value (or key) tested for membership.</param>
/// <param name="key">Whether the test is for a map key (<c>CONTAINS KEY</c>) rather than a value (<c>CONTAINS</c>).</param>
public sealed class CollectionFilter(string member, object? value, bool key) : QueryFilter
{
    /// <summary>The collection member path.</summary>
    public string Member { get; } = member;

    /// <summary>The value (or key) tested for membership.</summary>
    public object? Value { get; } = value;

    /// <summary>Whether the test is for a map key.</summary>
    public bool Key { get; } = key;
}

/// <summary>A row-wise comparison of several members against several values: <c>(m1, m2) op (v1, v2)</c>.</summary>
/// <param name="members">The member paths, in order.</param>
/// <param name="op">The comparison operator.</param>
/// <param name="values">The values, in order.</param>
public sealed class TupleComparisonFilter(IReadOnlyList<string> members, ComparisonOperator op, IReadOnlyList<object?> values) : QueryFilter
{
    /// <summary>The member paths, in order.</summary>
    public IReadOnlyList<string> Members { get; } = members;

    /// <summary>The comparison operator.</summary>
    public ComparisonOperator Operator { get; } = op;

    /// <summary>The values, in order.</summary>
    public IReadOnlyList<object?> Values { get; } = values;
}

/// <summary>A logical combination of filters (<c>And</c>/<c>Or</c>, or a single-operand <c>Not</c>).</summary>
/// <param name="op">The logical operator.</param>
/// <param name="operands">The operands (one for <see cref="LogicalOperator.Not" />).</param>
public sealed class LogicalFilter(LogicalOperator op, IReadOnlyList<QueryFilter> operands) : QueryFilter
{
    /// <summary>The logical operator.</summary>
    public LogicalOperator Operator { get; } = op;

    /// <summary>The operands.</summary>
    public IReadOnlyList<QueryFilter> Operands { get; } = operands;
}
