using System.Security.Cryptography;
using System.Text;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Services;
using Conversia.Domain.Entities;
using Conversia.Domain.Interfaces;
using Moq;

namespace Conversia.Application.UnitTests.Services;

public class WebhookIngestionServiceTests
{
    private readonly Mock<IWhatsAppChannelRepository> _channels = new();
    private readonly Mock<IWebhookEventRepository> _events = new();
    private readonly Mock<ITenantContext> _tenantContext = new();
    private readonly Guid _tenant = Guid.NewGuid();

    private WebhookIngestionService CreateSut() => new(_channels.Object, _events.Object, _tenantContext.Object, TimeProvider.System);

    private (WhatsAppChannel Channel, string Secret) StoredChannel()
    {
        var (channel, secret) = WhatsAppChannel.Create(_tenant, Guid.NewGuid(), "3001234567");
        _channels.Setup(r => r.GetByChannelKeyAsync(channel.ChannelKey, It.IsAny<CancellationToken>())).ReturnsAsync(channel);
        return (channel, secret);
    }

    // ---- Authenticate: every failure is the same null, and only success sets the tenant ------------

    [Fact]
    public async Task Authenticate_WithTheRightSecret_ReturnsTheChannelAndSetsTheTenant()
    {
        var (channel, secret) = StoredChannel();

        var access = await CreateSut().AuthenticateAsync(channel.ChannelKey, secret);

        Assert.Equal(new WebhookChannelAccess(channel.Id, _tenant), access);
        _tenantContext.Verify(t => t.SetTenant(_tenant), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-secret")]
    public async Task Authenticate_WithAMissingOrWrongSecret_IsNullAndSetsNoTenant(string? secret)
    {
        var (channel, _) = StoredChannel();

        Assert.Null(await CreateSut().AuthenticateAsync(channel.ChannelKey, secret));
        _tenantContext.Verify(t => t.SetTenant(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Authenticate_WithAnInactiveChannel_IsNullEvenWithTheRightSecret()
    {
        var (channel, secret) = StoredChannel();
        channel.Deactivate();

        Assert.Null(await CreateSut().AuthenticateAsync(channel.ChannelKey, secret));
    }

    [Fact]
    public async Task Authenticate_WithAnUnknownOrMalformedKey_IsNullAndMalformedOnesNeverReachTheRepository()
    {
        var sut = CreateSut();

        Assert.Null(await sut.AuthenticateAsync(new string('a', 43), "secret"));
        Assert.Null(await sut.AuthenticateAsync("short", "secret"));
        Assert.Null(await sut.AuthenticateAsync("../../etc/passwd-aaaaaaaaaaaaaaaaaaaaaaaaaaaa", "secret"));

        _channels.Verify(r => r.GetByChannelKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- Store ----------------------------------------------------------------------------------

    private static WebhookChannelAccess Access() => new(Guid.NewGuid(), Guid.NewGuid());

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{\"a\":1} trailing")]
    [InlineData("{\"a\":")]
    public async Task Store_RejectsAnythingThatIsNotOneJsonObject_WithoutTouchingTheRepository(string body)
    {
        var result = await CreateSut().StoreAsync(Access(), Encoding.UTF8.GetBytes(body));

        Assert.Equal(WebhookStoreResult.InvalidPayload, result);
        _events.Verify(r => r.AddIfNewAsync(It.IsAny<WebhookEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Store_HashesTheRawBodyAndKeepsItUntouched()
    {
        var access = Access();
        var body = Encoding.UTF8.GetBytes("{ \"b\":  1,\n \"a\": 2 }");
        WebhookEvent? saved = null;
        _events.Setup(r => r.AddIfNewAsync(It.IsAny<WebhookEvent>(), It.IsAny<CancellationToken>()))
            .Callback<WebhookEvent, CancellationToken>((e, _) => saved = e).ReturnsAsync(true);

        var result = await CreateSut().StoreAsync(access, body);

        Assert.Equal(WebhookStoreResult.Stored, result);
        Assert.Equal(SHA256.HashData(body), saved!.BodyHash);
        Assert.Equal("{ \"b\":  1,\n \"a\": 2 }", saved.RawPayload);
        Assert.Equal((access.TenantId, access.ChannelId), (saved.TenantId, saved.ChannelId));
    }

    [Fact]
    public async Task Store_ADuplicateBody_IsReportedAsDuplicate()
    {
        _events.Setup(r => r.AddIfNewAsync(It.IsAny<WebhookEvent>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        Assert.Equal(WebhookStoreResult.Duplicate, await CreateSut().StoreAsync(Access(), "{}"u8.ToArray()));
    }

    [Fact]
    public async Task Store_APayloadTheDatabaseRefuses_IsReportedAsInvalid()
    {
        _events.Setup(r => r.AddIfNewAsync(It.IsAny<WebhookEvent>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidWebhookPayloadException());

        Assert.Equal(WebhookStoreResult.InvalidPayload, await CreateSut().StoreAsync(Access(), "{}"u8.ToArray()));
    }
}
