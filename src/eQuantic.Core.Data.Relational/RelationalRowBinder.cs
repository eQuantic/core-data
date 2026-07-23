using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;

namespace eQuantic.Core.Data.Relational;

/// <summary>
///     Binds an arbitrary result set into <typeparamref name="TResult" /> by column name — the materializer
///     behind the raw-SQL escape hatch, where the shape is whatever the query selected rather than a mapped
///     entity. Matching is case-insensitive and snake_case-tolerant (<c>created_at</c> binds
///     <c>CreatedAt</c>), values convert through the same coercions entity materialization uses, unmatched
///     columns are ignored and unmatched members keep their default. The ordinal-to-member plan is computed
///     once per result shape and cached, so a row costs a loop over the columns.
/// </summary>
/// <typeparam name="TResult">The result type.</typeparam>
internal sealed class RelationalRowBinder<TResult> where TResult : new()
{
    private static readonly ConcurrentDictionary<string, RelationalRowBinder<TResult>> Cache = new();

    private readonly (int Ordinal, PropertyInfo Property)[] _bindings;
    private readonly Func<object>? _accessorCreate;
    private readonly Data.Repository.EntityAccessor? _accessor;

    private RelationalRowBinder((int Ordinal, PropertyInfo Property)[] bindings)
    {
        _bindings = bindings;
        _accessor = Data.Repository.EntityAccessors.For(typeof(TResult));
        _accessorCreate = _accessor is null ? null : _accessor.Create;
    }

    /// <summary>The binder for this reader's column shape (cached by that shape).</summary>
    /// <param name="reader">The reader, before the first read.</param>
    public static RelationalRowBinder<TResult> For(DbDataReader reader)
    {
        var names = new string[reader.FieldCount];
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
        {
            names[ordinal] = reader.GetName(ordinal);
        }

        // The separator is escaped rather than literal so it survives editors: joined bare, ("ab", "c") and
        // ("a", "bc") would share a key and the second shape would be bound with the first one's ordinals.
        return Cache.GetOrAdd(string.Join("\u001f", names), _ => Build(names));
    }

    private static RelationalRowBinder<TResult> Build(string[] names)
    {
        var properties = typeof(TResult)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property is { CanWrite: true } && property.GetIndexParameters().Length == 0)
            .ToList();

        var bindings = new List<(int, PropertyInfo)>(names.Length);
        for (var ordinal = 0; ordinal < names.Length; ordinal++)
        {
            var match = properties.FirstOrDefault(property => Matches(property.Name, names[ordinal]));
            if (match is not null)
            {
                bindings.Add((ordinal, match));
            }
        }

        return new RelationalRowBinder<TResult>(bindings.ToArray());
    }

    /// <summary>Member and column match when their names agree ignoring case and underscores.</summary>
    private static bool Matches(string member, string column) =>
        string.Equals(member, column, StringComparison.OrdinalIgnoreCase)
        || string.Equals(member, column.Replace("_", string.Empty), StringComparison.OrdinalIgnoreCase);

    /// <summary>Materializes the reader's current row.</summary>
    /// <param name="reader">The reader, positioned on a row.</param>
    public TResult Bind(DbDataReader reader)
    {
        // A generated accessor (from the package's source generator) writes without reflection when the result
        // type happens to be a mapped entity; an arbitrary DTO falls back to the property setters.
        var result = _accessorCreate is not null ? (TResult)_accessorCreate() : new TResult();

        foreach (var (ordinal, property) in _bindings)
        {
            if (reader.IsDBNull(ordinal))
            {
                continue;
            }

            var value = RelationalMaterializer.ChangeValue(reader.GetValue(ordinal), property.PropertyType);
            if (_accessor is not null)
            {
                _accessor.Set(result!, property.Name, value);
            }
            else
            {
                property.SetValue(result, value);
            }
        }

        return result;
    }
}
