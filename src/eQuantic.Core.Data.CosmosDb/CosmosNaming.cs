using System.Reflection;
using eQuantic.Core.Data.Modeling;

namespace eQuantic.Core.Data.CosmosDb;

/// <summary>
///     Naming helpers shared across the provider. The stored element name of a member is resolved the same way
///     everywhere — the serializer, LINQ member translation, partition key paths, patch paths and migration
///     paths — so a rename can never desynchronize what writes from what queries:
///     <c>[StoredAs]</c> &gt; <c>[JsonPropertyName]</c> &gt; camelCase (the System.Text.Json web default).
/// </summary>
internal static class CosmosNaming
{
    /// <summary>Lower-cases the first character (e.g. <c>CountryCode</c> → <c>countryCode</c>).</summary>
    public static string CamelCase(string name) =>
        string.IsNullOrEmpty(name) || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>The stored element name for a member.</summary>
    public static string StoredName(MemberInfo member)
    {
        if (member.GetCustomAttributes(typeof(StoredAsAttribute), inherit: true) is [StoredAsAttribute stored, ..])
        {
            return stored.Name;
        }

        return member.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>() is { } json
            ? json.Name
            : CamelCase(member.Name);
    }

    /// <summary>The stored segments for a dotted CLR member path rooted at <paramref name="root" />.</summary>
    public static IEnumerable<string> StoredPath(Type root, string dottedPath)
    {
        var currentType = root;
        foreach (var segment in dottedPath.Split('.'))
        {
            var member = currentType?.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
            if (member is null)
            {
                yield return CamelCase(segment);
                continue;
            }

            yield return StoredName(member);
            currentType = member.PropertyType;
        }
    }

    /// <summary>The stored segments for a member-path selector (rooted at the lambda's parameter type).</summary>
    public static IEnumerable<string> StoredPath(System.Linq.Expressions.LambdaExpression selector) =>
        StoredPath(selector.Parameters[0].Type, eQuantic.Linq.Expressions.MemberPathExtensions.GetMemberPath(selector));
}
