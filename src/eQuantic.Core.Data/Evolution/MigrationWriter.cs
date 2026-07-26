using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace eQuantic.Core.Data.Evolution;

/// <summary>
///     Writes a <see cref="ModelDifference" /> as the migration class that is committed alongside it.
///     <para>
///         What comes out is ordinary source: a file to read, edit, and keep. Generation is a starting point, not
///         an authority — the tooling knows what moved in the model, and only a person knows what the data means.
///     </para>
///     <para>
///         Where those two part ways, the file is written so that <b>the solution does not build</b>. A member
///         added without saying what existing records hold, and a change no store operation can express, both emit
///         a <c>#error</c> naming the decision. The alternative — a comment — is a change that runs, appears to
///         succeed, and quietly leaves the data wrong.
///     </para>
/// </summary>
public static class MigrationWriter
{
    private static readonly Dictionary<string, string> FieldTypes = new(StringComparer.Ordinal)
    {
        ["System.String"] = "String",
        ["System.Boolean"] = "Boolean",
        ["System.Int32"] = "Int32",
        ["System.Int64"] = "Int64",
        ["System.Double"] = "Double",
        ["System.Decimal"] = "Decimal",
        ["System.DateTime"] = "DateTime",
        ["System.Guid"] = "Guid",
        ["MongoDB.Bson.ObjectId"] = "ObjectId",
    };

    /// <summary>Renders the difference as a compilable migration.</summary>
    /// <param name="difference">What changed.</param>
    /// <param name="name">The migration's name, which becomes its title and type name.</param>
    /// <param name="namespaceName">The namespace of the generated file.</param>
    /// <param name="stamp">The moment the migration is ordered by.</param>
    /// <exception cref="InvalidOperationException">The difference carries refusals, which are never rendered.</exception>
    public static string Write(ModelDifference difference, string name, string namespaceName, DateTime stamp)
    {
        if (difference.Refusals.Count > 0)
        {
            throw new InvalidOperationException(
                "A difference that contains refusals is not written: the refused change would be missing from the " +
                "file while the snapshot advanced past it, leaving the store behind with nothing to say so.");
        }

        var typeName = Identifier(name);
        var text = new StringBuilder();

        text.AppendLine("// Generated from the difference between the model and the last snapshot, then yours to");
        text.AppendLine("// edit. Read it before running it: the tooling knows what moved, you know what it means.");
        text.AppendLine("#nullable enable");
        text.AppendLine();
        text.AppendLine("using eQuantic.Core.Data.Migration;");
        text.AppendLine();
        text.AppendLine($"namespace {namespaceName};");
        text.AppendLine();
        text.AppendLine($"/// <summary>{Escape(name)}.</summary>");
        text.AppendLine($"[Migration({Literal(name)}, {stamp.Year}, {stamp.Month}, {stamp.Day}, " +
                        $"{stamp.Hour}, {stamp.Minute}, {stamp.Second})]");
        text.AppendLine($"public sealed class {typeName} : Migration");
        text.AppendLine("{");
        text.AppendLine("    /// <inheritdoc />");
        text.AppendLine("    public override void Up(IMigrationBuilder migration)");
        text.AppendLine("    {");

        var body = new StringBuilder();
        foreach (var entity in difference.Changes.Select(change => change.EntityType).Distinct(StringComparer.Ordinal))
        {
            WriteEntity(body, entity, difference.Changes.Where(change => change.EntityType == entity).ToList());
        }

        text.Append(body.Length == 0
            ? "        // Nothing the model changed needs a step here.\n"
            : body.ToString());

        text.AppendLine("    }");
        text.AppendLine("}");
        return text.ToString();
    }

    private static void WriteEntity(StringBuilder text, string entityType, IReadOnlyList<ModelChange> changes)
    {
        // A dropped entity is the one case with no type left to name: the class it stood for is gone from the
        // model, and the store has no operation that removes a collection. It is written out and handed over.
        var undroppable = changes.Where(change => change.Kind == ModelChangeKind.DropCollection).ToList();
        var steps = new List<string>();
        var decisions = new List<string>();

        foreach (var change in changes.Except(undroppable))
        {
            Render(change, entityType, steps, decisions);
        }

        if (steps.Count > 0)
        {
            text.AppendLine($"        migration.For<{TypeReference(entityType)}>(entity => entity");
            for (var index = 0; index < steps.Count; index++)
            {
                text.Append("            ").Append(steps[index]);
                text.AppendLine(index == steps.Count - 1 ? ");" : string.Empty);
            }
        }

        foreach (var change in undroppable)
        {
            decisions.Add(
                $"'{entityType}' is no longer mapped. Dropping '{change.From}' would delete everything in it, and " +
                "nothing here can tell whether that data is finished with — so this one is left to you. " +
                $"migration.For<T>(t => t.DropCollection(\"{change.From}\")) is the operation, if it is what you " +
                "want. Then delete this line.");
        }

        foreach (var decision in decisions)
        {
            text.AppendLine($"#error {Collapse(decision)}");
        }

        text.AppendLine();
    }

    private static void Render(ModelChange change, string entityType, List<string> steps, List<string> decisions)
    {
        switch (change.Kind)
        {
            case ModelChangeKind.AddCollection:
                steps.Add(".EnsureCollection()");
                break;

            case ModelChangeKind.AddField when change.NeedsValue:
                steps.Add($".AddField(x => x.{change.Member})");
                steps.Add($".Update(_ => true, set => set.Set(x => x.{change.Member}, default!))");
                decisions.Add(
                    $"'{entityType}.{change.Member}' is added without saying what the records that already exist " +
                    "hold. Replace `default!` above with the value — or say it in the model, with [DefaultValue] " +
                    "on the member or .Default(…) in the configuration, and generate again — then delete this line.");
                break;

            case ModelChangeKind.AddField:
                steps.Add($".AddField(x => x.{change.Member})");
                if (change.DefaultLiteral is { } literal)
                {
                    steps.Add($".Update(_ => true, set => set.Set(x => x.{change.Member}, {literal}))");
                }

                break;

            case ModelChangeKind.DropField:
                if (change.AmbiguousRenameHint is { } hint)
                {
                    decisions.Add(hint + " Then delete this line.");
                }

                steps.Add($".DropField({Literal(change.From!)})");
                break;

            case ModelChangeKind.RenameField:
                // Both names stated: by now the model already answers with the new one.
                steps.Add($".RenameField({Literal(change.From!)}, {Literal(change.To!)})");
                break;

            case ModelChangeKind.ConvertField
                when FieldTypes.TryGetValue(change.From!, out var from) &&
                     FieldTypes.TryGetValue(change.To!, out var to):
                steps.Add($".ConvertField(x => x.{change.Member}, MigrationFieldType.{from}, MigrationFieldType.{to})");
                break;

            case ModelChangeKind.ConvertField:
                decisions.Add(
                    $"'{entityType}.{change.Member}' changes from {change.From} to {change.To}, which is not one of " +
                    "the conversions a store can perform on its own. Write the step by hand and delete this line.");
                break;

            case ModelChangeKind.ChangeFacets:
                steps.Add($".ResizeField(x => x.{change.Member})");
                break;

            case ModelChangeKind.RenameCollection:
                steps.Add($".RenameCollection({Literal(change.From!)}, {Literal(change.To!)})");
                break;

            case ModelChangeKind.DropCollection:
            default:
                break;
        }
    }

    /// <summary>Renders a CLR type name as a reference that cannot collide with a using.</summary>
    private static string TypeReference(string entityType) =>
        "global::" + entityType.Replace('+', '.');

    /// <summary>Turns a migration name into a valid type name, so a name with spaces still produces a file.</summary>
    private static string Identifier(string name)
    {
        var text = new StringBuilder();
        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                text.Append(character);
            }
        }

        if (text.Length == 0 || char.IsDigit(text[0]))
        {
            text.Insert(0, 'M');
        }

        return text.ToString();
    }

    /// <summary>A <c>#error</c> is one line, so the message becomes one.</summary>
    private static string Collapse(string message) =>
        string.Join(" ", message.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()));

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Literal(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
