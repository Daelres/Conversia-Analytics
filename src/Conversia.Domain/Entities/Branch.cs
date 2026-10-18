using Conversia.Domain.Common;

namespace Conversia.Domain.Entities;

/// <summary>A physical location of a tenant. The name is unique within the tenant, ignoring letter case.</summary>
public class Branch : BaseEntity
{
    public const int NameMaxLength = 100;

    public static readonly string NameRuleText = TextRules.RuleText(NameMaxLength);

    public static readonly string NameRuleMessage = $"El nombre de la sucursal {NameRuleText}.";

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public bool IsActive { get; private set; } = true;

    // Required by EF Core for materialization.
    private Branch()
    {
    }

    public static Branch Create(Guid tenantId, string name)
        => new()
        {
            TenantId = TextRules.RequiredId(tenantId, "El identificador del espacio de trabajo es obligatorio."),
            Name = TextRules.Required(name, NameMaxLength, NameRuleMessage),
        };
}
