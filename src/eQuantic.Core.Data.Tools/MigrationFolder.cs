using System.Globalization;
using System.Text;

namespace eQuantic.Core.Data.Tools;

/// <summary>
///     The folder generated files go in, read the same on every system: <c>/</c> and <c>\</c> both separate its
///     segments, empty ones (a leading, trailing or doubled separator) are dropped, and it always lies inside the
///     project. Its segments give the generated files' namespace, each made a C# identifier the way an IDE names a
///     class added to a folder.
/// </summary>
internal sealed class MigrationFolder
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    private MigrationFolder(IReadOnlyList<string> segments) => Segments = segments;

    /// <summary>The folder's segments, as they are named on disk.</summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>Reads <c>--output</c>.</summary>
    /// <param name="output">The folder as it was given, relative to the project.</param>
    /// <exception cref="ToolException">It names no folder, or one outside the project.</exception>
    public static MigrationFolder Parse(string output)
    {
        var segments = output.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new ToolException($"--output '{output}' names no folder. Give one relative to the project, such as Migrations.");
        }

        foreach (var segment in segments)
        {
            if (segment.Trim() is "." or ".." || segment.Contains(':'))
            {
                throw new ToolException(
                    $"--output '{output}' is not a folder inside the project ('{segment}'). Give one relative to the " +
                    "project, such as Migrations or Data/Migrations: its segments also name the generated files' namespace.");
            }
        }

        return new MigrationFolder(segments);
    }

    /// <summary>Where the folder is, under <paramref name="projectDirectory" />.</summary>
    /// <param name="projectDirectory">The project's directory.</param>
    public string PathUnder(string projectDirectory) => Path.Combine([projectDirectory, .. Segments]);

    /// <summary>The namespace of the files generated into the folder: the project's, then one identifier per segment.</summary>
    /// <param name="rootNamespace">The project's root namespace.</param>
    public string Namespace(string rootNamespace) =>
        string.Join('.', Segments.Select(Identifier).Prepend(rootNamespace));

    /// <summary>
    ///     A segment as a C# identifier: a character an identifier cannot hold becomes <c>_</c>, one that would start
    ///     it with a digit is preceded by <c>_</c>, and a keyword is escaped with <c>@</c>.
    /// </summary>
    /// <param name="segment">The folder's name.</param>
    internal static string Identifier(string segment)
    {
        var text = new StringBuilder(segment.Length + 1);
        foreach (var character in segment)
        {
            text.Append(IsPart(character) ? character : '_');
        }

        if (!IsStart(text[0]))
        {
            text.Insert(0, '_');
        }

        var identifier = text.ToString();
        return Keywords.Contains(identifier) ? "@" + identifier : identifier;
    }

    private static bool IsStart(char character) =>
        character == '_' || char.IsLetter(character)
                         || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.LetterNumber;

    private static bool IsPart(char character) =>
        IsStart(character) || CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.Format;
}
