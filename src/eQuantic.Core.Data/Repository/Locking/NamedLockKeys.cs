using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace eQuantic.Core.Data.Repository.Locking;

/// <summary>
///     How a lock's name becomes the key a store locks on. Both forms are pure functions of the name's UTF-8
///     bytes and of SHA-256, so every process, machine and version of this library derives the same key from the same
///     name.
///     <para>
///         <b>Provider SPI</b>: providers call it when they take a named lock; application code never needs it.
///     </para>
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class NamedLockKeys
{
    /// <summary>The first character of a hashed text key; a name that starts with it is always hashed.</summary>
    public const char HashedPrefix = '~';

    /// <summary>The fewest hex characters a hashed text key keeps (128 bits of the digest).</summary>
    private const int MinimumHexLength = 32;

    /// <summary>
    ///     The 64-bit key of a name: the first eight bytes of the SHA-256 digest of its UTF-8 bytes, read big-endian
    ///     as a signed integer (PostgreSQL's <c>bigint</c> advisory-lock key).
    /// </summary>
    /// <param name="name">The lock's name.</param>
    public static long Int64(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(name), digest);
        return BinaryPrimitives.ReadInt64BigEndian(digest);
    }

    /// <summary>
    ///     The text key of a name: the name itself when it has at most <paramref name="maxLength" /> characters, every
    ///     one of them printable ASCII without a space (<c>!</c> to <c>~</c>) that <paramref name="allowed" /> accepts,
    ///     and it does not start with <see cref="HashedPrefix" />; otherwise <see cref="HashedPrefix" /> followed by the
    ///     lower-case hex SHA-256 digest of its UTF-8 bytes, cut to <paramref name="maxLength" />. Two different names
    ///     never share a key (short of a SHA-256 collision): a name kept as it is never starts with the prefix.
    /// </summary>
    /// <param name="name">The lock's name.</param>
    /// <param name="maxLength">The longest key the store accepts (at least 33).</param>
    /// <param name="allowed">The characters the store keeps apart, beyond printable ASCII; <c>null</c> accepts them all.</param>
    public static string Text(string name, int maxLength, Func<char, bool>? allowed = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (maxLength < MinimumHexLength + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), maxLength,
                $"A text key keeps at least {MinimumHexLength + 1} characters, so that a hashed name stays unique.");
        }

        if (IsPlain(name, maxLength, allowed))
        {
            return name;
        }

        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant();
        return HashedPrefix + hex[..Math.Min(hex.Length, maxLength - 1)];
    }

    private static bool IsPlain(string name, int maxLength, Func<char, bool>? allowed)
    {
        if (name.Length == 0 || name.Length > maxLength || name[0] == HashedPrefix)
        {
            return false;
        }

        foreach (var character in name)
        {
            if (character is < '!' or > '~' || (allowed is not null && !allowed(character)))
            {
                return false;
            }
        }

        return true;
    }
}
