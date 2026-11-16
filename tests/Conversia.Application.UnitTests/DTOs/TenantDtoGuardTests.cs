using System.Reflection;
using Conversia.Application.DTOs.Conversations;

namespace Conversia.Application.UnitTests.DTOs;

/// <summary>
/// The provider's media id (<c>Messages.MediaId</c>) is kept only so roadmap stage 3 can download a file within Meta's window: until that
/// stage decides how media is shown, no answer of the API carries it (docs/WEBHOOKS.md, "Media metadata"). Same for the context of a
/// phone-app echo (<c>AssignedAdvisorIdAtReceipt</c>), which is not authorship.
/// </summary>
public class TenantDtoGuardTests
{
    private static readonly string[] NotExposed = ["MediaId", "AssignedAdvisorIdAtReceipt"];

    [Fact]
    public void NoAnswerOfTheApi_ExposesTheMediaId_NorTheEchoContext()
    {
        var offending = typeof(MessageResponse).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("Conversia.Application.DTOs", StringComparison.Ordinal) == true && t.IsClass)
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => (Type: t, Property: p)))
            .Where(x => NotExposed.Contains(x.Property.Name, StringComparer.OrdinalIgnoreCase))
            .Select(x => $"{x.Type.FullName}.{x.Property.Name}")
            .ToList();

        Assert.Empty(offending);
    }
}
