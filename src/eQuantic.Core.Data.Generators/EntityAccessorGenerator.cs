using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace eQuantic.Core.Data.Generators;

/// <summary>
///     Generates a reflection-free <c>EntityAccessor</c> (create / get / set behind a name switch) for every
///     entity in the compilation — a non-abstract, non-generic class or record implementing
///     <c>eQuantic.Core.Data.Repository.IEntity</c> with an accessible parameterless constructor — plus one
///     module initializer registering them all. Entities the generated code could not honor faithfully are
///     skipped entirely (no accessor beats a wrong one): init-only or setter-less public properties would make
///     the generated <c>Set</c> silently lossy where reflection is not, so those types stay on the reflection
///     path.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class EntityAccessorGenerator : IIncrementalGenerator
{
    private const string EntityInterface = "eQuantic.Core.Data.Repository.IEntity";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var entities = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax { BaseList: not null } and (ClassDeclarationSyntax or RecordDeclarationSyntax),
                static (syntaxContext, _) => Model(syntaxContext))
            .Where(static model => model is not null)
            .Collect();

        context.RegisterSourceOutput(entities, static (production, models) =>
        {
            var distinct = models
                .OfType<EntityModel>()
                .GroupBy(model => model.FullName)
                .Select(group => group.First())
                .OrderBy(model => model.FullName)
                .ToList();

            if (distinct.Count == 0)
            {
                return;
            }

            production.AddSource("eQuanticEntityAccessors.g.cs", SourceText.From(Emit(distinct), Encoding.UTF8));
        });
    }

    private static EntityModel? Model(GeneratorSyntaxContext context)
    {
        if (context.SemanticModel.GetDeclaredSymbol(context.Node) is not INamedTypeSymbol symbol)
        {
            return null;
        }

        if (symbol.IsAbstract || symbol.IsGenericType || symbol.IsStatic
            || symbol.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal)
            || symbol.ContainingType is not null)
        {
            return null;
        }

        if (!symbol.AllInterfaces.Any(candidate => candidate.ToDisplayString() == EntityInterface))
        {
            return null;
        }

        if (!symbol.InstanceConstructors.Any(constructor =>
                constructor.Parameters.Length == 0
                && constructor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal))
        {
            return null;
        }

        // GetMembers() is declared-only: the inheritance chain must be walked, or entities built on base
        // classes (the DataModel audit bases, most prominently) would generate accessors blind to their
        // inherited members — a silently lossy Set. Most-derived declarations win (the seen-guard).
        var members = new List<MemberModel>();
        var seen = new HashSet<string>();
        for (var current = symbol; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is not IPropertySymbol property
                    || property.IsStatic || property.IsIndexer
                    || property.DeclaredAccessibility != Accessibility.Public
                    || property.GetMethod is not { DeclaredAccessibility: Accessibility.Public }
                    || !seen.Add(property.Name))
                {
                    continue;
                }

                var settable = property.SetMethod is { DeclaredAccessibility: Accessibility.Public } setter
                               && !setter.IsInitOnly;

                // A public property the generated Set could not write (init-only / private-set / no setter)
                // would make the accessor silently lossy where reflection is not — the whole type stays on
                // the reflection path.
                if (!settable && property.SetMethod is not null)
                {
                    return null;
                }

                members.Add(new MemberModel(property.Name,
                    property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), settable));
            }
        }

        return new EntityModel(symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Sanitize(symbol.ToDisplayString()), members);
    }

    private static string Sanitize(string display) =>
        new(display.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());

    private static string Emit(IReadOnlyList<EntityModel> entities)
    {
        var source = new StringBuilder();
        source.AppendLine("// <auto-generated by eQuantic.Core.Data.Generators/>");
        source.AppendLine("#nullable enable");
        source.AppendLine("namespace eQuantic.Core.Data.Generated");
        source.AppendLine("{");

        foreach (var entity in entities)
        {
            source.AppendLine($"    internal sealed class {entity.AccessorName} : global::eQuantic.Core.Data.Repository.EntityAccessor");
            source.AppendLine("    {");
            source.AppendLine($"        public override object Create() => new {entity.FullName}();");
            source.AppendLine();
            source.AppendLine("        public override object? Get(object entity, string member)");
            source.AppendLine("        {");
            source.AppendLine($"            var typed = ({entity.FullName})entity;");
            source.AppendLine("            return member switch");
            source.AppendLine("            {");
            foreach (var member in entity.Members)
            {
                source.AppendLine($"                \"{member.Name}\" => typed.{member.Name},");
            }

            source.AppendLine("                _ => null,");
            source.AppendLine("            };");
            source.AppendLine("        }");
            source.AppendLine();
            source.AppendLine("        public override void Set(object entity, string member, object? value)");
            source.AppendLine("        {");
            source.AppendLine($"            var typed = ({entity.FullName})entity;");
            source.AppendLine("            switch (member)");
            source.AppendLine("            {");
            foreach (var member in entity.Members.Where(candidate => candidate.Settable))
            {
                source.AppendLine($"                case \"{member.Name}\": typed.{member.Name} = ({member.Type})value!; break;");
            }

            source.AppendLine("            }");
            source.AppendLine("        }");
            source.AppendLine("    }");
            source.AppendLine();
        }

        source.AppendLine("    internal static class eQuanticEntityAccessorRegistration");
        source.AppendLine("    {");
        source.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        source.AppendLine("        internal static void Register()");
        source.AppendLine("        {");
        foreach (var entity in entities)
        {
            source.AppendLine("            global::eQuantic.Core.Data.Repository.EntityAccessors.Register(" +
                              $"typeof({entity.FullName}), new {entity.AccessorName}());");
        }

        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine("}");
        return source.ToString();
    }

    private sealed class EntityModel(string fullName, string sanitized, List<MemberModel> members)
    {
        public string FullName { get; } = fullName;
        public string AccessorName { get; } = sanitized + "_Accessor";
        public IReadOnlyList<MemberModel> Members { get; } = members;
    }

    private sealed class MemberModel(string name, string type, bool settable)
    {
        public string Name { get; } = name;
        public string Type { get; } = type;
        public bool Settable { get; } = settable;
    }
}
