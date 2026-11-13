using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.Tests;

/// <summary>A parent with a collection of related entities, for the relation-count and include-shape tests.</summary>
public sealed class Album
{
    public int Id { get; set; }

    public string Title { get; set; } = "";

    public List<Track>? Tracks { get; set; }

    public List<string> Genres { get; set; } = [];
}

/// <summary>A related entity holding the foreign key back to its album.</summary>
public sealed class Track
{
    public int Id { get; set; }

    public int AlbumId { get; set; }

    public string Name { get; set; } = "";

    public int? Position { get; set; }
}

/// <summary>
///     Unit tests for the store-agnostic read shapes — no store: the relation counts a projection holds, the shape an
///     include records, where nulls go and the window of a read without a total. Every provider builds on these.
/// </summary>
[TestFixture]
public sealed class ReadShapesTests
{
    private static bool IsTracks(System.Reflection.PropertyInfo property) => property.Name == nameof(Album.Tracks);

    [Test]
    public void A_projection_s_relation_counts_are_taken_out_in_order()
    {
        var projection = RelationCountInterpreter.Interpret<Album, object>(
            x => new { x.Title, All = x.Tracks!.Count(), Named = x.Tracks!.LongCount(t => t.Name != ""), Size = x.Tracks!.Count },
            IsTracks)!;

        Assert.That(projection.Counts, Has.Count.EqualTo(3));
        Assert.That(projection.Counts.Select(count => count.Predicate is null), Is.EqualTo(new[] { true, false, true }));
        Assert.That(projection.Counts.All(count => count.ElementType == typeof(Track)), Is.True);
        Assert.That(projection.Members, Is.EquivalentTo(new[] { nameof(Album.Title) }), "the navigation is no longer read");

        var row = projection.Projection.Compile()(new Album { Title = "Blue" }, [7, 5, 7]);
        Assert.That(row.ToString(), Is.EqualTo("{ Title = Blue, All = 7, Named = 5, Size = 7 }"),
            "each count reads its slot, converted to the type the map asked for");
    }

    [Test]
    public void A_projection_without_relation_counts_is_left_alone()
    {
        Assert.That(RelationCountInterpreter.Interpret<Album, int>(x => x.Genres.Count, IsTracks), Is.Null,
            "a count over a member that is not a relation is the store's ordinary projection");
        Assert.That(RelationCountInterpreter.Interpret<Album, string>(x => x.Title, IsTracks), Is.Null);
    }

    [Test]
    public void A_projection_using_the_entity_whole_says_so()
    {
        var projection = RelationCountInterpreter.Interpret<Album, object>(x => new { Album = x, Tracks = x.Tracks!.Count() }, IsTracks)!;

        Assert.That(projection.Members, Is.Null);
    }

    [Test]
    public void An_include_records_its_shape_and_its_path()
    {
        var options = new QueryOptions<Album>()
            .Include(x => x.Tracks, tracks => tracks
                .Where(t => t.Name != "")
                .Where(t => t.Position != null)
                .OrderBy(t => t.Position, NullsOrder.Last)
                .ThenByDescending(t => t.Name)
                .Skip(1)
                .Take(3));

        var shape = options.IncludeShapes[nameof(Album.Tracks)];
        Assert.That(options.IncludePaths, Does.Contain(nameof(Album.Tracks)), "a shaped include is an include like any other");
        Assert.That(shape.ElementType, Is.EqualTo(typeof(Track)));
        Assert.That(shape.Filter!.Compile().DynamicInvoke(new Track { Name = "a", Position = 1 }), Is.EqualTo(true));
        Assert.That(shape.Filter!.Compile().DynamicInvoke(new Track { Name = "a" }), Is.EqualTo(false), "two Where calls mean both");
        Assert.That(shape.Sortings.Select(sort => (sort.Descending, sort.Nulls)),
            Is.EqualTo(new[] { (false, NullsOrder.Last), (true, NullsOrder.Default) }));
        Assert.That((shape.SkipCount, shape.TakeCount), Is.EqualTo(((int?)1, (int?)3)));

        var plain = new QueryOptions<Album>().Include(x => x.Tracks);
        Assert.That(plain.IncludeShapes, Is.Empty, "an include without a shape loads everything and records no shape");
    }

    [Test]
    public void Nulls_placement_rides_with_its_sort()
    {
        var options = new QueryOptions<Track>().OrderBy(x => x.Position, NullsOrder.First).ThenBy(x => x.Name);

        Assert.That(options.Sortings.Select(options.NullsOrderOf), Is.EqualTo(new[] { NullsOrder.First, NullsOrder.Default }));
        Assert.That(ReadShapes.IsNullsSmallest(NullsOrder.First, descending: false), Is.True);
        Assert.That(ReadShapes.IsNullsSmallest(NullsOrder.First, descending: true), Is.False);
        Assert.That(ReadShapes.IsNullsSmallest(NullsOrder.Last, descending: true), Is.True);
    }

    [Test]
    public void Rows_ordered_in_memory_place_nulls_as_asked_and_compare_text_ordinally()
    {
        var tracks = new[]
        {
            new Track { Id = 1, Name = "b", Position = 2 },
            new Track { Id = 2, Name = "B", Position = null },
            new Track { Id = 3, Name = "a", Position = 1 },
        };

        int[] Ids(IReadOnlyList<MemorySort> sorts) => ReadShapes.Order(tracks, sorts).Select(track => track.Id).ToArray();
        System.Linq.Expressions.Expression<Func<Track, int?>> position = x => x.Position;
        System.Linq.Expressions.Expression<Func<Track, string>> name = x => x.Name;

        Assert.That(Ids([new MemorySort(position, false, NullsOrder.Last)]), Is.EqualTo(new[] { 3, 1, 2 }));
        Assert.That(Ids([new MemorySort(position, true, NullsOrder.First)]), Is.EqualTo(new[] { 2, 1, 3 }));
        Assert.That(Ids([new MemorySort(position, false, NullsOrder.Default)]), Is.EqualTo(new[] { 2, 3, 1 }),
            "by default nulls sort as the smallest value");
        Assert.That(Ids([new MemorySort(name, false, NullsOrder.Default)]), Is.EqualTo(new[] { 2, 3, 1 }), "'B' < 'a' < 'b' ordinally");
    }

    [Test]
    public void The_window_of_a_read_honours_its_own_limit()
    {
        Assert.That(ReadShapes.Window(new QueryOptions<Track>().Skip(10).Take(10), null), Is.EqualTo(((int?)10, (int?)10)));
        Assert.That(ReadShapes.Window(new QueryOptions<Track>().Take(10), 1), Is.EqualTo(((int?)null, (int?)1)), "a first reads one row");
        Assert.That(ReadShapes.Window(new QueryOptions<Track>().Take(0), 2), Is.EqualTo(((int?)null, (int?)0)));
        Assert.That(ReadShapes.Window<Track>(null, null), Is.EqualTo(((int?)null, (int?)null)));

        Assert.That(() => ReadShapes.RefuseRowWindow(new QueryOptions<Track>().Skip(1), "CountAsync"),
            Throws.InstanceOf<NotSupportedException>().With.Message.Contains("CountAsync"));
        Assert.That(() => new QueryOptions<Track>().Take(-1), Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.That(() => ReadShapes.RefuseForDistinct(new QueryOptions<Track>().OrderBy(x => x.Name)),
            Throws.InstanceOf<NotSupportedException>());
    }
}
