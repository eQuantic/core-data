using eQuantic.Core.Data.Repository.Sql;

namespace eQuantic.Core.Data.Repository;

public interface IDefaultUnitOfWork : ISqlUnitOfWork<ISqlUnitOfWork>
{
    
}
