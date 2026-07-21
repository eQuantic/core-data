using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>A counter-table test entity: partition key <see cref="Space" />, counter column <see cref="Hits" />.</summary>
public sealed class Tally : IEntity<string>
{
    public string Space { get; set; } = "";

    public long Hits { get; set; }

    public string GetKey() => Space;

    public void SetKey(string key) => Space = key;
}
