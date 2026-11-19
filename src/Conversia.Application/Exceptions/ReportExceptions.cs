namespace Conversia.Application.Exceptions;

/// <summary>A report or report definition that does not exist, exists in another company, or that the caller may not read. The API maps it to 404.</summary>
public class ReportNotFoundException : Exception
{
    public const string DefaultMessage = "Informe no encontrado.";

    public const string InvalidLinkMessage = "El enlace no es válido o el informe ya no existe.";

    public ReportNotFoundException()
        : base(DefaultMessage)
    {
    }

    public ReportNotFoundException(string message)
        : base(message)
    {
    }
}

/// <summary>The company reached its number of report definitions. The API maps it to 409 with the code <c>report_limit</c>.</summary>
public class ReportLimitException(int max) : Exception($"Tu empresa puede tener como máximo {max} informes automáticos. Elimina uno para crear otro.")
{
    public const string Code = "report_limit";
}
