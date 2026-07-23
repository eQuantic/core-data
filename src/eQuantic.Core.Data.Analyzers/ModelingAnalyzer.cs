using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace eQuantic.Core.Data.Analyzers;

/// <summary>
///     Analyzes entity types annotated with the <c>eQuantic.Core.Data.Modeling</c> vocabulary and reports the
///     misuses that are wrong on <b>every</b> provider (see <see cref="ModelingDiagnostics" />) — the same
///     violations the model builders throw for at runtime, surfaced while typing. Attributes are matched by
///     full metadata name, so the analyzer carries no dependency on the data packages.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ModelingAnalyzer : DiagnosticAnalyzer
{
    private const string Ns = "eQuantic.Core.Data.Modeling.";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        ModelingDiagnostics.ConcurrencyTokenType,
        ModelingDiagnostics.DuplicatePartitionKeyOrder,
        ModelingDiagnostics.DuplicateClusteringKeyOrder,
        ModelingDiagnostics.MultipleEntityKeys,
        ModelingDiagnostics.InvalidTimeToLive,
        ModelingDiagnostics.InvalidFacet,
        ModelingDiagnostics.UnmappedConflict,
        ModelingDiagnostics.SearchIndexOnNonString,
        ModelingDiagnostics.CounterOnNonIntegral,
        ModelingDiagnostics.GeneratedKeyType,
        ModelingDiagnostics.EmptyStorageName);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeType, SymbolKind.NamedType);
    }

    private static void AnalyzeType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct)
        {
            return;
        }

        foreach (var attribute in type.GetAttributes())
        {
            switch (Name(attribute))
            {
                case "EntityAttribute" when Text(attribute, 0) is { Length: 0 }:
                    context.ReportDiagnostic(Diagnostic.Create(ModelingDiagnostics.EmptyStorageName,
                        Location(attribute, type), type.Name, "Entity"));
                    break;
                case "TimeToLiveAttribute" when Int(attribute, 0) is { } seconds and <= 0:
                    context.ReportDiagnostic(Diagnostic.Create(ModelingDiagnostics.InvalidTimeToLive,
                        Location(attribute, type), type.Name, seconds));
                    break;
            }
        }

        var entityKeys = 0;
        var partitionOrders = new List<(int Order, AttributeData Attribute, IPropertySymbol Property)>();
        var clusteringOrders = new List<(int Order, AttributeData Attribute, IPropertySymbol Property)>();

        foreach (var member in type.GetMembers())
        {
            if (member is not IPropertySymbol property)
            {
                continue;
            }

            var unmapped = false;
            var mappingAnnotations = new List<string>();

            foreach (var attribute in property.GetAttributes())
            {
                var name = Name(attribute);
                if (name is null)
                {
                    continue;
                }

                if (name != "UnmappedAttribute")
                {
                    mappingAnnotations.Add(name.Substring(0, name.Length - "Attribute".Length));
                }

                switch (name)
                {
                    case "UnmappedAttribute":
                        unmapped = true;
                        break;

                    case "ConcurrencyTokenAttribute" when !IsTokenCapable(property.Type):
                        context.ReportDiagnostic(Diagnostic.Create(ModelingDiagnostics.ConcurrencyTokenType,
                            Location(attribute, property), property.Name, property.Type.ToDisplayString()));
                        break;

                    case "EntityKeyAttribute":
                        entityKeys++;
                        if (entityKeys == 2)
                        {
                            context.ReportDiagnostic(Diagnostic.Create(ModelingDiagnostics.MultipleEntityKeys,
                                Location(attribute, property), type.Name));
                        }

                        if (Named(attribute, "Generated") is true && !IsIntegral(Unwrap(property.Type)))
                        {
                            context.ReportDiagnostic(Diagnostic.Create(ModelingDiagnostics.GeneratedKeyType,
                                Location(attribute, property), property.Name, property.Type.ToDisplayString()));
                        }

                        break;

                    case "PartitionKeyAttribute":
                        partitionOrders.Add((Named(attribute, "Order") as int? ?? 0, attribute, property));
                        break;

                    case "ClusteringKeyAttribute":
                        clusteringOrders.Add((Named(attribute, "Order") as int? ?? 0, attribute, property));
                        break;

                    case "SearchIndexAttribute" when Unwrap(property.Type).SpecialType != SpecialType.System_String:
                        context.ReportDiagnostic(Diagnostic.Create(ModelingDiagnostics.SearchIndexOnNonString,
                            Location(attribute, property), property.Name, property.Type.ToDisplayString()));
                        break;

                    case "CounterAttribute" when !IsIntegral(Unwrap(property.Type)):
                        context.ReportDiagnostic(Diagnostic.Create(ModelingDiagnostics.CounterOnNonIntegral,
                            Location(attribute, property), property.Name, property.Type.ToDisplayString()));
                        break;

                    case "StoredAsAttribute" when Text(attribute, 0) is { Length: 0 }:
                        context.ReportDiagnostic(Diagnostic.Create(ModelingDiagnostics.EmptyStorageName,
                            Location(attribute, property), property.Name, "StoredAs"));
                        break;

                    case "FacetAttribute" when FacetProblem(attribute, property) is { } problem:
                        context.ReportDiagnostic(Diagnostic.Create(ModelingDiagnostics.InvalidFacet,
                            Location(attribute, property), property.Name, problem));
                        break;
                }
            }

            if (unmapped && mappingAnnotations.Count > 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(ModelingDiagnostics.UnmappedConflict,
                    property.Locations.FirstOrDefault() ?? type.Locations[0],
                    property.Name, string.Join("], [", mappingAnnotations)));
            }
        }

        ReportDuplicateOrders(context, type, partitionOrders, ModelingDiagnostics.DuplicatePartitionKeyOrder);
        ReportDuplicateOrders(context, type, clusteringOrders, ModelingDiagnostics.DuplicateClusteringKeyOrder);
    }

    private static void ReportDuplicateOrders(SymbolAnalysisContext context, INamedTypeSymbol type,
        List<(int Order, AttributeData Attribute, IPropertySymbol Property)> declared, DiagnosticDescriptor descriptor)
    {
        if (declared.Count < 2)
        {
            return;
        }

        foreach (var collision in declared.GroupBy(entry => entry.Order).Where(group => group.Count() > 1))
        {
            foreach (var (order, attribute, property) in collision)
            {
                context.ReportDiagnostic(Diagnostic.Create(descriptor, Location(attribute, property), type.Name, order));
            }
        }
    }

    /// <summary>What is wrong with the facet, or <c>null</c> when nothing is.</summary>
    private static string? FacetProblem(AttributeData attribute, IPropertySymbol property)
    {
        var length = Named(attribute, "Length") as int? ?? 0;
        var precision = Named(attribute, "Precision") as int? ?? 0;
        var scale = Named(attribute, "Scale") as int? ?? 0;
        var memberType = Unwrap(property.Type);

        if (length < 0 || precision < 0 || scale < 0)
        {
            return "facet values cannot be negative";
        }

        if (scale > 0 && precision == 0)
        {
            return "Scale needs a Precision";
        }

        if (scale > precision && precision > 0)
        {
            return $"Scale ({scale}) cannot exceed Precision ({precision})";
        }

        if (length > 0 && memberType.SpecialType != SpecialType.System_String)
        {
            return $"Length applies to string members and '{property.Type.ToDisplayString()}' is not one";
        }

        if (precision > 0 && memberType.SpecialType != SpecialType.System_Decimal)
        {
            return $"Precision/Scale apply to decimal members and '{property.Type.ToDisplayString()}' is not one";
        }

        if (length == 0 && precision == 0)
        {
            return "it declares nothing (set Length, or Precision/Scale)";
        }

        return null;
    }

    private static bool IsTokenCapable(ITypeSymbol declared)
    {
        var type = Unwrap(declared);
        return type.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_String
               || type.ToDisplayString() == "System.Guid";
    }

    private static bool IsIntegral(ITypeSymbol type) =>
        type.SpecialType is SpecialType.System_SByte or SpecialType.System_Byte
            or SpecialType.System_Int16 or SpecialType.System_UInt16
            or SpecialType.System_Int32 or SpecialType.System_UInt32
            or SpecialType.System_Int64 or SpecialType.System_UInt64;

    /// <summary>Unwraps <c>Nullable&lt;T&gt;</c> to <c>T</c>.</summary>
    private static ITypeSymbol Unwrap(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;

    /// <summary>The attribute's short name when it belongs to the modeling vocabulary, else <c>null</c>.</summary>
    private static string? Name(AttributeData attribute)
    {
        var display = attribute.AttributeClass?.ToDisplayString();
        return display is not null && display.StartsWith(Ns, StringComparison.Ordinal)
            ? display.Substring(Ns.Length)
            : null;
    }

    private static string? Text(AttributeData attribute, int index) =>
        attribute.ConstructorArguments.Length > index ? attribute.ConstructorArguments[index].Value as string : null;

    private static int? Int(AttributeData attribute, int index) =>
        attribute.ConstructorArguments.Length > index ? attribute.ConstructorArguments[index].Value as int? : null;

    private static object? Named(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(argument => argument.Key == name).Value.Value;

    private static Location Location(AttributeData attribute, ISymbol fallback) =>
        attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation()
        ?? fallback.Locations.FirstOrDefault()
        ?? Microsoft.CodeAnalysis.Location.None;
}
