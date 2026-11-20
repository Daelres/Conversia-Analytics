using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

public class MessageTemplateTests
{
    private static readonly DateTime T0 = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    private static MessageTemplate Template(string? body = "Hola {{1}}, tu cita es el {{2}}.", TemplateStatus status = TemplateStatus.Approved, bool supported = true)
        => MessageTemplate.Create(Guid.NewGuid(), Guid.NewGuid(), "recordatorio_cita", "es", TemplateCategory.Utility, status, body, supported, T0);

    [Fact]
    public void Placeholders_Positional_AreOrderedByNumber()
        => Assert.Equal(["1", "2", "3"], TemplatePlaceholders.Parse("{{2}} y {{1}} y {{3}} y otra vez {{1}}"));

    [Fact]
    public void Placeholders_Named_AreInOrderOfFirstAppearance()
        => Assert.Equal(["first_name", "order_number"], TemplatePlaceholders.Parse("Gracias, {{first_name}}! Pedido {{ order_number }} de {{first_name}}."));

    [Fact]
    public void Placeholders_NoneInAPlainText()
        => Assert.Empty(TemplatePlaceholders.Parse("Hola, ¿cómo estás?"));

    [Fact]
    public void Create_ReadsTheParametersFromTheBody()
    {
        var template = Template();

        Assert.Equal(["1", "2"], template.ParameterNames);
        Assert.Equal(2, template.ParameterCount);
        Assert.False(template.NamedParameters);
        Assert.True(template.CanBeSent);
    }

    [Theory]
    [InlineData(TemplateStatus.Pending)]
    [InlineData(TemplateStatus.Rejected)]
    [InlineData(TemplateStatus.Paused)]
    [InlineData(TemplateStatus.Disabled)]
    [InlineData(TemplateStatus.Unknown)]
    public void OnlyAnApprovedTemplate_CanBeSent(TemplateStatus status)
        => Assert.False(Template(status: status).CanBeSent);

    [Fact]
    public void AnUnsupportedTemplate_CannotBeSent_EvenApproved()
        => Assert.False(Template(supported: false).CanBeSent);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Nombre")] // upper case is not a Meta template name
    [InlineData("con espacio")]
    public void Create_RefusesAnInvalidName(string name)
        => Assert.Throws<DomainException>(() => MessageTemplate.Create(Guid.NewGuid(), Guid.NewGuid(), name, "es", TemplateCategory.Utility, TemplateStatus.Approved, "Hola", true, T0));

    [Theory]
    [InlineData("")]
    [InlineData("español")]
    [InlineData("es-CO")] // Meta writes es_CO
    public void Create_RefusesAnInvalidLanguage(string language)
        => Assert.Throws<DomainException>(() => MessageTemplate.Create(Guid.NewGuid(), Guid.NewGuid(), "hola", language, TemplateCategory.Utility, TemplateStatus.Approved, "Hola", true, T0));

    [Fact]
    public void Render_ReplacesEachPlaceholder()
        => Assert.Equal("Hola Ana, tu cita es el lunes.", Template().Render(["Ana", "lunes"]));

    [Fact]
    public void Render_Named_UsesTheValuesInTheOrderOfTheNames()
    {
        var template = Template("Gracias, {{first_name}}! Pedido {{order}}. {{first_name}}");

        Assert.True(template.NamedParameters);
        Assert.Equal("Gracias, Ana! Pedido 42. Ana", template.Render(["Ana", "42"]));
    }

    [Fact]
    public void Render_DoesNotReinterpretAValueThatLooksLikeAPlaceholder()
        => Assert.Equal("Hola {{2}}, tu cita es el lunes.", Template().Render(["{{2}}", "lunes"]));

    [Fact]
    public void ValidateParameters_RefusesAWrongCount()
    {
        var template = Template();

        Assert.Throws<DomainException>(() => template.ValidateParameters(["Ana"]));
        Assert.Throws<DomainException>(() => template.ValidateParameters(["Ana", "lunes", "extra"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("dos\nlíneas")]
    [InlineData("con\ttab")]
    [InlineData("cinco     espacios")]
    [InlineData("nul\0")]
    public void ValidateParameters_RefusesAValueWhatsAppRefuses(string value)
        => Assert.Throws<DomainException>(() => Template().ValidateParameters([value, "lunes"]));

    [Fact]
    public void ValidateParameters_RefusesAValueLongerThanTheLimit()
        => Assert.Throws<DomainException>(() => Template().ValidateParameters([new string('a', MessageTemplate.ParameterMaxLength + 1), "lunes"]));

    [Fact]
    public void ValidateParameters_TrimsAndKeepsTheValues()
        => Assert.Equal(["Ana", "lunes 3"], Template().ValidateParameters(["  Ana ", "lunes 3"]));

    [Fact]
    public void Refresh_ChangesWhatTheProviderSays_AndKeepsTheIdentity()
    {
        var template = Template();
        var id = template.Id;

        template.Refresh(TemplateCategory.Marketing, TemplateStatus.Paused, "Hola {{1}}", true, T0.AddHours(1));

        Assert.Equal(id, template.Id);
        Assert.Equal(TemplateCategory.Marketing, template.Category);
        Assert.Equal(TemplateStatus.Paused, template.Status);
        Assert.Equal(["1"], template.ParameterNames);
        Assert.Equal(T0.AddHours(1), template.SyncedAt);
    }

    [Fact]
    public void ChangeStatus_FromTheWebhook_ChangesOnlyTheStatus()
    {
        var template = Template();

        template.ChangeStatus(TemplateStatus.Disabled, T0.AddMinutes(3));

        Assert.Equal(TemplateStatus.Disabled, template.Status);
        Assert.Equal(T0.AddMinutes(3), template.StatusChangedAt);
        Assert.Equal(T0, template.SyncedAt);
    }

    [Fact]
    public void ABodyWithTooManyParameters_IsUnsupported()
    {
        var body = string.Join(" ", Enumerable.Range(1, MessageTemplate.MaxParameters + 1).Select(i => $"{{{{{i}}}}}"));

        var template = Template(body);

        Assert.False(template.IsSupported);
        Assert.Empty(template.ParameterNames);
    }

    [Fact]
    public void Message_RecordsTheTemplateItSent()
    {
        var message = Message.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "wamid.1", MessageDirection.Outbound, "template", "Hola Ana", T0,
            MessageSender.Conversia, Guid.NewGuid(), template: new MessageTemplateUse("recordatorio_cita", TemplateCategory.Utility));

        Assert.Equal("recordatorio_cita", message.TemplateName);
        Assert.Equal(TemplateCategory.Utility, message.TemplateCategory);
    }

    [Fact]
    public void Message_ATemplateIsOnlyOnAConversiaSend()
        => Assert.Throws<DomainException>(() => Message.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "wamid.1", MessageDirection.Inbound, "template", "Hola", T0,
            template: new MessageTemplateUse("recordatorio_cita", TemplateCategory.Utility)));

    [Fact]
    public void Tenant_ReadReceiptsAreOffByDefault_AndCanBeSwitched()
    {
        var tenant = Tenant.Create("Empresa");
        Assert.False(tenant.ReadReceiptsEnabled);

        Assert.True(tenant.SetReadReceipts(true));
        Assert.True(tenant.ReadReceiptsEnabled);
        Assert.False(tenant.SetReadReceipts(true));
    }

    // ---- Order of status changes (review fix 2) ----------------------------------------------

    [Fact]
    public void ChangeStatus_OlderThanTheLastNotification_IsIgnored()
    {
        var template = Template();

        Assert.True(template.ChangeStatus(TemplateStatus.Paused, T0.AddMinutes(10)));
        Assert.False(template.ChangeStatus(TemplateStatus.Approved, T0.AddMinutes(5))); // delivered late

        Assert.Equal(TemplateStatus.Paused, template.Status);
        Assert.Equal(T0.AddMinutes(10), template.StatusChangedAt);
    }

    [Fact]
    public void Refresh_FromAListReadBeforeTheLastNotification_KeepsTheNotifiedStatus()
    {
        var template = Template();
        template.ChangeStatus(TemplateStatus.Paused, T0.AddMinutes(10));

        template.Refresh(TemplateCategory.Utility, TemplateStatus.Approved, "Hola {{1}}", true, T0.AddMinutes(11), listedAt: T0.AddMinutes(9));
        Assert.Equal(TemplateStatus.Paused, template.Status);

        template.Refresh(TemplateCategory.Utility, TemplateStatus.Approved, "Hola {{1}}", true, T0.AddMinutes(21), listedAt: T0.AddMinutes(20));
        Assert.Equal(TemplateStatus.Approved, template.Status);
    }
}
