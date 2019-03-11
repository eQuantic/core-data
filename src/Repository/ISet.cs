using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace eQuantic.Core.Data.Repository
{
    public interface ISet<TEntity> : IQueryable<TEntity> where TEntity : class, IEntity, new()
    {
        IEnumerable<TEntity> Execute();
    }
}
