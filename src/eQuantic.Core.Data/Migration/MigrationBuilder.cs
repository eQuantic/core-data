using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace eQuantic.Core.Data.Migration;

/// <summary>
///     The default recording <see cref="IMigrationBuilder" />: the fluent calls simply capture
///     <see cref="MigrationOperation" />s, which a provider's <see cref="IMigrationExecutor" /> later applies.
/// </summary>
public sealed class MigrationBuilder : IMigrationBuilder
{
    private readonly List<MigrationOperation> _operations = [];

    /// <inheritdoc />
    public IReadOnlyList<MigrationOperation> Operations => _operations;

    /// <inheritdoc />
    public IMigrationBuilder For<TEntity>(Action<ICollectionMigration<TEntity>> configure) where TEntity : class
    {
        configure(new CollectionMigration<TEntity>(_operations));
        return this;
    }

    /// <inheritdoc />
    public IMigrationBuilder Run(Func<IMigrationExecutionContext, CancellationToken, Task> operation)
    {
        _operations.Add(new RunOperation(operation));
        return this;
    }

    private sealed class CollectionMigration<TEntity>(List<MigrationOperation> ops)
        : ICollectionMigration<TEntity> where TEntity : class
    {
        public ICollectionMigration<TEntity> EnsureCollection()
        {
            ops.Add(new EnsureCollectionOperation(typeof(TEntity)));
            return this;
        }

        public ICollectionMigration<TEntity> Index<TField>(Expression<Func<TEntity, TField>> field, bool descending = false, bool unique = false)
        {
            ops.Add(new EnsureIndexOperation(typeof(TEntity), new IndexKey[] { new(field, descending) }) { Unique = unique });
            return this;
        }

        public ICollectionMigration<TEntity> Index<TField>(Expression<Func<TEntity, TField>> field, Action<IIndexOptions<TEntity>> options)
        {
            var builder = new IndexOptions<TEntity>();
            options(builder);
            ops.Add(new EnsureIndexOperation(typeof(TEntity), new IndexKey[] { new(field, builder.IsDescending) })
            {
                Unique = builder.IsUnique,
                Name = builder.IndexName,
                Filter = builder.FilterPredicate,
                Method = builder.IndexMethod,
                ExpireAfter = builder.ExpireAfter,
            });
            return this;
        }

        public ICollectionMigration<TEntity> AddField<TField>(Expression<Func<TEntity, TField>> field)
        {
            ops.Add(new AddFieldOperation(typeof(TEntity), field));
            return this;
        }

        public ICollectionMigration<TEntity> DropField(string field)
        {
            ops.Add(new DropFieldOperation(typeof(TEntity),
                !string.IsNullOrWhiteSpace(field) ? field : throw new ArgumentException("The stored field name is required.", nameof(field))));
            return this;
        }

        public ICollectionMigration<TEntity> CompositeIndex(Action<IIndexKeyBuilder<TEntity>> keys, bool unique = false)
        {
            var builder = new IndexKeyBuilder<TEntity>();
            keys(builder);
            ops.Add(new EnsureIndexOperation(typeof(TEntity), builder.Keys) { Unique = unique });
            return this;
        }

        public ICollectionMigration<TEntity> ConvertField<TField>(Expression<Func<TEntity, TField>> field, MigrationFieldType from, MigrationFieldType to)
        {
            ops.Add(new ConvertFieldOperation(typeof(TEntity), field, from, to));
            return this;
        }

        public ICollectionMigration<TEntity> ResizeField<TField>(Expression<Func<TEntity, TField>> field)
        {
            ops.Add(new ResizeFieldOperation(typeof(TEntity), field));
            return this;
        }

        public ICollectionMigration<TEntity> RenameCollection(string currentName, string newName)
        {
            ops.Add(new RenameCollectionOperation(typeof(TEntity), currentName, newName));
            return this;
        }

        public ICollectionMigration<TEntity> DropCollection(string name)
        {
            ops.Add(new DropCollectionOperation(typeof(TEntity), name));
            return this;
        }

        public ICollectionMigration<TEntity> RenameField<TField>(Expression<Func<TEntity, TField>> field, string newName)
        {
            ops.Add(new RenameFieldOperation(typeof(TEntity), field, newName));
            return this;
        }

        public ICollectionMigration<TEntity> RenameField(string currentName, string newName)
        {
            ops.Add(new RenameFieldOperation(typeof(TEntity), currentName, newName));
            return this;
        }

        public ICollectionMigration<TEntity> Update(Expression<Func<TEntity, bool>> predicate, Action<IUpdateBuilder<TEntity>> update)
        {
            var builder = new UpdateBuilder<TEntity>();
            update(builder);
            ops.Add(new UpdateOperation(typeof(TEntity), predicate, builder.Sets));
            return this;
        }
    }

    private sealed class IndexOptions<TEntity> : IIndexOptions<TEntity> where TEntity : class
    {
        public bool IsUnique { get; private set; }

        public bool IsDescending { get; private set; }

        public string? IndexName { get; private set; }

        public LambdaExpression? FilterPredicate { get; private set; }

        public IndexMethod IndexMethod { get; private set; }

        public TimeSpan? ExpireAfter { get; private set; }

        public IIndexOptions<TEntity> Unique()
        {
            IsUnique = true;
            return this;
        }

        public IIndexOptions<TEntity> Descending()
        {
            IsDescending = true;
            return this;
        }

        public IIndexOptions<TEntity> Named(string name)
        {
            IndexName = name;
            return this;
        }

        public IIndexOptions<TEntity> Filtered(Expression<Func<TEntity, bool>> predicate)
        {
            FilterPredicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
            return this;
        }

        public IIndexOptions<TEntity> Gin()
        {
            IndexMethod = IndexMethod.Gin;
            return this;
        }

        public IIndexOptions<TEntity> Text()
        {
            IndexMethod = IndexMethod.Text;
            return this;
        }

        public IIndexOptions<TEntity> Ttl(TimeSpan expireAfter)
        {
            ExpireAfter = expireAfter;
            return this;
        }
    }

    private sealed class IndexKeyBuilder<TEntity> : IIndexKeyBuilder<TEntity> where TEntity : class
    {
        public List<IndexKey> Keys { get; } = [];

        public IIndexKeyBuilder<TEntity> Ascending<TField>(Expression<Func<TEntity, TField>> field)
        {
            Keys.Add(new IndexKey(field, false));
            return this;
        }

        public IIndexKeyBuilder<TEntity> Descending<TField>(Expression<Func<TEntity, TField>> field)
        {
            Keys.Add(new IndexKey(field, true));
            return this;
        }
    }

    private sealed class UpdateBuilder<TEntity> : IUpdateBuilder<TEntity> where TEntity : class
    {
        public List<FieldSet> Sets { get; } = [];

        public IUpdateBuilder<TEntity> Set<TField>(Expression<Func<TEntity, TField>> field, TField value)
        {
            Sets.Add(new FieldSet(field, value));
            return this;
        }
    }
}
