using System.Collections.Generic;

namespace eQuantic.Core.Data.Query;

/// <summary>A comparison operator in the dialect-agnostic filter model.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
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
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
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
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public abstract class QueryFilter;

/// <summary>A member compared to a constant: <c>member op value</c>.</summary>
/// <param name="member">The member path (e.g. <c>Total</c> or <c>Customer.Name</c>).</param>
/// <param name="op">The comparison operator.</param>
/// <param name="value">The compared value.</param>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
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
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
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
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class StringFilter(string member, StringOperator op, string value) : QueryFilter
{
    /// <summary>The member path.</summary>
    public string Member { get; } = member;

    /// <summary>The substring position.</summary>
    public StringOperator Operator { get; } = op;

    /// <summary>The tested substring.</summary>
    public string Value { get; } = value;
}

/// <summary>How a <see cref="TextFilter" /> places the value in the member's text.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public enum TextMatch
{
    /// <summary>The text is the value.</summary>
    Equals,

    /// <summary>The text starts with the value.</summary>
    StartsWith,

    /// <summary>The text ends with the value.</summary>
    EndsWith,

    /// <summary>The text contains the value.</summary>
    Contains,
}

/// <summary>What a <see cref="TextFilter" /> does with letter case.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public enum TextCase
{
    /// <summary>Case is ignored on both sides (<c>StringComparison.OrdinalIgnoreCase</c>).</summary>
    Ignore,

    /// <summary>The member's text is lower-cased and compared with the value as it is (<c>x.Name.ToLower() == v</c>).</summary>
    Lower,

    /// <summary>The member's text is upper-cased and compared with the value as it is (<c>x.Name.ToUpper() == v</c>).</summary>
    Upper,
}

/// <summary>
///     A text test that does not follow the store's collation: equality, prefix, suffix or containment, either without
///     regard to case (<c>string.Equals(x.Name, v, StringComparison.OrdinalIgnoreCase)</c>,
///     <c>x.Name.Contains(v, StringComparison.OrdinalIgnoreCase)</c>) or against the member's lower- or upper-cased text
///     (<c>x.Name.ToLower() == v</c>, <c>x.Name.ToUpper().StartsWith(v)</c>). The value's wildcard characters are
///     literal: a renderer escapes them.
/// </summary>
/// <param name="member">The member path.</param>
/// <param name="match">Where the value has to be in the text.</param>
/// <param name="value">The value.</param>
/// <param name="textCase">What is done with letter case.</param>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class TextFilter(string member, TextMatch match, string value, TextCase textCase) : QueryFilter
{
    /// <summary>The member path.</summary>
    public string Member { get; } = member;

    /// <summary>Where the value has to be in the text.</summary>
    public TextMatch Match { get; } = match;

    /// <summary>The value.</summary>
    public string Value { get; } = value;

    /// <summary>What is done with letter case.</summary>
    public TextCase Case { get; } = textCase;

    /// <summary>
    ///     Whether the test can hold at all: the member's lower-cased text never holds an upper-case letter (nor the
    ///     upper-cased one a lower-case letter), so a value that does matches nothing — exactly as in C#.
    /// </summary>
    public bool CanMatch => Case switch
    {
        TextCase.Lower => Value == Value.ToLowerInvariant(),
        TextCase.Upper => Value == Value.ToUpperInvariant(),
        _ => true,
    };
}

/// <summary>
///     A function applied to a member — used directly as a predicate (<c>fn(member, args)</c>) or compared to a
///     constant (<c>fn(member, args) op value</c>). The function is referenced by <b>name</b>: a dialect renders
///     the names its registry knows (the standard set plus whatever the developer maps) and refuses the rest,
///     which degrade to the gated client-side residual — where the marker method's real C# body runs.
/// </summary>
/// <param name="function">The function name (the marker method's name).</param>
/// <param name="member">The member path the function applies to.</param>
/// <param name="arguments">The constant arguments after the member, in order.</param>
/// <param name="op">The comparison operator, or <c>null</c> when the function itself is the predicate.</param>
/// <param name="value">The compared value when <paramref name="op" /> is present.</param>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class FunctionFilter(string function, string member, IReadOnlyList<object?> arguments,
    ComparisonOperator? op, object? value) : QueryFilter
{
    /// <summary>The function name.</summary>
    public string Function { get; } = function;

    /// <summary>The member path.</summary>
    public string Member { get; } = member;

    /// <summary>The constant arguments after the member, in order.</summary>
    public IReadOnlyList<object?> Arguments { get; } = arguments;

    /// <summary>The comparison operator, or <c>null</c> when the function itself is the predicate.</summary>
    public ComparisonOperator? Operator { get; } = op;

    /// <summary>The compared value when <see cref="Operator" /> is present.</summary>
    public object? Value { get; } = value;
}

/// <summary>A member constrained to a set of values: <c>member IN (values)</c>.</summary>
/// <param name="member">The member path.</param>
/// <param name="values">The candidate values.</param>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
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
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
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
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
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
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class LogicalFilter(LogicalOperator op, IReadOnlyList<QueryFilter> operands) : QueryFilter
{
    /// <summary>The logical operator.</summary>
    public LogicalOperator Operator { get; } = op;

    /// <summary>The operands.</summary>
    public IReadOnlyList<QueryFilter> Operands { get; } = operands;
}
