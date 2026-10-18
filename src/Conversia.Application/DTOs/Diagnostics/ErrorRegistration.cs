namespace Conversia.Application.DTOs.Diagnostics;

/// <summary>The outcome of registering an error: its code, and whether a row was written.</summary>
public sealed record ErrorRegistration(string Code, bool Persisted);
