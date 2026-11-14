using eQuantic.Core.Data.Repository;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb;

/// <summary>
///     Reads MongoDB's "key taken" — error 11000 (<c>E11000 duplicate key error</c>) — out of every shape the driver
///     reports it in: a write's error, a bulk write's errors, a command's (<c>findAndModify</c>) — and names the index
///     the server named.
/// </summary>
internal static class MongoDuplicateKeys
{
    private const int DuplicateKey = 11000;

    /// <summary>The <see cref="DuplicateKeyException" /> for a driver exception that says a key is taken, or <c>null</c>.</summary>
    public static DuplicateKeyException? From(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current switch
            {
                MongoWriteException { WriteError: { } error } when IsDuplicate(error) => error.Message,
                MongoBulkWriteException bulk => bulk.WriteErrors.FirstOrDefault(IsDuplicate)?.Message,
                MongoCommandException { Code: DuplicateKey } command => command.ErrorMessage ?? command.Message,
                _ => null,
            };

            if (message is not null)
            {
                return new DuplicateKeyException(IndexName(message), exception);
            }
        }

        return null;
    }

    /// <summary>Whether a bulk write's error is a taken key.</summary>
    public static bool IsDuplicate(WriteError error) => error.Category == ServerErrorCategory.DuplicateKey || error.Code == DuplicateKey;

    /// <summary>Whether a bulk write's error is a taken key.</summary>
    public static bool IsDuplicate(BulkWriteError error) => error.Category == ServerErrorCategory.DuplicateKey || error.Code == DuplicateKey;

    /// <summary>The index in <c>E11000 duplicate key error collection: db.c index: name dup key: { … }</c>.</summary>
    private static string? IndexName(string message)
    {
        const string marker = "index: ";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = message.IndexOf(' ', start);
        return end > start ? message[start..end] : message[start..];
    }

    /// <summary>Runs a write and turns a taken key into <see cref="DuplicateKeyException" />.</summary>
    public static async Task<T> TranslatingAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not DuplicateKeyException && From(exception) is { } duplicate)
        {
            throw duplicate;
        }
    }
}
