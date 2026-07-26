using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using eQuantic.Core.Data.Modeling;

namespace eQuantic.Core.Data.Evolution;

/// <summary>
///     Reads the evolution vocabulary a member declares about itself: where it used to be stored, and what
///     records written before it existed should hold.
///     <para>
///         It lives on the member rather than in a store's model on purpose. A document store has no column to
///         hang a default on — for MongoDB and Cosmos DB the class <em>is</em> the schema — so the attribute is the
///         only place the answer can live for all six stores. Models that can also express it fluently take
///         precedence; this is the fallback that always works.
///     </para>
/// </summary>
public static class MemberVocabulary
{
    /// <summary>The stored names a member has previously had, from <see cref="PreviousNameAttribute" />.</summary>
    /// <param name="member">The member, or <c>null</c> when the store could not resolve one.</param>
    public static IReadOnlyList<string> PreviousNames(MemberInfo? member) =>
        member is null
            ? []
            : member.GetCustomAttributes<PreviousNameAttribute>(inherit: true)
                .Select(attribute => attribute.Name)
                .ToList();

    /// <summary>
    ///     What records written before the member existed hold, from <see cref="DefaultValueAttribute" />, as a C#
    ///     literal. <c>null</c> when the member declares nothing — which is what makes a generated change stop and
    ///     ask instead of quietly settling for <c>default(T)</c>.
    /// </summary>
    /// <param name="member">The member, or <c>null</c> when the store could not resolve one.</param>
    public static string? DefaultLiteral(MemberInfo? member) =>
        member?.GetCustomAttribute<DefaultValueAttribute>(inherit: true) is { } attribute
            ? CSharpLiteral.Render(attribute.Value)
            : null;

    /// <summary>The value itself, for a store that has to write it rather than render it.</summary>
    /// <param name="member">The member, or <c>null</c>.</param>
    /// <param name="value">The declared value.</param>
    /// <returns>Whether the member declared one.</returns>
    public static bool TryDefaultValue(MemberInfo? member, out object? value)
    {
        var attribute = member?.GetCustomAttribute<DefaultValueAttribute>(inherit: true);
        value = attribute?.Value;
        return attribute is not null;
    }

    /// <summary>The property a stored member name refers to, or <c>null</c> when the type has no such property.</summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="memberName">The CLR member name.</param>
    public static MemberInfo? Find(Type entityType, string memberName) =>
        entityType.GetProperty(memberName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

    /// <summary>Whether a stored value may be absent: a nullable value type, or any reference.</summary>
    /// <param name="storedType">The CLR type as stored.</param>
    public static bool IsOptional(Type storedType) =>
        Nullable.GetUnderlyingType(storedType) is not null || !storedType.IsValueType;
}
