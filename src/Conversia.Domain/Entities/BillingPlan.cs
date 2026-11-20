using System.Text.RegularExpressions;
using Conversia.Domain.Common;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>
/// A plan of the platform's catalogue (docs/BILLING.md, "Plans"), managed by the platform administrator: a monthly price per billed line and
/// the AI conversations a month it includes. Prices are the owner's, never hard-coded. Platform-level: no tenant query filter. A plan in use
/// is never deleted, only deactivated (no new subscription can take it; the existing ones keep it).
/// </summary>
public partial class BillingPlan : BaseEntity
{
    public const int CodeMaxLength = 40;
    public const int NameMaxLength = 100;

    public static readonly string CodeRuleMessage =
        $"El código del plan es obligatorio: letras minúsculas, números, guiones y guiones bajos, hasta {CodeMaxLength} caracteres.";

    public static readonly string NameRuleMessage = $"El nombre del plan {TextRules.RuleText(NameMaxLength)}.";

    public static readonly string IncludedAiRuleMessage =
        $"Las conversaciones con IA incluidas deben ser un número entero entre 0 y {Tenant.MaxAiMonthlyConversationQuota:N0}.";

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$", RegexOptions.CultureInvariant)]
    internal static partial Regex CodeShape();

    /// <summary>Stable, unique, lowercase: what the administrator and the charge snapshot name the plan by.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>The monthly price of one billed line, COP in cents (whole pesos).</summary>
    public long PricePerLineInCents { get; private set; }

    /// <summary>The AI conversations a month the plan includes: the company's AI quota when the administrator set none of its own.</summary>
    public int IncludedAiConversations { get; private set; }

    public bool IsActive { get; private set; } = true;

    private BillingPlan()
    {
    }

    public static BillingPlan Create(string code, string name, long pricePerLineInCents, int includedAiConversations)
    {
        var plan = new BillingPlan { Code = ValidCode(code) };
        plan.Update(name, pricePerLineInCents, includedAiConversations, isActive: true);
        return plan;
    }

    public void Update(string name, long pricePerLineInCents, int includedAiConversations, bool isActive)
    {
        Name = TextRules.Required(name, NameMaxLength, NameRuleMessage);
        PricePerLineInCents = BillingMoney.Price(pricePerLineInCents);
        IncludedAiConversations = includedAiConversations is >= 0 and <= Tenant.MaxAiMonthlyConversationQuota
            ? includedAiConversations
            : throw new DomainException(IncludedAiRuleMessage);
        IsActive = isActive;
    }

    internal static string ValidCode(string? code)
    {
        var trimmed = code?.Trim();
        return trimmed is { Length: > 0 and <= CodeMaxLength } && CodeShape().IsMatch(trimmed) ? trimmed : throw new DomainException(CodeRuleMessage);
    }
}

/// <summary>
/// An optional package of extra AI conversations a plan offers, at a price. A subscription picks one and a quantity; every paid charge then
/// raises the company's AI quota by <c>quantity x conversations</c> for the period it pays (docs/BILLING.md, "AI add-ons").
/// </summary>
public class BillingAddOn : BaseEntity
{
    public const int MaxQuantity = 20;

    public static readonly string ConversationsRuleMessage =
        $"Las conversaciones del paquete deben ser un número entero entre 1 y {Tenant.MaxAiMonthlyConversationQuota:N0}.";

    public Guid PlanId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>The AI conversations one package adds to the period's quota.</summary>
    public int Conversations { get; private set; }

    /// <summary>The price of one package for one period, COP in cents.</summary>
    public long PriceInCents { get; private set; }

    public bool IsActive { get; private set; } = true;

    private BillingAddOn()
    {
    }

    public static BillingAddOn Create(Guid planId, string code, string name, int conversations, long priceInCents)
    {
        var addOn = new BillingAddOn
        {
            PlanId = TextRules.RequiredId(planId, "El plan es obligatorio."),
            Code = BillingPlan.ValidCode(code),
        };
        addOn.Update(name, conversations, priceInCents, isActive: true);
        return addOn;
    }

    public void Update(string name, int conversations, long priceInCents, bool isActive)
    {
        Name = TextRules.Required(name, BillingPlan.NameMaxLength, BillingPlan.NameRuleMessage);
        Conversations = conversations is >= 1 and <= Tenant.MaxAiMonthlyConversationQuota ? conversations : throw new DomainException(ConversationsRuleMessage);
        PriceInCents = BillingMoney.Price(priceInCents);
        IsActive = isActive;
    }
}
