using System;
using System.Collections.Generic;
using System.Linq;

namespace eQuantic.Core.Data.Evolution;

/// <summary>
///     Compares the model as it is against the model as it was and reports what changed. It answers in data, not
///     in statements: a store renders the change, and a person reads it before it runs.
///     <para>
///         Three judgements are made here rather than left to the renderer, because getting them wrong costs
///         data. A member that appears with no declared value produces a change marked as needing one. A member
///         that appears while another disappears is a <b>rename</b> when the model says so and a drop-and-add
///         when it does not — flagged, because that pair is how data goes missing. And a change the store cannot
///         make is refused by name instead of generated.
///     </para>
/// </summary>
public static class ModelDiffer
{
    /// <summary>Compares two versions of a model.</summary>
    /// <param name="before">The model as it was — an empty snapshot the first time.</param>
    /// <param name="after">The model as it is.</param>
    /// <exception cref="InvalidOperationException">The snapshots belong to different stores.</exception>
    public static ModelDifference Compare(ModelSnapshot before, ModelSnapshot after)
    {
        if (before.Entities.Count > 0 && !string.Equals(before.Provider, after.Provider, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The recorded model belongs to '{before.Provider}' and the current one to '{after.Provider}'. " +
                "A model is compared against its own store's history; pointing an application at a different " +
                "store starts a new one.");
        }

        var changes = new List<ModelChange>();
        var refusals = new List<ModelRefusal>();

        foreach (var entity in after.Entities)
        {
            var previous = before.For(entity.EntityType);
            if (previous is null)
            {
                changes.Add(new ModelChange(ModelChangeKind.AddCollection, entity.EntityType) { To = entity.Collection });
                continue;
            }

            CompareEntity(previous, entity, after.Provider, changes, refusals);
        }

        foreach (var gone in before.Entities.Where(entity => after.For(entity.EntityType) is null))
        {
            changes.Add(new ModelChange(ModelChangeKind.DropCollection, gone.EntityType) { From = gone.Collection });
        }

        return new ModelDifference(after.Provider, changes, refusals);
    }

    private static void CompareEntity(EntitySnapshot before, EntitySnapshot after, string provider,
        List<ModelChange> changes, List<ModelRefusal> refusals)
    {
        if (!string.Equals(before.Collection, after.Collection, StringComparison.Ordinal))
        {
            changes.Add(new ModelChange(ModelChangeKind.RenameCollection, after.EntityType)
            {
                From = before.Collection,
                To = after.Collection,
            });
        }

        RefuseImpossible(before, after, provider, refusals);

        // Members present in both are matched by their CLR name; the stored name may still have moved.
        var carried = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in after.Fields)
        {
            if (before.Field(field.Member) is { } was)
            {
                carried.Add(was.Member);
                CompareField(after.EntityType, was, field, changes);
                continue;
            }

            // A member the previous version did not have: either the model says which one it used to be…
            var origin = before.Fields.FirstOrDefault(candidate =>
                after.Field(candidate.Member) is null &&
                field.PreviousNames.Contains(candidate.Name, StringComparer.Ordinal));

            if (origin is not null)
            {
                carried.Add(origin.Member);
                changes.Add(new ModelChange(ModelChangeKind.RenameField, after.EntityType)
                {
                    Member = field.Member,
                    From = origin.Name,
                    To = field.Name,
                });
                continue;
            }

            // …or it is simply new.
            changes.Add(new ModelChange(ModelChangeKind.AddField, after.EntityType)
            {
                Member = field.Member,
                To = field.Name,
                DefaultLiteral = field.DefaultLiteral,
                // A nullable member is allowed to be absent — null is a real answer for it. Everything else
                // would land on default(T) in every existing record, which is a value nobody chose.
                NeedsValue = field.DefaultLiteral is null && !field.Nullable,
            });
        }

        var dropped = before.Fields.Where(field => !carried.Contains(field.Member)).ToList();
        var added = changes
            .Where(change => change.Kind == ModelChangeKind.AddField && change.EntityType == after.EntityType)
            .ToList();

        foreach (var field in dropped)
        {
            // A drop opposite an add is how a rename looks when nobody declared it — say so, because generating
            // the pair is what loses the values.
            var hint = added.Count > 0
                ? $"'{field.Name}' disappears while {string.Join(", ", added.Select(change => $"'{change.To}'"))} " +
                  "appears. If one became the other, declare [PreviousName] on the new member so the change keeps " +
                  "the data instead of dropping and re-adding it."
                : null;

            changes.Add(new ModelChange(ModelChangeKind.DropField, after.EntityType)
            {
                Member = field.Member,
                From = field.Name,
                AmbiguousRenameHint = hint,
            });
        }
    }

    private static void CompareField(string entityType, FieldSnapshot before, FieldSnapshot after, List<ModelChange> changes)
    {
        if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal))
        {
            // Same member, different stored name: a rename with nothing ambiguous about it.
            changes.Add(new ModelChange(ModelChangeKind.RenameField, entityType)
            {
                Member = after.Member,
                From = before.Name,
                To = after.Name,
            });
        }

        if (!string.Equals(before.StoredType, after.StoredType, StringComparison.Ordinal))
        {
            changes.Add(new ModelChange(ModelChangeKind.ConvertField, entityType)
            {
                Member = after.Member,
                From = before.StoredType,
                To = after.StoredType,
            });
        }

        if (before.Length != after.Length || before.Precision != after.Precision || before.Scale != after.Scale)
        {
            changes.Add(new ModelChange(ModelChangeKind.ChangeFacets, entityType)
            {
                Member = after.Member,
                From = Facets(before),
                To = Facets(after),
            });
        }
    }

    private static void RefuseImpossible(EntitySnapshot before, EntitySnapshot after, string provider,
        List<ModelRefusal> refusals)
    {
        var partitionMoved = !before.PartitionKeys.SequenceEqual(after.PartitionKeys, StringComparer.Ordinal);
        var clusteringMoved = !before.Clustering.SequenceEqual(after.Clustering);
        var keyMoved = !before.Keys.SequenceEqual(after.Keys, StringComparer.Ordinal);

        if (string.Equals(provider, "cassandra", StringComparison.OrdinalIgnoreCase) &&
            (partitionMoved || clusteringMoved))
        {
            refusals.Add(new ModelRefusal(after.EntityType,
                "Cassandra stores rows by their partition and clustering keys, so changing either would move every " +
                "row that already exists; there is no ALTER that does it.",
                "Map a second entity with the new key, copy the data across, and retire the old table once the " +
                "readers have moved."));
        }

        if (keyMoved && !partitionMoved)
        {
            refusals.Add(new ModelRefusal(after.EntityType,
                $"The key changed from [{string.Join(", ", before.Keys)}] to [{string.Join(", ", after.Keys)}], and " +
                "an existing table's identity cannot be redefined without deciding what happens to the rows keyed " +
                "the old way.",
                "Write the change by hand: create the new shape, move the rows with the mapping only you know, and " +
                "drop the old one."));
        }
    }

    private static string Facets(FieldSnapshot field) =>
        field.Precision != 0 ? $"({field.Precision},{field.Scale})" : $"({field.Length})";
}
