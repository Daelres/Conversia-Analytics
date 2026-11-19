using System.Reflection;
using Conversia.Application.Reports;

namespace Conversia.Application.UnitTests.Reports;

/// <summary>
/// The data pack can hold no customer data (docs/REPORTS.md, "Privacy"): its types are walked, and every text field must be one of a short list of
/// company-internal names. A field added to the pack that could carry a message, a phone or an email fails here until it is reviewed.
/// </summary>
public class ReportDataPackGuardTests
{
    // The only text a pack may carry: the company, the period and scope in words, the section names, and names of branches, advisors, sources
    // and analysis labels. Reviewed one by one; add a name here only after checking it can never hold a customer's data.
    private static readonly HashSet<string> AllowedTextFields =
        ["ReportDataPack.Version", "ReportDataPack.Company", "ReportDataPack.Frequency", "ReportDataPack.Scope", "ReportDataPack.Sections",
         "ReportPackPeriod.Label", "ReportConversionRate.Label", "ReportConversion.WindowLabel", "ReportGroupRate.Name", "ReportGroupResponse.Name", "ReportShare.Label", "ReportDropOff.MainStage"];

    private static readonly Type[] Scalars = [typeof(int), typeof(int?), typeof(long), typeof(double), typeof(double?), typeof(bool), typeof(DateOnly), typeof(DateOnly?)];

    [Fact]
    public void Every_field_of_the_pack_is_a_number_a_date_a_flag_or_a_reviewed_company_name()
    {
        var seen = new HashSet<Type>();
        var texts = new List<string>();
        Walk(typeof(ReportDataPack), seen, texts);

        Assert.All(texts, field => Assert.Contains(field, AllowedTextFields));
        Assert.True(seen.Count >= 10, "the walk reached the section types");
    }

    [Fact]
    public void No_field_is_named_like_personal_data()
    {
        var seen = new HashSet<Type>();
        Walk(typeof(ReportDataPack), seen, []);

        var names = seen.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)).Select(p => p.Name).ToList();
        foreach (var forbidden in new[] { "Phone", "Email", "Message", "Text", "Customer", "Contact", "WaId", "Body", "Address", "Number" })
        {
            Assert.DoesNotContain(names, n => n.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void Walk(Type type, HashSet<Type> seen, List<string> texts)
    {
        if (!seen.Add(type))
        {
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
        {
            var t = property.PropertyType;
            if (Scalars.Contains(t))
            {
                continue;
            }

            if (t == typeof(string) || t == typeof(IReadOnlyList<string>))
            {
                texts.Add($"{type.Name}.{property.Name}");
                continue;
            }

            var element = t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ? t.GetGenericArguments()[0] : t;
            Assert.True(element.Namespace == typeof(ReportDataPack).Namespace && element.Name.StartsWith("Report", StringComparison.Ordinal),
                $"{type.Name}.{property.Name} is of an unexpected type {t}");
            Walk(element, seen, texts);
        }
    }
}
