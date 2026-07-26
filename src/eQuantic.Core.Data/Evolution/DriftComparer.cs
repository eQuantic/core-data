using System;
using System.Collections.Generic;
using System.Linq;

namespace eQuantic.Core.Data.Evolution;

/// <summary>
///     Compares what the model says a store should hold against what it does hold.
///     <para>
///         This is the check nothing else performs. A migration history says which changes <em>ran</em>; it cannot
///         say whether someone altered a column afterwards, whether a change was applied by hand on one
///         environment and not another, or whether a half-finished migration left the store between two states.
///         Only looking answers that.
///     </para>
///     <para>
///         The comparison itself is deliberately dull, because both sides arrive in the same vocabulary — the
///         provider that reads the database also renders the model through the same dialect. Everything difficult
///         about spelling a type happens before this point.
///     </para>
/// </summary>
public static class DriftComparer
{
    /// <summary>Compares the two descriptions.</summary>
    /// <param name="expected">What the model says.</param>
    /// <param name="observed">What the store holds.</param>
    /// <exception cref="InvalidOperationException">The descriptions belong to different stores.</exception>
    public static DriftReport Compare(DatabaseSnapshot expected, DatabaseSnapshot observed)
    {
        if (!string.Equals(expected.Provider, observed.Provider, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The model describes '{expected.Provider}' and the database answered as '{observed.Provider}'.");
        }

        var findings = new List<DriftFinding>();

        foreach (var collection in expected.Collections)
        {
            var actual = observed.For(collection.Name);
            if (actual is null)
            {
                findings.Add(new DriftFinding(DriftKind.MissingCollection, collection.EntityType, collection.Name));
                continue;
            }

            CompareFields(collection, actual, findings);
        }

        return new DriftReport(expected.Provider, findings);
    }

    private static void CompareFields(DatabaseCollection expected, DatabaseCollection observed,
        List<DriftFinding> findings)
    {
        foreach (var field in expected.Fields)
        {
            var actual = observed.Field(field.Name);
            if (actual is null)
            {
                findings.Add(new DriftFinding(DriftKind.MissingField, expected.EntityType, expected.Name)
                {
                    Field = field.Name,
                    Expected = field.StoredType,
                });
                continue;
            }

            if (!string.Equals(field.StoredType, actual.StoredType, StringComparison.Ordinal))
            {
                findings.Add(new DriftFinding(DriftKind.TypeDiffers, expected.EntityType, expected.Name)
                {
                    Field = field.Name,
                    Expected = field.StoredType,
                    Found = actual.StoredType,
                });
            }

            if (field.Nullable != actual.Nullable)
            {
                findings.Add(new DriftFinding(DriftKind.NullabilityDiffers, expected.EntityType, expected.Name)
                {
                    Field = field.Name,
                    Expected = field.Nullable ? "null allowed" : "not null",
                    Found = actual.Nullable ? "null allowed" : "not null",
                });
            }
        }

        var mapped = expected.Fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var field in observed.Fields.Where(field => !mapped.Contains(field.Name)))
        {
            findings.Add(new DriftFinding(DriftKind.UnexpectedField, expected.EntityType, expected.Name)
            {
                Field = field.Name,
                Found = field.StoredType,
            });
        }
    }
}
