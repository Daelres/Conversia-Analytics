using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>The stored copy of a message's media (docs/MESSAGING.md, "Media"): one state per message, moved only forward.</summary>
public class MessageMediaStorageTests
{
    private static readonly DateTime T0 = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly StoredMediaObject Object = new($"{Tenant:N}/2026/11/0123456789abcdef0123456789abcdef", 1234, "image/jpeg");

    private static Message Inbound(MessageMedia? media, bool imported = false, string type = "image")
        => Message.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), "wamid.X", MessageDirection.Inbound, type, null, T0, media: media, imported: imported);

    [Fact]
    public void AMessageWithAMediaId_WaitsForItsDownload()
    {
        var message = Inbound(new MessageMedia("123", "image/jpeg", false));

        Assert.Equal(MediaStatus.Pending, message.MediaStatus);
        Assert.Equal(T0, message.MediaNextAttemptAt);
        Assert.Equal(0, message.MediaAttempts);
        Assert.Null(message.MediaObjectKey);
    }

    [Fact]
    public void AMessageWithoutMedia_HasNoMediaState()
        => Assert.Null(Message.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), "wamid.X", MessageDirection.Inbound, "text", "Hola", T0).MediaStatus);

    [Fact]
    public void MediaWithoutAnId_CanNeverBeDownloaded_AndIsFailedAtOnce()
        => Assert.Equal(MediaStatus.Failed, Inbound(new MessageMedia(null, "image/jpeg", false)).MediaStatus);

    [Fact]
    public void StoringIt_KeepsTheObjectSizeAndType_AndEndsTheRetries()
    {
        var message = Inbound(new MessageMedia("123", "image/jpeg", false));

        message.MarkMediaStored(Object, T0.AddMinutes(1));

        Assert.Equal(MediaStatus.Stored, message.MediaStatus);
        Assert.Equal(Object.ObjectKey, message.MediaObjectKey);
        Assert.Equal(1234, message.MediaSizeBytes);
        Assert.Equal("image/jpeg", message.MediaContentType);
        Assert.Equal(T0.AddMinutes(1), message.MediaStoredAt);
        Assert.Null(message.MediaNextAttemptAt);
    }

    [Fact]
    public void ARetry_CountsTheAttemptAndMovesTheNextOne()
    {
        var message = Inbound(new MessageMedia("123", "image/jpeg", false));

        message.ScheduleMediaRetry(T0.AddMinutes(5));

        Assert.Equal((MediaStatus.Pending, 1, T0.AddMinutes(5)), (message.MediaStatus!.Value, message.MediaAttempts, message.MediaNextAttemptAt!.Value));
    }

    [Fact]
    public void APostponement_MovesTheNextAttempt_WithoutCountingOne()
    {
        var message = Inbound(new MessageMedia("123", "image/jpeg", false));
        message.ScheduleMediaRetry(T0.AddMinutes(10));

        message.PostponeMedia(T0.AddMinutes(30));

        Assert.Equal((1, T0.AddMinutes(30)), (message.MediaAttempts, message.MediaNextAttemptAt!.Value));
    }

    [Fact]
    public void AnAttemptLostToOurOwnStore_IsGivenBack_SoAnOutageNeverUsesUpTheAttempts()
    {
        var message = Inbound(new MessageMedia("123", "image/jpeg", false));
        message.ScheduleMediaRetry(T0.AddMinutes(10));

        message.ReleaseMediaAttempt(T0.AddMinutes(30));

        Assert.Equal((MediaStatus.Pending, 0, T0.AddMinutes(30)), (message.MediaStatus!.Value, message.MediaAttempts, message.MediaNextAttemptAt!.Value));
    }

    [Theory]
    [InlineData(MediaStatus.TooLarge)]
    [InlineData(MediaStatus.Blocked)]
    [InlineData(MediaStatus.Expired)]
    [InlineData(MediaStatus.Failed)]
    public void AFinalRefusal_EndsTheRetries(MediaStatus status)
    {
        var message = Inbound(new MessageMedia("123", "image/jpeg", false));

        message.MarkMediaUnavailable(status);

        Assert.Equal(status, message.MediaStatus);
        Assert.Null(message.MediaNextAttemptAt);
    }

    [Theory]
    [InlineData(MediaStatus.Pending)]
    [InlineData(MediaStatus.Stored)]
    [InlineData(MediaStatus.Purged)]
    public void OnlyARefusalStateIsAccepted_AsUnavailable(MediaStatus status)
        => Assert.Throws<DomainException>(() => Inbound(new MessageMedia("123", "image/jpeg", false)).MarkMediaUnavailable(status));

    [Fact]
    public void AStoredMediaCannotBeStoredAgain_NorRefused()
    {
        var message = Inbound(new MessageMedia("123", "image/jpeg", false));
        message.MarkMediaStored(Object, T0);

        Assert.Throws<DomainException>(() => message.MarkMediaStored(Object, T0));
        Assert.Throws<DomainException>(() => message.MarkMediaUnavailable(MediaStatus.Failed));
        Assert.Throws<DomainException>(() => message.ScheduleMediaRetry(T0));
    }

    [Fact]
    public void PurgingIt_ForgetsTheObject_AndReturnsItsKeyToDelete()
    {
        var message = Inbound(new MessageMedia("123", "image/jpeg", false));
        message.MarkMediaStored(Object, T0);

        var key = message.PurgeMedia();

        Assert.Equal(Object.ObjectKey, key);
        Assert.Equal(MediaStatus.Purged, message.MediaStatus);
        Assert.Null(message.MediaObjectKey);
    }

    [Fact]
    public void OnlyAStoredMediaCanBePurged()
        => Assert.Throws<DomainException>(() => Inbound(new MessageMedia("123", "image/jpeg", false)).PurgeMedia());

    [Fact]
    public void ASendFromConversia_CarriesItsStoredCopyFromTheStart()
    {
        var message = Message.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), "wamid.OUT", MessageDirection.Outbound, "image", "Mira", T0,
            MessageSender.Conversia, media: new MessageMedia("998877", "image/jpeg", true), storedMedia: Object);

        Assert.Equal(MediaStatus.Stored, message.MediaStatus);
        Assert.Equal(Object.ObjectKey, message.MediaObjectKey);
        Assert.Equal(T0, message.MediaStoredAt);
    }

    [Fact]
    public void AStoredCopyWithoutMedia_IsRefused()
        => Assert.Throws<DomainException>(() => Message.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), "wamid.OUT", MessageDirection.Outbound, "text", "x", T0,
            MessageSender.Conversia, storedMedia: Object));

    [Theory]
    [InlineData("", 1, "image/jpeg")]
    [InlineData("../etc/passwd", 1, "image/jpeg")]
    [InlineData("tenant/ok", 0, "image/jpeg")]
    [InlineData("tenant/ok", 1, "not a mime")]
    public void AnInvalidStoredObject_IsRefused(string key, long size, string type)
        => Assert.False(new StoredMediaObject(key, size, type).IsValid);

    [Fact]
    public void AnImportedMessage_IsMarked()
        => Assert.True(Inbound(null, imported: true, type: "text").IsImported);

    [Fact]
    public void AHistoryPlaceholder_IsCompletedByItsFollowUp_AndThenWaitsForTheDownload()
    {
        var placeholder = Inbound(null, imported: true, type: "media_placeholder");

        placeholder.CompleteHistoryPlaceholder("image", new MessageMedia("555", "image/jpeg", true), T0.AddDays(1));

        Assert.Equal("image", placeholder.Type);
        Assert.Equal("555", placeholder.MediaId);
        Assert.Equal(MediaStatus.Pending, placeholder.MediaStatus);
        Assert.Equal(T0.AddDays(1), placeholder.MediaNextAttemptAt);
    }

    [Fact]
    public void OnlyAnImportedPlaceholder_CanBeCompleted()
    {
        Assert.Throws<DomainException>(() => Inbound(null, imported: false, type: "media_placeholder").CompleteHistoryPlaceholder("image", new MessageMedia("1", null, false), T0));
        Assert.Throws<DomainException>(() => Inbound(null, imported: true, type: "text").CompleteHistoryPlaceholder("image", new MessageMedia("1", null, false), T0));
    }

    [Fact]
    public void AnEchoStoredFirst_AdoptsTheCopyOfTheSend_WhileItsOwnIsNotStored()
    {
        var echo = Message.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), "wamid.E", MessageDirection.Outbound, "image", null, T0, MessageSender.PhoneApp,
            media: new MessageMedia("55", "image/jpeg", false));

        Assert.True(echo.AdoptStoredCopy(Object, T0.AddSeconds(1)));

        Assert.Equal((MediaStatus.Stored, Object.ObjectKey), (echo.MediaStatus!.Value, echo.MediaObjectKey));
        Assert.Null(echo.MediaNextAttemptAt);
    }

    [Fact]
    public void AMessageThatAlreadyHasACopy_RefusesAnother()
    {
        var echo = Message.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), "wamid.E", MessageDirection.Outbound, "image", null, T0, MessageSender.PhoneApp,
            media: new MessageMedia("55", "image/jpeg", false));
        echo.MarkMediaStored(Object, T0);

        Assert.False(echo.AdoptStoredCopy(Object with { ObjectKey = Object.ObjectKey + "b" }, T0));
        Assert.Equal(Object.ObjectKey, echo.MediaObjectKey);
    }
}
