using System;
using System.Collections.Generic;
using System.Linq;

namespace eQuantic.Core.Data.Evolution;

/// <summary>
///     The decisions a generated migration hands back for the two changes no store operation performs faithfully:
///     an enum switched between its numbers and its labels, and a member whose generation by the store changed.
///     <para>
///         Each says what the hand-written step has to do <b>on the store the snapshot belongs to</b>, because the
///         answer differs by engine: a PostgreSQL column moves to an enum type through a <c>USING</c> clause, a MySQL
///         <c>ENUM</c> reads a number as a label's position, a document store rewrites every document, and Cassandra
///         cannot change a column's type at all. Generating a conversion instead would write the number 1 as the text
///         <c>"1"</c>, never as its label, and a generation change left unsaid leaves the store not assigning what the
///         model no longer writes.
///     </para>
/// </summary>
internal static class MigrationDecisions
{
    private static readonly HashSet<string> IntegralTypes = new(StringComparer.Ordinal)
    {
        // CLR, as the relational and document stores record a member's type
        "System.Byte", "System.SByte", "System.Int16", "System.UInt16", "System.Int32", "System.UInt32",
        "System.Int64", "System.UInt64",
        // CQL, as Cassandra records it (an enum stored as its number is its underlying type's)
        "tinyint", "smallint", "int", "bigint", "varint",
    };

    private enum Engine
    {
        PostgreSql,
        SqlServer,
        MySql,
        MariaDb,
        MongoDb,
        CosmosDb,
        Cassandra,
        Unknown,
    }

    /// <summary>Whether a conversion moves a member to or from an enum's labels.</summary>
    /// <param name="change">A conversion.</param>
    public static bool IsLabelSwitch(ModelChange change) =>
        change.Kind == ModelChangeKind.ConvertField &&
        (StoredForm.LabelsOf(change.From) is not null || StoredForm.LabelsOf(change.To) is not null);

    /// <summary>What the step that moves a member to or from an enum's labels must do on this store.</summary>
    /// <param name="provider">The store the difference belongs to.</param>
    /// <param name="entityType">The entity's CLR type name.</param>
    /// <param name="change">The conversion.</param>
    public static string Labels(string provider, string entityType, ModelChange change)
    {
        var member = $"'{entityType}.{change.Member}'";
        var engine = EngineOf(provider);
        var from = StoredForm.LabelsOf(change.From);
        var to = StoredForm.LabelsOf(change.To);
        var collection = StoredForm.IsLabelCollection(change.From) || StoredForm.IsLabelCollection(change.To);

        if (from is not null && to is not null)
        {
            return string.Equals(from, to, StringComparison.Ordinal)
                ? $"{member} changes from {change.From} to {change.To}, the same enum's labels in another shape. Write " +
                  "the step that brings what is stored to the new shape, then delete this line."
                : $"{member} is now stored as the labels of {to} instead of those of {from}. Write a Run step that " +
                  $"rewrites each stored label of {from} as the label of {to} that stands for the same value " +
                  $"(EnumLabels.Of(typeof({from})) and EnumLabels.Of(typeof({to})) pair each member's name with its " +
                  $"label){RetypeForOtherLabels(engine)}, then delete this line.";
        }

        if (to is not null)
        {
            var numbers = IsNumbersOf(change.From!, to);
            var subject = (numbers, collection) switch
            {
                (true, true) => "each number in the stored arrays",
                (true, false) => "each stored number",
                (false, true) => "each value in the stored arrays",
                _ => "each stored value",
            };
            var pairs = $"EnumLabels.Of(typeof({to})) pairs each member's name with its label";

            return $"{member} is now stored as the labels of {to}, and what is already stored holds " +
                   $"{(numbers ? "its numbers" : $"values of {change.From}")}, which the model can no longer read. No " +
                   "conversion is generated: a type change would write the number 1 as the text '1', never as its " +
                   $"label. {ToLabels(engine, subject, pairs)} Then delete this line.";
        }

        if (!IsNumbersOf(change.To!, from!))
        {
            return $"{member} was stored as the labels of {from} and is now stored as {change.To}. No conversion is " +
                   $"generated between an enum's labels and another type: write the step that brings the stored labels " +
                   $"(and, on a relational store, the column) to what {change.To} reads " +
                   $"(EnumLabels.Of(typeof({from})) pairs each member's name with its label), then delete this line.";
        }

        return $"{member} is now stored as the numbers of {from}, and what is already stored holds its labels, which " +
               "the model can no longer read. No conversion is generated: a type change cannot know which number a " +
               $"label stands for. {ToNumbers(engine, from!, change.To!, collection)} Then delete this line.";
    }

    /// <summary>What the step that changes how the store assigns a member must do on this store.</summary>
    /// <param name="provider">The store the difference belongs to.</param>
    /// <param name="entityType">The entity's CLR type name.</param>
    /// <param name="change">The generation change.</param>
    public static string Generation(string provider, string entityType, ModelChange change)
    {
        var engine = EngineOf(provider);
        var opening = $"'{entityType}.{change.Member}' is now {Describe(change.To)} (it was {Describe(change.From)}).";

        var step = engine switch
        {
            Engine.PostgreSql or Engine.SqlServer or Engine.MySql or Engine.MariaDb =>
                RelationalGeneration(engine, change.From, change.To),
            Engine.MongoDb or Engine.CosmosDb or Engine.Cassandra =>
                StoreAssignedGeneration(engine, change.From, change.To),
            _ => null,
        };

        return opening + " " + (step ??
            "Write the step that makes the store assign it so, a sequence starting above the largest value already " +
            "stored, or a default the store gives every insert.") + " Then delete this line.";
    }

    /// <summary>
    ///     What the step that brings the store to an enum's new labels must do on this store, or <c>null</c> when the
    ///     store holds nothing that has to change (a label added or moved on a store with no enum type).
    /// </summary>
    /// <param name="provider">The store the difference belongs to.</param>
    /// <param name="entityType">The entity's CLR type name.</param>
    /// <param name="change">The change of labels.</param>
    public static string? LabelSet(string provider, string entityType, ModelChange change)
    {
        var engine = EngineOf(provider);
        var diff = LabelDiff.Between(change.LabelsBefore, change.LabelsAfter);
        var enumName = StoredForm.LabelsOf(change.StoredType) ?? change.StoredType;
        var member = $"'{entityType}.{change.Member}'";
        var opening = $"The labels of {enumName}, which {member} is stored as, changed from ({Quoted(change.LabelsBefore)}) " +
                      $"to ({Quoted(change.LabelsAfter)}){diff.Describe()}.";
        var rewrites = diff.Rewrites(engine == Engine.Cassandra ? "row" : engine is Engine.MongoDb or Engine.CosmosDb ? "document" : "row");

        string? step = engine switch
        {
            Engine.PostgreSql when diff.Removed.Count == 0 && !diff.Reordered =>
                "Write a Run step that runs " + string.Join(", then ", diff.PostgreSqlStatements()) + ": the rows already " +
                "stored keep their values" + (diff.Renamed.Count > 0 ? ", a renamed label renamed in them too" : string.Empty) +
                "." + (diff.Added.Count > 0
                    ? " A label added cannot be used in the transaction that adds it (and before PostgreSQL 12 cannot be " +
                      "added inside one)."
                    : string.Empty),
            // The old type has no room for a new label, so a renamed one is mapped on the way to the new type, and only
            // the rows holding a removed one are rewritten first (to a label the old type has and the new one keeps).
            Engine.PostgreSql =>
                "PostgreSQL cannot remove or reorder an enum type's labels: write a Run step that creates a new type with " +
                $"({Quoted(change.LabelsAfter)}), " +
                (diff.Removed.Count > 0 ? RemovedRewrite(diff, "rows") + ", " : string.Empty) +
                "moves every column of the old type to it with ALTER COLUMN … TYPE … USING its text" +
                $"{(diff.Renamed.Count > 0 ? " (mapping " + diff.RenamedPairs() + ")" : string.Empty)}, drops the old type " +
                "and renames the new one to its name.",
            Engine.MySql or Engine.MariaDb =>
                "Write a Run step that " +
                (diff.Renamed.Count > 0
                    ? $"first widens the column to an ENUM holding both the old and the new labels, rewrites {diff.RenamedPairs()} " +
                      "in the rows, then "
                    : string.Empty) +
                (diff.Removed.Count > 0 ? $"{RemovedRewrite(diff, "rows")}, then " : string.Empty) +
                $"runs ALTER TABLE … MODIFY COLUMN … ENUM({Quoted(change.LabelsAfter)}), the full list, keeping the " +
                "column's NULL as it is: MySQL keeps every stored value whose label the list still has, and turns any " +
                "other into '' (an error in strict mode).",
            Engine.SqlServer when diff.Added.Count == 0 && diff.Removed.Count == 0 && diff.Renamed.Count == 0 => null,
            Engine.SqlServer =>
                "Write a Run step that drops the CHECK holding the column to the labels, " +
                (rewrites is null ? string.Empty : rewrites + ", ") +
                (diff.Longest(change.LabelsAfter) > diff.Longest(change.LabelsBefore)
                    ? $"widens the column to nvarchar({diff.Longest(change.LabelsAfter)}), "
                    : string.Empty) +
                $"and adds CHECK ([…] IN ({string.Join(", ", change.LabelsAfter.Select(label => "N'" + label.Label.Replace("'", "''") + "'"))})).",
            Engine.MongoDb or Engine.CosmosDb or Engine.Cassandra when rewrites is null => null,
            Engine.MongoDb or Engine.CosmosDb or Engine.Cassandra =>
                $"A label added needs nothing in the store, a renamed or removed one does: write a Run step that {rewrites}" +
                $"{(engine == Engine.CosmosDb ? ", one read and one replace per document" : string.Empty)}; the labels' " +
                "reader refuses a label the enum no longer has.",
            _ => $"Write the step that brings what is stored to the new labels{(rewrites is null ? string.Empty : ": it " + rewrites)}.",
        };

        return step is null ? null : $"{opening} {step} Then delete this line.";
    }

    /// <summary>What the step that makes the store number an entity's key, or stop numbering it, must do on this store.</summary>
    /// <param name="provider">The store the difference belongs to.</param>
    /// <param name="entityType">The entity's CLR type name.</param>
    /// <param name="change">The key generation change.</param>
    public static string KeyGeneration(string provider, string entityType, ModelChange change)
    {
        var engine = EngineOf(provider);
        var numbered = change.To is not null;
        var opening = numbered
            ? $"'{entityType}' now has its key ({change.Member}) numbered by the store (it was given by the application)."
            : $"'{entityType}' now has its key ({change.Member}) given by the application (it was numbered by the store).";

        var step = (engine, numbered) switch
        {
            (Engine.PostgreSql, true) =>
                "Write a Run step that makes the key an identity starting above the largest key already stored (ALTER " +
                "COLUMN … ADD GENERATED BY DEFAULT AS IDENTITY (START WITH the largest key + 1)), or the next insert " +
                "collides with a stored key.",
            (Engine.PostgreSql, false) =>
                "Write a Run step that drops the key's identity (ALTER COLUMN … DROP IDENTITY); inserts now carry the " +
                "application's key.",
            (Engine.SqlServer, true) =>
                "SQL Server cannot make an existing column an IDENTITY: write a Run step that creates the table anew with " +
                "the key as IDENTITY(1,1), copies the rows across under SET IDENTITY_INSERT … ON, reseeds it to the " +
                "largest key (DBCC CHECKIDENT … RESEED), and swaps the tables.",
            (Engine.SqlServer, false) =>
                "SQL Server cannot take IDENTITY off a column, and refuses every insert that carries a key until it is " +
                "gone: write a Run step that creates the table anew without it, copies the rows across under SET " +
                "IDENTITY_INSERT … ON, and swaps the tables.",
            (Engine.MySql or Engine.MariaDb, true) =>
                "Write a Run step that modifies the key column to AUTO_INCREMENT and sets the table's AUTO_INCREMENT to " +
                "the largest key already stored + 1, or the next insert collides with a stored key.",
            (Engine.MySql or Engine.MariaDb, false) =>
                "Write a Run step that modifies the key column without AUTO_INCREMENT; inserts now carry the " +
                "application's key.",
            (_, true) => "Write the step that makes the store number the key, starting above the largest key already stored.",
            _ => "Write the step that stops the store numbering the key, if it does.",
        };

        return $"{opening} {step} Then delete this line.";
    }

    /// <summary>
    ///     What a member added as store-generated needs on a store that does not fill the records already there, or
    ///     <c>null</c> where the store fills them itself (every relational engine: an identity, a sequence's default,
    ///     <c>AUTO_INCREMENT</c> and a clock or SQL default all reach the rows already in the table).
    /// </summary>
    /// <param name="provider">The store the difference belongs to.</param>
    /// <param name="entityType">The entity's CLR type name.</param>
    /// <param name="change">The added member.</param>
    public static string? AddedGenerated(string provider, string entityType, ModelChange change)
    {
        var engine = EngineOf(provider);
        if (engine is Engine.PostgreSql or Engine.SqlServer or Engine.MySql or Engine.MariaDb)
        {
            return null;
        }

        var documents = engine == Engine.Cassandra ? "rows" : "documents";
        var member = $"'{entityType}.{change.Member}'";
        if (change.Generated == StoredForm.Sequence)
        {
            var counter = engine is Engine.Unknown ? "its counter" : $"its counter, {Counter(engine)},";
            return $"{member} is added numbered by a sequence the store keeps, which numbers new inserts only: the " +
                   $"{documents} already stored {(engine == Engine.Cassandra ? "hold null for it" : "lack it")}. Write a " +
                   $"Run step that gives each of them a number of its own, then sets {counter} to the largest of them, " +
                   $"or new {documents} repeat numbers. Then delete this line.";
        }

        return $"{member} is added {Describe(change.Generated)}, which reaches new inserts only: the {documents} already " +
               $"stored {(engine == Engine.Cassandra ? "hold null for it" : "lack it")}. Give the ones that must have a value " +
               "one with an Update step. Then delete this line.";
    }

    private static string Counter(Engine engine) => engine switch
    {
        Engine.MongoDb =>
            "the document in the \"_sequences\" collection whose _id is the collection and element names joined by a dot",
        Engine.CosmosDb =>
            "the item in the \"_sequences\" container whose id is the container and property names joined by a dot",
        _ => "the row of the \"equantic_sequences\" table named by the table and column joined by a dot",
    };

    private static string Quoted(IReadOnlyList<LabelSnapshot> labels) =>
        string.Join(", ", labels.Select(label => "'" + label.Label.Replace("'", "''") + "'"));

    private static string RemovedRewrite(LabelDiff diff, string records) =>
        $"rewrites the {records} holding {string.Join(" or ", diff.Removed.Select(label => $"'{label.Label}'"))} to a label that remains";

    /// <summary>
    ///     How an enum's labels moved between two versions. Labels are matched by the member they stand for, then, among
    ///     those left over, by the member's number (a member renamed in code keeps its number): a matched label whose
    ///     text changed was renamed, an unmatched old one removed, an unmatched new one added.
    /// </summary>
    private sealed class LabelDiff
    {
        public List<LabelSnapshot> Added { get; } = [];
        public List<LabelSnapshot> Removed { get; } = [];
        public List<(LabelSnapshot From, LabelSnapshot To)> Renamed { get; } = [];
        public bool Reordered { get; private set; }
        private IReadOnlyList<LabelSnapshot> _after = [];
        private HashSet<LabelSnapshot> _addedSet = [];

        public static LabelDiff Between(IReadOnlyList<LabelSnapshot> before, IReadOnlyList<LabelSnapshot> after)
        {
            var diff = new LabelDiff { _after = after };
            var pairs = new List<(LabelSnapshot From, LabelSnapshot To)>();
            var unmatchedBefore = before.ToList();
            var unmatchedAfter = new List<LabelSnapshot>();

            foreach (var label in after)
            {
                var same = unmatchedBefore.FirstOrDefault(candidate => candidate.Member == label.Member);
                if (same is null)
                {
                    unmatchedAfter.Add(label);
                    continue;
                }

                unmatchedBefore.Remove(same);
                pairs.Add((same, label));
            }

            foreach (var label in unmatchedAfter.ToList())
            {
                var same = unmatchedBefore.FirstOrDefault(candidate => candidate.Value == label.Value);
                if (same is null)
                {
                    continue;
                }

                unmatchedBefore.Remove(same);
                unmatchedAfter.Remove(label);
                pairs.Add((same, label));
            }

            diff.Renamed.AddRange(pairs.Where(pair => pair.From.Label != pair.To.Label));
            diff.Removed.AddRange(unmatchedBefore);
            diff.Added.AddRange(unmatchedAfter);
            diff._addedSet = [.. unmatchedAfter];

            // Moved: the labels both versions have, read in the new order, are not in the old one.
            var positions = pairs.ToDictionary(pair => pair.To, pair => before.ToList().IndexOf(pair.From));
            var order = after.Where(positions.ContainsKey).Select(label => positions[label]).ToList();
            diff.Reordered = order.Zip(order.Skip(1)).Any(step => step.First > step.Second);
            return diff;
        }

        public string Describe()
        {
            var parts = new List<string>();
            if (Added.Count > 0)
            {
                parts.Add($"{Names(Added)} added");
            }

            if (Renamed.Count > 0)
            {
                parts.Add($"{RenamedPairs()} renamed");
            }

            if (Removed.Count > 0)
            {
                parts.Add($"{Names(Removed)} removed");
            }

            if (Reordered)
            {
                parts.Add("the order moved");
            }

            return parts.Count == 0 ? string.Empty : ": " + string.Join(", ", parts);
        }

        public string RenamedPairs() =>
            string.Join(", ", Renamed.Select(pair => $"'{pair.From.Label}' as '{pair.To.Label}'"));

        /// <summary>The rewrite renamed and removed labels need in what is stored, or <c>null</c> when none does.</summary>
        public string? Rewrites(string record)
        {
            var parts = new List<string>();
            if (Renamed.Count > 0)
            {
                parts.Add($"rewrites {RenamedPairs()} in every {record}");
            }

            if (Removed.Count > 0)
            {
                parts.Add($"rewrites every {record} holding {string.Join(" or ", Removed.Select(label => $"'{label.Label}'"))} " +
                          "to a label that remains");
            }

            return parts.Count == 0 ? null : string.Join(" and ", parts);
        }

        /// <summary>PostgreSQL's statements for renamed and added labels, renames first so every neighbour named exists.</summary>
        public IEnumerable<string> PostgreSqlStatements()
        {
            foreach (var (from, to) in Renamed)
            {
                yield return $"ALTER TYPE … RENAME VALUE '{from.Label}' TO '{to.Label}'";
            }

            for (var index = 0; index < _after.Count; index++)
            {
                if (!_addedSet.Contains(_after[index]))
                {
                    continue;
                }

                // After the label before it (already there, or added by the statement before this one); the first
                // label goes before the first one that was already there.
                yield return index > 0
                    ? $"ALTER TYPE … ADD VALUE '{_after[index].Label}' AFTER '{_after[index - 1].Label}'"
                    : $"ALTER TYPE … ADD VALUE '{_after[index].Label}' BEFORE " +
                      $"'{_after.FirstOrDefault(label => !_addedSet.Contains(label))?.Label}'";
            }
        }

        public int Longest(IReadOnlyList<LabelSnapshot> labels) => labels.Count == 0 ? 0 : labels.Max(label => label.Label.Length);

        private static string Names(IEnumerable<LabelSnapshot> labels) =>
            string.Join(", ", labels.Select(label => $"'{label.Label}'"));
    }

    /// <summary>
    ///     The decision for a search index whose declaration changed, or <c>null</c> where the store's index already serves
    ///     the new one (PostgreSQL's trigram index serves every mode) or the store builds none (SQL Server, MySQL, MariaDB,
    ///     the document stores, where the declaration names a pattern the provider matches without an index).
    /// </summary>
    public static string? Search(string provider, string entityType, ModelChange change)
    {
        var engine = EngineOf(provider);
        var member = $"'{entityType}.{change.Member}'";
        var from = change.From ?? "no search index";
        var to = change.To ?? "no search index";
        return engine switch
        {
            Engine.Cassandra when change.From is null =>
                $"{member} gains a search index ({to}). Add .EnsureCollection() to this migration: it creates the SASI index " +
                $"{CassandraSearchIndex(change)} with the declared options, and LIKE pushes down once it exists. Then delete this line.",
            Engine.Cassandra when change.To is null =>
                $"{member} no longer declares its search index ({from}). Write a Run step that drops it " +
                $"(DROP INDEX IF EXISTS {CassandraSearchIndex(change)}), or keep it if something else reads it. Then delete this line.",
            Engine.Cassandra =>
                $"The search index on {member} changes from {from} to {to}, and Cassandra cannot change a SASI index's options in " +
                $"place: the index already built keeps matching as it did, so it must be rebuilt. Write a Run step that drops it " +
                $"(DROP INDEX IF EXISTS {CassandraSearchIndex(change)}) and add .EnsureCollection() after it, which creates it again " +
                "with the new options. Until then the provider reads the cluster's schema and pushes a match that ignores case " +
                "down only on an index that ignores case; anywhere else it runs client-side, behind .AllowClientEvaluation(). " +
                "Then delete this line.",
            Engine.PostgreSql when change.From is null =>
                $"{member} gains a search index ({to}). Add .EnsureCollection() to this migration: it creates the trigram " +
                $"index ix_{change.Collection}_{change.Field}_search the declaration asks for. Then delete this line.",
            Engine.PostgreSql when change.To is null =>
                $"{member} no longer declares its search index ({from}). Write a Run step that drops it " +
                $"(DROP INDEX IF EXISTS \"ix_{change.Collection}_{change.Field}_search\"), or keep it if something else reads it. " +
                "Then delete this line.",
            _ => null,
        };
    }

    /// <summary>The name the Cassandra migration gives a search index (<c>{table}_{column}_search</c>).</summary>
    private static string CassandraSearchIndex(ModelChange change) => $"{change.Collection}_{change.Field}_search";

    private static Engine EngineOf(string provider) => provider.ToLowerInvariant() switch
    {
        "postgresql" => Engine.PostgreSql,
        "mssql" or "sqlserver" => Engine.SqlServer,
        "mysql" => Engine.MySql,
        "mariadb" => Engine.MariaDb,
        "mongodb" => Engine.MongoDb,
        "cosmosdb" => Engine.CosmosDb,
        "cassandra" => Engine.Cassandra,
        _ => Engine.Unknown,
    };

    /// <summary>
    ///     Whether a stored type holds the numbers of <paramref name="enumName" />: the enum itself (as the relational
    ///     and document stores record one stored as its number), a collection of it, or an integral type (CQL on
    ///     Cassandra, or a member that was an integer before it was the enum).
    /// </summary>
    private static bool IsNumbersOf(string storedType, string enumName) =>
        string.Equals(storedType, enumName, StringComparison.Ordinal) ||
        string.Equals(storedType, enumName + "[]", StringComparison.Ordinal) ||
        storedType.Contains("[[" + enumName + ",", StringComparison.Ordinal) ||
        IntegralTypes.Contains(storedType);

    private static string ToLabels(Engine engine, string subject, string pairs) => engine switch
    {
        Engine.PostgreSql =>
            "Write a Run step that creates the enum type with its labels (CREATE TYPE … AS ENUM), then moves the " +
            $"column to it with ALTER COLUMN … TYPE … USING an expression that rewrites {subject} as its label ({pairs}).",
        Engine.MySql or Engine.MariaDb =>
            $"Write a Run step that changes the column to text, rewrites {subject} as its label ({pairs}), and only " +
            "then changes it to its ENUM(…): a number put straight into an ENUM is read as the position of a label, " +
            "counted from 1, not as the member it stands for.",
        Engine.SqlServer =>
            $"Write a Run step that changes the column to nvarchar, rewrites {subject} as its label ({pairs}), and adds " +
            "the CHECK that holds it to the labels, as the engine creates such a column.",
        Engine.MongoDb =>
            $"Write a Run step that rewrites {subject} as its label in every document ({pairs}): the labels' " +
            "serializer refuses a number where a label belongs.",
        Engine.CosmosDb =>
            $"Write a Run step that rewrites {subject} as its label in every document ({pairs}), one read and one " +
            "replace per document, since Cosmos DB has no set-based update: the labels' converter refuses a number " +
            "where a label belongs.",
        Engine.Cassandra =>
            "Cassandra cannot change a column's type, nor re-add a dropped column with another: map the member to a " +
            $"new column (Column(...) or [StoredAs]), and write a Run step that adds it as text, rewrites {subject} " +
            $"as its label into it ({pairs}), and drops the old column.",
        _ => $"Write a Run step that rewrites {subject} as its label ({pairs}).",
    };

    private static string ToNumbers(Engine engine, string enumName, string storedType, bool collection)
    {
        var subject = collection ? "each label in the stored arrays" : "each stored label";
        var pairs = $"EnumLabels.Of(typeof({enumName})) pairs each member's name with its label";
        return engine switch
        {
            Engine.PostgreSql =>
                "Write a Run step that moves the column to the integral type the model declares with ALTER COLUMN … " +
                $"TYPE … USING an expression that rewrites {subject} as its number ({pairs}); the enum type can be " +
                "dropped once no column uses it.",
            Engine.MySql or Engine.MariaDb =>
                $"Write a Run step that changes the column to text, rewrites {subject} as its number ({pairs}), and " +
                "only then changes it to the integral type the model declares: an ENUM changed straight to a number " +
                "becomes the position of its label, counted from 1, not the member's value.",
            Engine.SqlServer =>
                $"Write a Run step that drops the CHECK holding the column to the labels, rewrites {subject} as its " +
                $"number ({pairs}), and changes the column to the integral type the model declares.",
            Engine.MongoDb =>
                $"Write a Run step that rewrites {subject} as its number in every document ({pairs}).",
            Engine.CosmosDb =>
                $"Write a Run step that rewrites {subject} as its number in every document ({pairs}), one read and " +
                "one replace per document, since Cosmos DB has no set-based update.",
            Engine.Cassandra =>
                "Cassandra cannot change a column's type, nor re-add a dropped column with another: map the member to " +
                $"a new column (Column(...) or [StoredAs]), and write a Run step that adds it as {storedType}, " +
                $"rewrites {subject} as its number into it ({pairs}), and drops the old column.",
            _ => $"Write a Run step that rewrites {subject} as its number ({pairs}).",
        };
    }

    private static string RetypeForOtherLabels(Engine engine) => engine switch
    {
        Engine.PostgreSql => ", and moves the column to the new enum type with ALTER COLUMN … TYPE … USING",
        Engine.MySql or Engine.MariaDb => ", and changes the column to the new ENUM(…) through text",
        Engine.SqlServer => ", and replaces the CHECK on the old labels with one on the new",
        _ => string.Empty,
    };

    private static string Describe(string? generated) => generated switch
    {
        null => "written by the application",
        StoredForm.Sequence => "numbered by a sequence the store keeps",
        StoredForm.Clock => "stamped with the store's clock",
        _ when StoredForm.DefaultOf(generated) is { } sql => $"filled by the store with the default {sql}",
        _ => $"assigned by the store as {generated}",
    };

    private static string? RelationalGeneration(Engine engine, string? from, string? to)
    {
        if (to is null)
        {
            var drop = (engine, from) switch
            {
                (Engine.PostgreSql, StoredForm.Sequence) => "drops the column's identity (ALTER COLUMN … DROP IDENTITY)",
                (Engine.PostgreSql, _) => "drops the column's default (ALTER COLUMN … DROP DEFAULT)",
                (Engine.SqlServer, StoredForm.Sequence) =>
                    "drops the column's DEFAULT constraint and the sequence it draws from (<table>_<column>_seq)",
                (Engine.SqlServer, _) => "drops the column's DEFAULT constraint",
                (Engine.MySql, StoredForm.Sequence) =>
                    "modifies the column without AUTO_INCREMENT and drops the UNIQUE index that came with it",
                (Engine.MariaDb, StoredForm.Sequence) =>
                    "drops the column's DEFAULT and the sequence it draws from (<table>_<column>_seq)",
                _ => "drops the column's DEFAULT",
            };
            return $"Write a Run step that {drop}, and makes the column nullable, as the engine creates a column the " +
                   "application writes; inserts now carry the application's value.";
        }

        // What the column held before has to go first: a column has one default, and an identity is not one.
        var before = (engine, from) switch
        {
            (_, null) => string.Empty,
            (Engine.PostgreSql, StoredForm.Sequence) => "drops the column's identity, then ",
            (Engine.MySql, StoredForm.Sequence) => "removes the column's AUTO_INCREMENT, then ",
            _ => "drops the column's current default, then ",
        };

        if (to == StoredForm.Sequence)
        {
            var sequence = engine switch
            {
                Engine.PostgreSql =>
                    "makes it an identity starting above the largest value already stored (ALTER COLUMN … SET NOT NULL, " +
                    "then ADD GENERATED BY DEFAULT AS IDENTITY (START WITH the largest value + 1))",
                Engine.SqlServer =>
                    "creates the sequence the engine names <table>_<column>_seq starting above the largest value already " +
                    "stored (CREATE SEQUENCE … START WITH the largest value + 1), and gives the column DEFAULT (NEXT " +
                    "VALUE FOR it) and NOT NULL",
                Engine.MariaDb =>
                    "creates the sequence the engine names <table>_<column>_seq starting above the largest value already " +
                    "stored (CREATE SEQUENCE … START WITH the largest value + 1), and gives the column DEFAULT NEXT VALUE " +
                    "FOR it and NOT NULL",
                _ =>
                    "makes the column NOT NULL AUTO_INCREMENT UNIQUE (MySQL allows one such column per table) and sets the " +
                    "table's AUTO_INCREMENT to the largest value already stored + 1",
            };
            return $"Nothing generated makes the store number it: write a Run step that {before}{sequence}, or the next " +
                   "insert repeats a number.";
        }

        var fill = to == StoredForm.Clock
            ? engine switch
            {
                Engine.PostgreSql => "DEFAULT now()",
                Engine.SqlServer => "DEFAULT SYSUTCDATETIME() (SYSDATETIMEOFFSET() for a DateTimeOffset)",
                _ => "DEFAULT (UTC_TIMESTAMP(6))",
            }
            : StoredForm.DefaultOf(to) is { } sql
                ? "DEFAULT " + sql
                : null;

        return fill is null
            ? null
            : $"Nothing generated gives the column its default: write a Run step that {before}gives every row " +
              $"holding NULL a value, then the column {fill} and NOT NULL, as the engine creates it.";
    }

    private static string? StoreAssignedGeneration(Engine engine, string? from, string? to)
    {
        var documents = engine == Engine.Cassandra ? "rows" : "documents";
        var counter = Counter(engine);

        if (to == StoredForm.Sequence)
        {
            return $"The provider numbers it from a counter, {counter} (Explain() prints it), which starts at zero while " +
                   $"the {documents} already stored hold numbers of their own. Write a Run step that sets that counter " +
                   $"to the largest value already stored before the first insert, or new {documents} repeat numbers.";
        }

        if (to == StoredForm.Clock)
        {
            var clock = engine switch
            {
                Engine.MongoDb => "the server's time, read once per commit",
                Engine.CosmosDb => "the server's time (GetCurrentDateTime()), read once per commit",
                _ => "toTimestamp(now()) in the INSERT",
            };
            return $"The provider stamps each insert with {clock}, so nothing in the store has to change, but the " +
                   $"{documents} already stored keep what they hold, or lack it: give the ones that must have a time " +
                   "a value with an Update step.";
        }

        if (to is null)
        {
            return from == StoredForm.Sequence
                ? $"Inserts now write the application's value, and nothing in the store has to change: its counter, " +
                  $"{counter}, can be deleted once nothing reads it."
                : "Inserts now write the application's value, and nothing in the store has to change.";
        }

        return null;
    }
}
