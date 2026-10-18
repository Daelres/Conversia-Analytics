using Conversia.Application.DTOs.Diagnostics;

namespace Conversia.Application.Interfaces;

public interface IErrorRegistry
{
    /// <summary>
    /// Sanitizes, codes, fingerprints and (storm control permitting) stores the error. Never throws: when persistence
    /// fails the error is logged with its code and <see cref="ErrorRegistration.Persisted"/> is false.
    /// </summary>
    Task<ErrorRegistration> RegisterAsync(ErrorReport report, CancellationToken cancellationToken = default);
}
