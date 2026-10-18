namespace Conversia.Application.DTOs.Diagnostics;

/// <summary>Answer of <c>POST /api/diagnostics/errors</c> (<c>201</c>): the code to quote, and whether a row was written.</summary>
public sealed record ReportErrorResponse(string Code, bool Persisted);
