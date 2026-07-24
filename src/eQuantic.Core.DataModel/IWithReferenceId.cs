using System.Linq.Expressions;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.DataModel;

public interface IWithReferenceId<TDataEntity, TKey> where TDataEntity : IEntity
{
    TKey GetReferenceId();
    void SetReferenceId(TKey referenceId);

    /// <summary>
    /// Predicate scoping queries to the current reference (replaces the eQuantic.Linq v2
    /// <c>IFiltering</c>-based contract; a plain expression works with any provider).
    /// </summary>
    Expression<Func<TDataEntity, bool>> GetReferenceFilter();
}
