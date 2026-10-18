using Conversia.Domain.Common;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>A salesperson who answers customers from one branch. May be linked to an application user (no behavior yet).</summary>
public class Advisor : BaseEntity
{
    public const int NameMaxLength = 150;

    public static readonly string NameRuleText = TextRules.RuleText(NameMaxLength);

    public static readonly string NameRuleMessage = $"El nombre del asesor {NameRuleText}.";

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public Guid BranchId { get; private set; }

    public bool IsActive { get; private set; } = true;

    public Guid? UserId { get; private set; }

    // Required by EF Core for materialization.
    private Advisor()
    {
    }

    public static Advisor Create(Guid tenantId, Guid branchId, string name, Guid? userId = null)
    {
        if (userId == Guid.Empty)
        {
            throw new DomainException("El identificador del usuario no es válido.");
        }

        return new Advisor
        {
            TenantId = TextRules.RequiredId(tenantId, "El identificador del espacio de trabajo es obligatorio."),
            BranchId = TextRules.RequiredId(branchId, "La sucursal es obligatoria."),
            Name = TextRules.Required(name, NameMaxLength, NameRuleMessage),
            UserId = userId,
        };
    }
}
