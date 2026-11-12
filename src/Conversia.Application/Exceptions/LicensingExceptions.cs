namespace Conversia.Application.Exceptions;

/// <summary>The licence does not exist. The API layer maps this to 404.</summary>
public class LineLicenseNotFoundException : Exception
{
    public LineLicenseNotFoundException()
        : base("Licencia no encontrada.")
    {
    }
}

/// <summary>The licence is not in the state the change needs. The API layer maps this to 409 with <see cref="Code"/>.</summary>
public class LineLicenseConflictException : Exception
{
    public const string NotAvailable = "license_not_available";
    public const string NotAssigned = "license_not_assigned";
    public const string StillAssigned = "license_assigned";

    public LineLicenseConflictException(string code)
        : base(MessageOf(code))
    {
        Code = code;
    }

    public string Code { get; }

    private static string MessageOf(string code) => code switch
    {
        NotAvailable => "La licencia no está disponible: está asignada a una empresa o retirada.",
        NotAssigned => "La licencia no está asignada a ninguna empresa.",
        StillAssigned => "La licencia sigue asignada a una empresa: libérala antes de retirarla.",
        _ => "La licencia no admite ese cambio ahora.",
    };
}

/// <summary>
/// <c>Licensing:RequireAssignedLicense</c> is on and the company has no licence assigned for this number. The API layer maps this to 409 with
/// <see cref="Code"/>.
/// </summary>
public class LicenseRequiredException : Exception
{
    public const string Code = "license_required";

    public LicenseRequiredException()
        : base("Esta línea no tiene una licencia de 360dialog asignada a tu empresa. Contacta al administrador de Conversia.")
    {
    }
}
