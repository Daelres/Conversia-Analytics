using Conversia.Application.Ingestion;
using Conversia.Application.Sandbox;
using Conversia.Domain.Entities;

namespace Conversia.Application.UnitTests.Media;

/// <summary>The simulated customer of the internal sandbox can send a synthetic image or document (docs/SANDBOX.md): the same shape the parser reads.</summary>
public class SandboxMediaPayloadTests
{
    private static readonly WhatsAppChannel Channel = WhatsAppChannel.Create(Guid.NewGuid(), Guid.NewGuid(), "+57 300 555 0100").Channel;

    [Theory]
    [InlineData("image", "image/png")]
    [InlineData("document", "application/pdf")]
    public void ASyntheticAttachment_ParsesAsAMediaMessageWithASandboxMediaId(string kind, string mime)
    {
        var body = SandboxWebhookPayload.InboundMedia(Channel, "573009990001", "Cliente de prueba", SandboxIds.NewInboundMessageId(), kind, "Mira", DateTime.UtcNow);

        var message = Assert.Single(Dialog360PayloadParser.Parse(body).Messages);
        Assert.Equal(kind, message.Type);
        Assert.Equal("Mira", message.Text);
        Assert.Equal(mime, message.Media!.MimeType);
        Assert.StartsWith(SandboxIds.MediaIdPrefix, message.Media.ProviderMediaId);
    }

    [Fact]
    public void AnUnknownKind_IsRefused()
        => Assert.Throws<ArgumentException>(() => SandboxWebhookPayload.InboundMedia(Channel, "573009990001", "x", "sandbox.in.1", "video", null, DateTime.UtcNow));
}
