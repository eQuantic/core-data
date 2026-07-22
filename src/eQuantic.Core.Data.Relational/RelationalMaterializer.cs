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
    public static TEntity Materialize<TEntity>(DbDataReader reader, IReadOnlyList<RelationalColumn> selected)
    {
        var entity = Activator.CreateInstance<TEntity>()!;
        for (var ordinal = 0; ordinal < selected.Count; ordinal++)
        {
            if (reader.IsDBNull(ordinal))
            {
                continue;
            }

            Assign(entity, selected[ordinal].Property, reader.GetValue(ordinal));
        }

        return entity;
    }

    private static void Assign(object entity, PropertyInfo property, object value)
    {
        var target = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (target.IsInstanceOfType(value))
        {
            property.SetValue(entity, value);
            return;
        }

        // A driver hands collections back as arrays; shape them into the member's List<T>.
        if (value is Array array && !target.IsArray && typeof(IEnumerable).IsAssignableFrom(target) && target.IsGenericType)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(target.GetGenericArguments()[0]))!;
            foreach (var item in array)
            {
                list.Add(item);
            }

            property.SetValue(entity, list);
            return;
        }

        property.SetValue(entity, target.IsEnum ? Enum.ToObject(target, value) : Convert.ChangeType(value, target));
    }
}
