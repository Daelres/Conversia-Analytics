using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>What a message records for later analysis (docs/decisiones.md): who sent it and the media metadata, never the file.</summary>
public class MessageCaptureTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    private static Message Create(MessageDirection direction, MessageSender? sender = null, Guid? user = null, Guid? advisor = null, MessageMedia? media = null)
        => Message.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), "wamid.1", direction, "text", "hola", T0, sender, user, advisor, media);

    [Fact]
    public void WithoutASender_TheDirectionDecides()
    {
        Assert.Equal(MessageSender.Customer, Create(MessageDirection.Inbound).Sender);
        Assert.Equal(MessageSender.Unknown, Create(MessageDirection.Outbound).Sender);
    }

    [Fact]
    public void AnOutboundMessage_KeepsItsSender_User_AndAdvisor()
    {
        var user = Guid.NewGuid();
        var advisor = Guid.NewGuid();

        var message = Create(MessageDirection.Outbound, MessageSender.Conversia, user, advisor);

        Assert.Equal((MessageSender.Conversia, (Guid?)user, (Guid?)advisor), (message.Sender, message.SentByUserId, message.SentByAdvisorId));
    }

    [Theory]
    [InlineData(MessageDirection.Inbound, MessageSender.PhoneApp)]
    [InlineData(MessageDirection.Inbound, MessageSender.Conversia)]
    [InlineData(MessageDirection.Outbound, MessageSender.Customer)]
    [InlineData(MessageDirection.Outbound, (MessageSender)42)]
    public void ASenderThatContradictsTheDirection_IsRefused(MessageDirection direction, MessageSender sender)
        => Assert.Throws<DomainException>(() => Create(direction, sender));

    [Fact]
    public void AnInboundMessage_CarriesNoUserNorAdvisor()
    {
        Assert.Throws<DomainException>(() => Create(MessageDirection.Inbound, user: Guid.NewGuid()));
        Assert.Throws<DomainException>(() => Create(MessageDirection.Inbound, advisor: Guid.NewGuid()));
        Assert.Throws<DomainException>(() => Create(MessageDirection.Outbound, MessageSender.Conversia, Guid.Empty));
    }

    [Fact]
    public void AnEcho_KeepsTheAssignedAdvisorAsContext_NeverAsItsAuthor()
    {
        var assigned = Guid.NewGuid();

        var echo = Message.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), "wamid.E", MessageDirection.Outbound, "text", "hola", T0, MessageSender.PhoneApp, assignedAdvisorIdAtReceipt: assigned);

        Assert.Equal(((Guid?)null, (Guid?)null, (Guid?)assigned), (echo.SentByUserId, echo.SentByAdvisorId, echo.AssignedAdvisorIdAtReceipt));
        Assert.Throws<DomainException>(() => Create(MessageDirection.Outbound, MessageSender.PhoneApp, advisor: Guid.NewGuid())); // not a fact
        Assert.Throws<DomainException>(() => Message.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), "wamid.C", MessageDirection.Outbound, "text", "hola", T0, MessageSender.Conversia, assignedAdvisorIdAtReceipt: assigned));
        Assert.Throws<DomainException>(() => Message.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), "wamid.I", MessageDirection.Inbound, "text", "hola", T0, assignedAdvisorIdAtReceipt: assigned));
    }

    [Fact]
    public void AnEchoStoredFirst_IsClaimedByTheSendPath()
    {
        var message = Message.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), "wamid.1", MessageDirection.Outbound, "text", "hola", T0, MessageSender.PhoneApp, assignedAdvisorIdAtReceipt: Guid.NewGuid());
        var user = Guid.NewGuid();

        message.AttributeToConversia(user, null);

        Assert.Equal((MessageSender.Conversia, (Guid?)user, (Guid?)null), (message.Sender, message.SentByUserId, message.SentByAdvisorId));
        Assert.Throws<DomainException>(() => Create(MessageDirection.Inbound).AttributeToConversia(user, null));
    }

    [Fact]
    public void MediaMetadata_IsStored_WithoutTheFile()
    {
        var message = Create(MessageDirection.Inbound, media: new MessageMedia("1003383421387256", "image/jpeg", HasCaption: true));

        Assert.Equal(("1003383421387256", "image/jpeg", true), (message.MediaId, message.MediaMimeType, message.HasCaption));
        Assert.Equal((null, null, false), (Create(MessageDirection.Inbound).MediaId, Create(MessageDirection.Inbound).MediaMimeType, Create(MessageDirection.Inbound).HasCaption));
    }

    [Theory]
    [InlineData("Image/JPEG")]
    [InlineData("imagejpeg")]
    [InlineData("/jpeg")]
    [InlineData("image/")]
    [InlineData("image/jp eg")]
    public void AMalformedMimeType_IsRefused(string mime)
        => Assert.Throws<DomainException>(() => Create(MessageDirection.Inbound, media: new MessageMedia("1", mime, false)));

    [Fact]
    public void AnOverlongMediaId_IsRefused()
        => Assert.Throws<DomainException>(() => Create(MessageDirection.Inbound, media: new MessageMedia(new string('1', MessageMedia.MaxIdLength + 1), null, false)));
}
