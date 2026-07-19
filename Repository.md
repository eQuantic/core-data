# Repository Pattern with Entity Framework

> **Version 5 note.** The contract-level examples below (entities, repository
> contracts, `QueryOptions`, `PagedResult`, unit-of-work usage) reflect the v5
> repository contracts shipped by this package. The Entity Framework provider
> types (`UnitOfWork`, `AsyncRepository`, …) reflect the v5 **target** shape and
> are being finalized alongside the provider reimplementation.

## UnitOfWork example:

```csharp
using eQuantic.Core.Data.EntityFramework.Repository;
using Microsoft.EntityFrameworkCore;

namespace eQuantic.Core.Web.Examples.Infrastructure
{
    public class ExampleUnitOfWork : UnitOfWork
    {
        public ExampleUnitOfWork(DbContext context) : base(context)
        {
        }
    }
}
```

## Entity data example:

Entities used with the repositories implement `IEntity<TKey>`, which ties an
entity to the type of its own key.

```csharp
using System;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Web.Examples.Infrastructure.Data
{
    public class UserData : IEntity<Guid>
    {
        public Guid Id { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;

        public Guid GetKey() => Id;
        public void SetKey(Guid key) => Id = key;
    }
}

namespace eQuantic.Core.Web.Examples.Infrastructure.Data
{
    public class PersonData : IEntity<Guid>
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateTime BirthDate { get; set; }
        public string? Phone { get; set; }
        public virtual UserData? User { get; set; }

        public Guid GetKey() => Id;
        public void SetKey(Guid key) => Id = key;
    }
}
```

## Repository example:

### Contract

The `TUnitOfWork` type parameter is no longer part of the repository contract.

```csharp
using System;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Web.Examples.Infrastructure.Data;

namespace eQuantic.Core.Web.Examples.Infrastructure.Repositories.Contracts
{
    public interface IPersonRepository : IAsyncRepository<PersonData, Guid>
    {
    }
}
```

### Implementation

```csharp
using System;
using eQuantic.Core.Data.EntityFramework.Repository;
using eQuantic.Core.Web.Examples.Infrastructure.Data;
using eQuantic.Core.Web.Examples.Infrastructure.Repositories.Contracts;

namespace eQuantic.Core.Web.Examples.Infrastructure.Repositories
{
    public class PersonRepository : AsyncRepository<PersonData, Guid>, IPersonRepository
    {
        public PersonRepository(IQueryableUnitOfWork unitOfWork) : base(unitOfWork)
        {
        }
    }
}
```

## Specification Pattern

```csharp
using System;
using System.Linq.Expressions;
using eQuantic.Core.Web.Examples.Infrastructure.Data;
using eQuantic.Linq.Specification;

namespace eQuantic.Core.Web.Examples.Domain.Specification
{
    public class PersonSpecification : Specification<PersonData>
    {
        private readonly string _term;

        public PersonSpecification(string term)
        {
            _term = term;
        }

        public override Expression<Func<PersonData, bool>> SatisfiedBy()
        {
            return p => p.Name.StartsWith(_term) || p.User.UserName.StartsWith(_term) || p.User.Email.StartsWith(_term);
        }
    }
}
```

## Domain Services

### Contract

```csharp
using System;
using System.Threading.Tasks;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Web.Examples.Domain.Entities;

namespace eQuantic.Core.Web.Examples.Domain.Services.Contracts
{
    public interface IPersonService
    {
        Task<Person?> GetAsync(Guid id);
        Task<bool> CreateAsync(Person person);
        Task<bool> UpdateAsync(Person person);
        Task<bool> DeleteAsync(Guid id);
        Task<PagedResult<Person>> FindAsync(string term, int pageIndex, int pageSize);
    }
}
```

### Implementation

Query shaping is expressed through `QueryOptions<TEntity>`, and paged reads
return `PagedResult<T>`.

```csharp
using System;
using System.Threading.Tasks;
using AutoMapper;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using eQuantic.Core.Web.Examples.Domain.Entities;
using eQuantic.Core.Web.Examples.Domain.Specification;
using eQuantic.Core.Web.Examples.Infrastructure;
using eQuantic.Core.Web.Examples.Infrastructure.Data;

namespace eQuantic.Core.Web.Examples.Domain.Services
{
    public class PersonService : IPersonService
    {
        public IMapper Mapper { get; }
        public ExampleUnitOfWork UnitOfWork { get; }

        public PersonService(IMapper mapper, ExampleUnitOfWork unitOfWork)
        {
            Mapper = mapper;
            UnitOfWork = unitOfWork;
        }

        public async Task<Person?> GetAsync(Guid id)
        {
            var repo = UnitOfWork.GetAsyncRepository<PersonData, Guid>();
            var item = await repo.GetAsync(id, new QueryOptions<PersonData>().Include(nameof(PersonData.User)));
            return item is null ? null : Mapper.Map<Person>(item);
        }

        public async Task<bool> CreateAsync(Person person)
        {
            var repo = UnitOfWork.GetAsyncRepository<PersonData, Guid>();
            var item = Mapper.Map<PersonData>(person);
            await repo.AddAsync(item);
            return await UnitOfWork.CommitAsync() > 0;
        }

        public async Task<bool> UpdateAsync(Person person)
        {
            var repo = UnitOfWork.GetAsyncRepository<PersonData, Guid>();
            var item = await repo.GetAsync(person.Id);
            if (item is null) return false;
            Mapper.Map(person, item);
            await repo.ModifyAsync(item);
            return await UnitOfWork.CommitAsync() > 0;
        }

        public async Task<bool> DeleteAsync(Guid id)
        {
            var repo = UnitOfWork.GetAsyncRepository<PersonData, Guid>();
            var item = await repo.GetAsync(id);
            if (item is null) return false;
            await repo.RemoveAsync(item);
            return await UnitOfWork.CommitAsync() > 0;
        }

        public async Task<PagedResult<Person>> FindAsync(string term, int pageIndex, int pageSize)
        {
            var repo = UnitOfWork.GetAsyncRepository<PersonData, Guid>();
            var options = new QueryOptions<PersonData>()
                .Where(new PersonSpecification(term))
                .Include(nameof(PersonData.User))
                .OrderBy("name");

            var page = await repo.GetPagedAsync(PageRequest.Of(pageIndex, pageSize), options);
            var persons = Mapper.Map<IReadOnlyList<Person>>(page.Items);

            return new PagedResult<Person>(persons, page.TotalCount, page.PageIndex, page.PageSize);
        }
    }
}
```

# DDD Pattern

## Domain Entity example:

```csharp
using System;

namespace eQuantic.Core.Web.Examples.Domain.Entities
{
    public class User
    {
        public Guid Id { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }
}

namespace eQuantic.Core.Web.Examples.Domain.Entities
{
    public class Person
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateTime BirthDate { get; set; }
        public string? Phone { get; set; }
        public virtual User? User { get; set; }
    }
}
```
