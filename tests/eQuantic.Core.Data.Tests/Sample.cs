namespace eQuantic.Core.Data.Tests;

/// <summary>A POCO with varied member kinds for exercising the filter interpreter.</summary>
public sealed class Sample
{
    public int TenantId { get; set; }

    public string Name { get; set; } = "";

    public bool IsActive { get; set; }

    public decimal Total { get; set; }

    public List<string> Tags { get; set; } = [];

    public Dictionary<string, int> Attributes { get; set; } = [];

    public DateTime CreatedAt { get; set; }
}
