using System.Collections;
using System.Data.Common;
using System.Reflection;

namespace eQuantic.Core.Data.Relational;

/// <summary>
///     Materializes <see cref="DbDataReader" /> rows into entities by ordinal, against the column list the
///     SELECT was rendered from — no name lookups per row. Provider CLR values assign directly; the two common
///     impedance gaps are bridged (driver arrays into <c>List&lt;T&gt;</c> members, numeric/enum coercions).
/// </summary>
internal static class RelationalMaterializer
{
    /// <summary>Materializes the current row.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="reader">The reader, positioned on a row.</param>
    /// <param name="selected">The columns the SELECT listed, in ordinal order.</param>
    public static TEntity Materialize<TEntity>(DbDataReader reader, IReadOnlyList<RelationalColumn> selected) =>
        (TEntity)Materialize(typeof(TEntity), reader, selected);

    /// <summary>Materializes the current row (non-generic — the include loader works over runtime types).</summary>
    public static object Materialize(Type entityType, DbDataReader reader, IReadOnlyList<RelationalColumn> selected)
    {
        // Generated accessors (shipped with the package's source generator) replace the reflection calls —
        // construction and member writes become direct code; reflection remains the fallback contract.
        var accessor = eQuantic.Core.Data.Repository.EntityAccessors.For(entityType);
        var entity = accessor?.Create() ?? Activator.CreateInstance(entityType)!;
        for (var ordinal = 0; ordinal < selected.Count; ordinal++)
        {
            if (reader.IsDBNull(ordinal))
            {
                continue;
            }

            var column = selected[ordinal];
            var value = Cell(column, reader.GetValue(ordinal), column.Property.PropertyType);
            if (accessor is not null)
            {
                accessor.Set(entity, column.Property.Name, value);
            }
            else
            {
                column.Property.SetValue(entity, value);
            }
        }

        return entity;
    }

    /// <summary>
    ///     Materializes the current row of an <b>arbitrary</b> result set (a <c>FromSql</c> escape hatch),
    ///     matching the reader's column names to the mapped columns; unmatched result columns are ignored.
    /// </summary>
    public static TEntity MaterializeByName<TEntity>(DbDataReader reader, RelationalEntityConfiguration configuration)
    {
        var entity = Activator.CreateInstance<TEntity>()!;
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
        {
            if (reader.IsDBNull(ordinal))
            {
                continue;
            }

            var name = reader.GetName(ordinal);
            var column = configuration.Columns.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (column is not null)
            {
                Assign(entity, column, reader.GetValue(ordinal));
            }
        }

        return entity;
    }

    /// <summary>Converts a reader value into a target CLR type (nullable unwrap, enum and numeric coercions).</summary>
    internal static object? ChangeValue(object? value, Type target)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        var type = Nullable.GetUnderlyingType(target) ?? target;
        if (type.IsInstanceOfType(value))
        {
            return value;
        }

        return type.IsEnum ? Enum.ToObject(type, value) : Convert.ChangeType(value, type);
    }

    /// <summary>
    ///     Materializes one cell into the target CLR type, through the column's converter and the same
    ///     impedance bridges the entity path applies — the shared per-cell pipeline behind entity assignment
    ///     and reader-direct projections.
    /// </summary>
    internal static object? Cell(RelationalColumn column, object value, Type memberType)
    {
        // A converted column materializes through its converter — the stored scalar becomes the domain value.
        if (column.Converter is { } converter)
        {
            return converter.FromStored(ChangeValue(value, converter.StoredType));
        }

        return Coerce(value, memberType);
    }

    private static void Assign(object entity, RelationalColumn column, object value) =>
        column.Property.SetValue(entity, Cell(column, value, column.Property.PropertyType));

    /// <summary>Coerces a provider value into a member type (nullable unwrap, jsonb, arrays, enum/numeric).</summary>
    private static object? Coerce(object value, Type memberType)
    {
        var target = Nullable.GetUnderlyingType(memberType) ?? memberType;

        if (target.IsInstanceOfType(value))
        {
            return value;
        }

        // Document columns (jsonb) come back as JSON text; shape them into the member's dictionary.
        if (value is string json && typeof(System.Collections.IDictionary).IsAssignableFrom(target))
        {
            return System.Text.Json.JsonSerializer.Deserialize(json, target);
        }

        // A driver hands collections back as arrays; shape them into the member's List<T>.
        if (value is Array array && !target.IsArray && typeof(IEnumerable).IsAssignableFrom(target) && target.IsGenericType)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(target.GetGenericArguments()[0]))!;
            foreach (var item in array)
            {
                list.Add(item);
            }

            return list;
        }

        return target.IsEnum ? Enum.ToObject(target, value) : Convert.ChangeType(value, target);
    }
}
