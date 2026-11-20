namespace Conversia.Web.Services;

/// <summary>The Spanish words for the coexistence state of a line (docs/MESSAGING.md, "Coexistence").</summary>
public static class CoexistenceText
{
    /// <summary>Why Meta disconnected the number, from the reason word of <c>account_update</c>.</summary>
    public static string Disconnection(string? reason) => reason switch
    {
        "PRIMARY_INACTIVITY" => "La aplicación WhatsApp Business no se abrió en unos 14 días. Ábrela y vuelve a conectar el número en 360dialog.",
        "COMPANION_INACTIVITY" => "Un dispositivo vinculado estuvo inactivo unos 30 días. Vuelve a conectar el número en 360dialog.",
        "ACCOUNT_DISCONNECTED" => "La cuenta se desconectó (por una medida de Meta o porque se eliminó).",
        "BUSINESS_DOWNGRADE" => "El número se registró en la aplicación de WhatsApp personal.",
        "CHANGE_NUMBER" => "El número de la cuenta cambió.",
        "USER_RE_REGISTERED" => "El número se registró de nuevo en otro dispositivo.",
        _ => "Meta desconectó el número de la API. Vuelve a conectarlo en 360dialog.",
    };
}
