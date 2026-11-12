using System.Text.RegularExpressions;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Common;

/// <summary>The shape of a stored keyed hash (lowercase hex HMAC-SHA256) and of the id of the key that made it.</summary>
public static partial class KeyedHashes
{
    public const int Length = 64;
    public const int KeyIdMaxLength = 32;

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Hex();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyId();

    public static bool IsKeyId(string? id) => id is not null && KeyId().IsMatch(id);

    public static string Required(string? hash)
        => hash is not null && Hex().IsMatch(hash) ? hash : throw new DomainException("La huella no es válida.");

    public static string RequiredKeyId(string? id)
        => IsKeyId(id) ? id! : throw new DomainException("El identificador de la llave de privacidad no es válido.");
}
