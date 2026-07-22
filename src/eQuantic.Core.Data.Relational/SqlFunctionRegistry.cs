namespace eQuantic.Core.Data.Relational;

/// <summary>
///     The dialect's function translations, keyed by the marker method's <b>name</b>. The engine ships the
///     standard set (<c>ToLower</c>/<c>ToUpper</c>/<c>Trim</c>, <c>Db.Like</c>, <c>IsNullOrEmpty</c>,
///     <c>Db.Year</c>/<c>Month</c>/<c>Day</c>); map your own with <see cref="Map" /> — write a static marker
///     method with a real C# body (so an unmapped dialect can still run it client-side), call it in your lambdas,
///     and register how this dialect renders it. The renderer hands you the quoted column and the bound
///     parameter markers; you return the SQL fragment.
/// </summary>
public sealed class SqlFunctionRegistry
{
    private readonly Dictionary<string, Func<string, IReadOnlyList<string>, string>> _renderers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers (or replaces) a function translation.</summary>
    /// <param name="function">The marker method's name.</param>
    /// <param name="render">Renders the fragment from the quoted column and the bound argument markers.</param>
    /// <returns>The same registry for chaining.</returns>
    public SqlFunctionRegistry Map(string function, Func<string, IReadOnlyList<string>, string> render)
    {
        _renderers[function] = render;
        return this;
    }

    /// <summary>Attempts to render a function; false sends the clause to the gated client-side residual.</summary>
    internal bool TryRender(string function, string column, IReadOnlyList<string> arguments, out string fragment)
    {
        if (_renderers.TryGetValue(function, out var render))
        {
            fragment = render(column, arguments);
            return true;
        }

        fragment = string.Empty;
        return false;
    }
}
