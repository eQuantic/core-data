using System;
using System.Collections.Generic;

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
        var counter = engine switch
        {
            Engine.MongoDb =>
                "the document in the \"_sequences\" collection whose _id is the collection and element names joined " +
                "by a dot",
            Engine.CosmosDb =>
                "the item in the \"_sequences\" container whose id is the container and property names joined by a dot",
            _ => "the row of the \"equantic_sequences\" table named by the table and column joined by a dot",
        };

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
