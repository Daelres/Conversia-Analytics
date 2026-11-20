using Conversia.Application.DTOs.Conversations;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Application.UnitTests.DTOs;

/// <summary>What a tenant user is told about a message's media (docs/MESSAGING.md, "Media"): its state and type, never the provider's id nor the store's key.</summary>
public class MessageAttachmentDtoTests
{
    private static readonly DateTime T0 = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tenant = Guid.NewGuid();

    [Fact]
    public void AStoredImage_IsDescribedByStateTypeAndSize()
    {
        var message = Message.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), "wamid.1", MessageDirection.Inbound, "image", null, T0, media: new MessageMedia("1", "image/jpeg", false));
        message.MarkMediaStored(new StoredMediaObject($"{Tenant:N}/2026/11/aa", 2048, "image/png"), T0);

        var attachment = MessageResponse.From(message).Attachment;

        Assert.NotNull(attachment);
        Assert.Equal(("stored", "image/png", 2048L, "image"), (attachment.Status, attachment.ContentType, attachment.SizeBytes!.Value, attachment.Kind));
    }

    [Fact]
    public void APendingFile_HasNoTypeYet()
    {
        var message = Message.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), "wamid.1", MessageDirection.Inbound, "document", null, T0, media: new MessageMedia("1", "application/pdf", false));

        var attachment = MessageResponse.From(message).Attachment!;

        Assert.Equal(("pending", null, "document"), (attachment.Status, attachment.ContentType, attachment.Kind));
    }

    [Theory]
    [InlineData(MediaStatus.TooLarge, "too_large")]
    [InlineData(MediaStatus.Blocked, "blocked")]
    [InlineData(MediaStatus.Expired, "expired")]
    [InlineData(MediaStatus.Failed, "failed")]
    public void EveryRefusal_HasItsOwnState(MediaStatus status, string expected)
    {
        var message = Message.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), "wamid.1", MessageDirection.Inbound, "video", null, T0, media: new MessageMedia("1", "video/mp4", false));
        message.MarkMediaUnavailable(status);

        Assert.Equal(expected, MessageResponse.From(message).Attachment!.Status);
    }

    [Fact]
    public void AMessageWithoutMedia_HasNoAttachment_AndAnImportedOneSaysSo()
    {
        var message = Message.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), "wamid.1", MessageDirection.Inbound, "text", "Hola", T0, imported: true);

        var response = MessageResponse.From(message);

        Assert.Null(response.Attachment);
        Assert.True(response.Imported);
    }
}
