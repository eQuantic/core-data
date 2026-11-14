using System;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace eQuantic.Core.Data.Repository.Write;

/// <summary>
///     A write repository that stages an update of <b>some</b> of an entity's members: the next commit writes the
///     members named (and the concurrency token and the update's lifecycle stamps the model declares) and leaves every
///     other stored member as it is — so two requests that changed different members of one row both keep their
///     change, where a full <c>Modify</c> would write back what the other changed. Every provider's repository
///     implements it: cast to it or resolve it from DI.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
public interface IPartialModifyRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
    /// <summary>Stages an update that writes only the members <paramref name="members" /> names.</summary>
    /// <param name="item">The entity, carrying its key and the new values.</param>
    /// <param name="members">The members to write: one (<c>x =&gt; x.Title</c>) or several (<c>x =&gt; new { x.Title, x.Locked }</c>).</param>
    void Modify(TEntity item, Expression<Func<TEntity, object?>> members);

    /// <summary>Stages an update that writes only the members <paramref name="members" /> names.</summary>
    /// <param name="item">The entity, carrying its key and the new values.</param>
    /// <param name="members">The members to write: one (<c>x =&gt; x.Title</c>) or several (<c>x =&gt; new { x.Title, x.Locked }</c>).</param>
    Task ModifyAsync(TEntity item, Expression<Func<TEntity, object?>> members);
}
