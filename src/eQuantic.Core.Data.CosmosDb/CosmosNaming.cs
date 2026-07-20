namespace eQuantic.Core.Data.CosmosDb;

/// <summary>Naming helpers shared across the provider — camelCase to match the System.Text.Json web serializer.</summary>
internal static class CosmosNaming
{
    /// <summary>Lower-cases the first character (e.g. <c>CountryCode</c> → <c>countryCode</c>).</summary>
    public static string CamelCase(string name) =>
        string.IsNullOrEmpty(name) || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
