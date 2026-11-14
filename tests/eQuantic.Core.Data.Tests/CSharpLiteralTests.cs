using System.Reflection;
using eQuantic.Core.Data.Evolution;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace eQuantic.Core.Data.Tests;

/// <summary>Declares an enum inside a type, as an entity often does.</summary>
public sealed class LiteralOwner
{
    public enum Status
    {
        Draft,
        Active = 5,
        Retired = -1,
    }
}

[Flags]
public enum LiteralAccess
{
    None = 0,
    Read = 1,
    Write = 2,
    Admin = 4,
}

public enum LiteralWide : long
{
    Small = 1,
    Huge = 5_000_000_000,
}

public enum LiteralUnsigned : ulong
{
    Top = ulong.MaxValue,
}

public enum LiteralTiny : sbyte
{
    Low = -100,
}

/// <summary>
///     A rendered literal is only right if the compiler reads it back as the value it was rendered from, so every case
///     here is compiled with Roslyn and its value compared, type included.
/// </summary>
[TestFixture]
public sealed class CSharpLiteralTests
{
    private static readonly object[] Cases =
    [
        LiteralOwner.Status.Active,
        LiteralOwner.Status.Retired,
        (LiteralOwner.Status)42,
        (LiteralOwner.Status)(-7),
        LiteralAccess.None,
        LiteralAccess.Admin,
        LiteralAccess.Read | LiteralAccess.Write,
        LiteralAccess.Read | LiteralAccess.Write | LiteralAccess.Admin | (LiteralAccess)64,
        LiteralWide.Huge,
        (LiteralWide)(-5_000_000_001),
        (LiteralWide)long.MinValue,
        (LiteralOwner.Status)int.MinValue,
        LiteralUnsigned.Top,
        (LiteralUnsigned)(ulong.MaxValue - 1),
        LiteralTiny.Low,
        (LiteralTiny)(-128),
        double.NaN,
        double.PositiveInfinity,
        double.NegativeInfinity,
        float.NaN,
        float.PositiveInfinity,
        float.NegativeInfinity,
        1.5d,
        -0.25f,
        double.MaxValue,
        float.Epsilon,
    ];

    private static object[] Compile(IReadOnlyList<string> literals)
    {
        var source = $$"""
            namespace Probe;

            public static class Literals
            {
                public static object[] Values => new object[]
                {
                    {{string.Join(",\n            ", literals)}}
                };
            }
            """;

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Append(typeof(LiteralOwner).Assembly.Location)
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(paths => MetadataReference.CreateFromFile(paths.First()));
        var compilation = CSharpCompilation.Create("Literals_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp12))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        Assert.That(result.Success, Is.True, source + Environment.NewLine + string.Join(Environment.NewLine,
            result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));

        var type = Assembly.Load(image.ToArray()).GetType("Probe.Literals")!;
        return (object[])type.GetProperty("Values")!.GetValue(null)!;
    }

    [Test]
    public void Every_rendered_literal_compiles_back_to_the_value_it_came_from()
    {
        var literals = Cases.Select(value => CSharpLiteral.Render(value)!).ToList();

        var values = Compile(literals);

        Assert.That(values, Has.Length.EqualTo(Cases.Length));
        Assert.Multiple(() =>
        {
            for (var index = 0; index < Cases.Length; index++)
            {
                Assert.That(values[index].GetType(), Is.EqualTo(Cases[index].GetType()), literals[index]);
                Assert.That(values[index], Is.EqualTo(Cases[index]), literals[index]);
            }
        });
    }

    [Test]
    public void A_defined_member_is_written_by_name_and_any_other_value_as_a_cast()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CSharpLiteral.Render(LiteralOwner.Status.Active),
                Is.EqualTo("eQuantic.Core.Data.Tests.LiteralOwner.Status.Active"), "a nested enum is named as C# names it");
            Assert.That(CSharpLiteral.Render(LiteralAccess.Admin), Is.EqualTo("eQuantic.Core.Data.Tests.LiteralAccess.Admin"),
                "a top-level enum's member is written as it always was");
            Assert.That(CSharpLiteral.Render(LiteralAccess.Read | LiteralAccess.Write),
                Is.EqualTo("(eQuantic.Core.Data.Tests.LiteralAccess)3"), "a combination of flags is its number");
            Assert.That(CSharpLiteral.Render((LiteralOwner.Status)42), Is.EqualTo("(eQuantic.Core.Data.Tests.LiteralOwner.Status)42"));
            Assert.That(CSharpLiteral.Render((LiteralOwner.Status)(-7)),
                Is.EqualTo("(eQuantic.Core.Data.Tests.LiteralOwner.Status)(-7)"), "(T)-7 would read as a subtraction");
        });
    }

    [Test]
    public void A_number_that_is_not_finite_is_written_as_the_constant_naming_it()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CSharpLiteral.Render(double.NaN), Is.EqualTo("double.NaN"));
            Assert.That(CSharpLiteral.Render(double.PositiveInfinity), Is.EqualTo("double.PositiveInfinity"));
            Assert.That(CSharpLiteral.Render(double.NegativeInfinity), Is.EqualTo("double.NegativeInfinity"));
            Assert.That(CSharpLiteral.Render(float.NaN), Is.EqualTo("float.NaN"));
            Assert.That(CSharpLiteral.Render(float.PositiveInfinity), Is.EqualTo("float.PositiveInfinity"));
            Assert.That(CSharpLiteral.Render(float.NegativeInfinity), Is.EqualTo("float.NegativeInfinity"));
        });
    }

    [Test]
    public void An_enum_inside_a_generic_type_is_refused_by_name()
    {
        var error = Assert.Throws<NotSupportedException>(() => CSharpLiteral.Render(GenericHolder<int>.Mode.On));
        Assert.That(error!.Message, Does.Contain("Mode").And.Contain("generic"));
    }

    private static class GenericHolder<T>
    {
        public enum Mode
        {
            Off,
            On,
        }
    }
}
