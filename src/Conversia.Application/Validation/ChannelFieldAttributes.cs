using System.ComponentModel.DataAnnotations;
using Conversia.Domain.Entities;

namespace Conversia.Application.Validation;

/// <summary>The display phone number rule of <see cref="WhatsAppChannel"/>, applied to the trimmed value. Null is left to <see cref="RequiredAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class DisplayPhoneNumberAttribute : ValidationAttribute
{
    public DisplayPhoneNumberAttribute()
        : base(ValidationMessages.DisplayPhoneNumber)
    {
    }

    public override bool IsValid(object? value)
        => value is null || (value is string phone && WhatsAppChannel.IsValidDisplayPhoneNumber(phone));
}

/// <summary>The provider phone number id rule of <see cref="WhatsAppChannel"/>. Optional: null and blank are valid.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class ProviderPhoneNumberIdAttribute : ValidationAttribute
{
    public ProviderPhoneNumberIdAttribute()
        : base(ValidationMessages.ProviderPhoneNumberId)
    {
    }

    public override bool IsValid(object? value)
        => value is null || (value is string id && WhatsAppChannel.IsValidProviderPhoneNumberId(id));
}
