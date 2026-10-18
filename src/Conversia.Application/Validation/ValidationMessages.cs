namespace Conversia.Application.Validation;

/// <summary>
/// The Spanish wording of every DataAnnotations rule of the request DTOs, in one place. {0} is the field's display name
/// (<c>[Display(Name = ...)]</c> on the property), {1} and {2} the rule's limits, as DataAnnotations formats them.
/// The custom attributes reuse these texts, so the API and the Web (which validates the same DTOs) say the same thing.
/// </summary>
public static class ValidationMessages
{
    public const string Required = "El campo {0} es obligatorio.";
    public const string Email = "El campo {0} no es válido.";
    public const string MaxLength = "El campo {0} no puede superar los {1} caracteres.";
    public const string Range = "El campo {0} debe estar entre {1} y {2}.";
    public const string EnumValue = "El campo {0} no es un valor reconocido.";
    public const string NullCharacter = "El campo {0} no puede contener caracteres nulos.";
    public const string Base64 = "El campo {0} debe ser texto en base64.";

    public static readonly string ProductName = $"El campo {{0}} {Domain.Entities.Product.NameRuleText}.";

    public static readonly string ProductSku = $"El campo {{0}} {Domain.Entities.Product.SkuRuleText}.";

    public static readonly string ProductPrice = $"El campo {{0}} {Domain.Entities.Product.PriceRuleText}.";

    public static readonly string StrongPassword =
        $"El campo {{0}} debe tener entre {StrongPasswordAttribute.MinLength} y {StrongPasswordAttribute.MaxLength} caracteres e incluir al menos una mayúscula, una minúscula y un dígito.";
}
