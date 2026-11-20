namespace Conversia.Domain.Exceptions;

/// <summary>Another licence that is not retired already has this number. The API layer maps it to 409.</summary>
public class DuplicateLineLicenseException : Exception
{
    public DuplicateLineLicenseException(Exception? innerException = null)
        : base("Ya hay una licencia vigente para ese número.", innerException)
    {
    }
}
