using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>
///     A partition + clustering test entity: readings are partitioned by <see cref="SensorId" /> and ordered within
///     the partition by the clustering key <see cref="At" />. This is the shape that exercises the advanced CQL the
///     provider generates — native clustering ranges, <c>ORDER BY</c> a clustering key, and <c>token()</c> ranges on
///     the partition key. The <see cref="IEntity{TKey}" /> key is <see cref="SensorId" /> (the partition column).
/// </summary>
public sealed class Reading : IEntity<int>
{
    public int SensorId { get; set; }

    public DateTime At { get; set; }

    public double Value { get; set; }

    public string Quality { get; set; } = "";

    public int GetKey() => SensorId;

    public void SetKey(int key) => SensorId = key;
}
