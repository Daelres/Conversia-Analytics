namespace Conversia.Web.Components.Shared;

/// <summary>What a <c>FormField</c> hands to its control: the ARIA attributes to splat, and whether the field is invalid.</summary>
public sealed class FieldAttributes : Dictionary<string, object>
{
    public bool Invalid { get; init; }
}
