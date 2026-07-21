using System.Diagnostics;

namespace eQuantic.Core.Data.Diagnostics;

/// <summary>
///     The shared <see cref="ActivitySource" /> every provider emits through — one name to subscribe to
///     (<c>eQuantic.Core.Data</c>) for OpenTelemetry/<see cref="ActivityListener" /> pipelines. Spans carry the
///     standard <c>db.*</c> tags plus the engine's own facts (<c>equantic.residual</c>,
///     <c>equantic.split_queries</c>, <c>equantic.partition_scoped</c>, staged write counts) — the things no
///     driver-level instrumentation can know. When nothing listens, the cost is a null check.
/// </summary>
public static class DataActivitySource
{
    /// <summary>The source name to register in a tracer provider (<c>AddSource("eQuantic.Core.Data")</c>).</summary>
    public const string Name = "eQuantic.Core.Data";

    /// <summary>The shared source.</summary>
    public static ActivitySource Instance { get; } = new(Name);
}
