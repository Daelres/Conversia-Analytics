using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;

namespace Conversia.Web.Components.Shared;

/// <summary>
/// The text box of <c>FormField</c>: an <c>InputText</c> that can also be flagged invalid from outside the <c>EditContext</c>
/// (a message the API reported). <c>InputBase</c> drops an <c>aria-invalid</c> that the edit context does not know about,
/// so the attribute is rendered here instead of being passed through.
/// </summary>
public sealed class TextInput : InputBase<string?>
{
    /// <summary>True when the API rejected the value; local validation failures are flagged by the base class.</summary>
    [Parameter]
    public bool Invalid { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "input");
        builder.AddMultipleAttributes(1, AdditionalAttributes);
        if (!string.IsNullOrEmpty(NameAttributeValue))
        {
            builder.AddAttribute(2, "name", NameAttributeValue);
        }

        if (!string.IsNullOrEmpty(CssClass))
        {
            builder.AddAttribute(3, "class", CssClass);
        }

        if (Invalid)
        {
            builder.AddAttribute(4, "aria-invalid", "true");
        }

        builder.AddAttribute(5, "value", CurrentValueAsString);
        builder.CloseElement();
    }

    protected override bool TryParseValueFromString(string? value, out string? result, [NotNullWhen(false)] out string? validationErrorMessage)
    {
        result = value;
        validationErrorMessage = null;
        return true;
    }
}
