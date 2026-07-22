using System;

namespace eQuantic.Core.Data.Repository;

/// <summary>
///     The write conventions every provider honours — register one as a singleton
///     (<c>services.AddSingleton(new DataConventions { ... })</c>) to tune them; the defaults apply otherwise.
///     <list type="bullet">
///         <item><see cref="Clock" /> — the time source behind <c>CreatedAt</c>/<c>UpdatedAt</c>/<c>DeletedAt</c>
///         stamps (inject a fixed <see cref="TimeProvider" /> in tests).</item>
///         <item><see cref="LifecycleStamps" /> / <see cref="SoftDelete" /> — the lifecycle conventions are
///         on by default for entities implementing the <c>eQuantic.Core.Domain</c> interfaces; turn them off
///         when the interfaces are adopted for other reasons.</item>
///         <item><see cref="CurrentUserId" /> — the <b>who</b> behind the stamps: a per-request accessor
///         (receives the scope's <see cref="IServiceProvider" />, like a global-filter factory). When set,
///         entities carrying <c>CreatedById</c>/<c>UpdatedById</c>/<c>DeletedById</c> members — the
///         <c>eQuantic.Core.DataModel</c> shapes — are stamped by property-name convention.</item>
///     </list>
/// </summary>
public sealed class DataConventions
{
    /// <summary>The time source for lifecycle stamps (<see cref="TimeProvider.System" /> by default).</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Whether <c>CreatedAt</c>/<c>UpdatedAt</c> (and the <c>…ById</c> members) stamp automatically.</summary>
    public bool LifecycleStamps { get; set; } = true;

    /// <summary>Whether deletes of <c>IEntityTimeEnded</c> entities become soft deletes with the automatic live-rows filter.</summary>
    public bool SoftDelete { get; set; } = true;

    /// <summary>
    ///     The current user's id for the <b>who</b> stamps, resolved per request from the scope's service
    ///     provider. Return <c>null</c> to leave the members untouched (e.g. background work with no user).
    /// </summary>
    public Func<IServiceProvider, object?>? CurrentUserId { get; set; }
}
