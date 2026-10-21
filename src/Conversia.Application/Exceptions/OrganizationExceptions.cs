namespace Conversia.Application.Exceptions;

/// <summary>A branch that does not exist, or exists in another tenant. The API layer maps this to 404.</summary>
public class BranchNotFoundException : Exception
{
    public const string DefaultMessage = "Sucursal no encontrada.";

    public BranchNotFoundException()
        : base(DefaultMessage)
    {
    }
}

/// <summary>An advisor that does not exist, or exists in another tenant. The API layer maps this to 404.</summary>
public class AdvisorNotFoundException : Exception
{
    public const string DefaultMessage = "Asesor no encontrado.";

    public AdvisorNotFoundException()
        : base(DefaultMessage)
    {
    }
}

/// <summary>A channel that does not exist, or exists in another tenant. The API layer maps this to 404.</summary>
public class ChannelNotFoundException : Exception
{
    public const string DefaultMessage = "Canal no encontrado.";

    public ChannelNotFoundException()
        : base(DefaultMessage)
    {
    }
}

/// <summary>The tenant already holds a branch with that name. The API layer maps this to 409.</summary>
public class DuplicateBranchNameException : Exception
{
    public const string DefaultMessage = "Ya existe una sucursal con ese nombre.";

    public DuplicateBranchNameException()
        : base(DefaultMessage)
    {
    }
}

/// <summary>A conversation that does not exist, or exists in another tenant. The API layer maps this to 404.</summary>
public class ConversationNotFoundException : Exception
{
    public const string DefaultMessage = "Conversación no encontrada.";

    public ConversationNotFoundException()
        : base(DefaultMessage)
    {
    }
}

/// <summary>A company that does not exist. The API layer maps this to 404 (the platform administrator's API) or 401 (a token whose company is gone).</summary>
public class TenantNotFoundException : Exception
{
    public const string DefaultMessage = "Empresa no encontrada.";

    public TenantNotFoundException()
        : base(DefaultMessage)
    {
    }
}
