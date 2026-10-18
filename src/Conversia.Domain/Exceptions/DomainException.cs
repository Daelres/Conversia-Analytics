namespace Conversia.Domain.Exceptions;

/// <summary>
/// Thrown when a domain invariant is violated while constructing or mutating
/// an entity (e.g. an empty required field, a malformed value).
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message)
    {
    }

    public DomainException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
