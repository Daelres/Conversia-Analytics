using System.Net;
using System.Text.RegularExpressions;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Messaging;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conversia.Infrastructure.UnitTests.Messaging;

/// <summary>
/// Which adapter sends for a channel (docs/SANDBOX.md): the 360dialog one outside sandbox mode, exactly as before the mode existed, and the
/// internal one inside it, whatever the channel has stored. And the internal one itself: no network, no key, an id that says what it is.
/// </summary>
public class WhatsAppMessageSenderResolverTests
{
    private const string Recipient = "000001234567890";

    private readonly CountingHandler _wire = new();
    private readonly ForbiddenProtector _protector = new();
    private readonly SandboxMessageSender _sandbox = new();
    private readonly Dialog360MessageSender _dialog360;
    private readonly WhatsAppMessageSenderResolver _resolver;

    public WhatsAppMessageSenderResolverTests()
    {
        _dialog360 = new Dialog360MessageSender(new HttpClient(_wire), _protector, Dialog360ApiOptions.Default, NullLogger<Dialog360MessageSender>.Instance);
        _resolver = new WhatsAppMessageSenderResolver(_dialog360, _sandbox);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        }
    }

    /// <summary>A protector nobody may ask anything: reading a stored key is already too much for the internal sender.</summary>
    private sealed class ForbiddenProtector : ISecretProtector
    {
        public int Reads { get; private set; }

        public bool IsConfigured => true;

        public string Protect(string plaintext, string purpose) => throw new InvalidOperationException("Nothing is protected in these tests.");

        public string Unprotect(string protectedValue, string purpose)
        {
            Reads++;
            throw new SecretProtectionException(SecretProtectionFailure.Unreadable);
        }
    }

    private static WhatsAppChannel Channel(WhatsAppProviderEnvironment environment, bool withKey)
    {
        var (channel, _) = WhatsAppChannel.Create(Guid.NewGuid(), Guid.NewGuid(), "+57 300 555 0100");
        channel.Activate();
        if (withKey)
        {
            channel.SetProviderCredentials("v1.1.stored-ciphertext", environment, DateTime.UtcNow);
        }
        else
        {
            channel.SetProviderEnvironment(environment);
        }

        return channel;
    }

    public static TheoryData<WhatsAppProviderEnvironment, bool> Channels => new()
    {
        { WhatsAppProviderEnvironment.Sandbox, true },
        { WhatsAppProviderEnvironment.Sandbox, false },
        { WhatsAppProviderEnvironment.Production, true },
        { WhatsAppProviderEnvironment.Production, false },
    };

    [Theory]
    [MemberData(nameof(Channels))]
    public void OutsideSandboxMode_TheSenderIsThe360DialogAdapter_WithOrWithoutAStoredKey(WhatsAppProviderEnvironment environment, bool withKey)
        => Assert.Same(_dialog360, _resolver.Resolve(Channel(environment, withKey), sandboxMode: false));

    [Theory]
    [MemberData(nameof(Channels))]
    public void InSandboxMode_TheSenderIsTheInternalOne_WithOrWithoutAStoredKey(WhatsAppProviderEnvironment environment, bool withKey)
        => Assert.Same(_sandbox, _resolver.Resolve(Channel(environment, withKey), sandboxMode: true));

    [Fact]
    public void OutsideSandboxMode_AnEnvironmentNobodySendsFor_IsStillAnError()
    {
        var channel = Channel(WhatsAppProviderEnvironment.Sandbox, withKey: false);
        typeof(WhatsAppChannel).GetProperty(nameof(WhatsAppChannel.ProviderEnvironment))!.SetValue(channel, (WhatsAppProviderEnvironment)99);

        Assert.Throws<InvalidOperationException>(() => _resolver.Resolve(channel, sandboxMode: false));
        Assert.Same(_sandbox, _resolver.Resolve(channel, sandboxMode: true));
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task InSandboxMode_SendingMakesNoNetworkCall_AndNeverReadsTheStoredKey(WhatsAppProviderEnvironment environment, bool withKey)
    {
        var channel = Channel(environment, withKey);

        var result = await _resolver.Resolve(channel, sandboxMode: true).SendTextAsync(new WhatsAppTextMessage(channel, Recipient, "Hola"));

        Assert.Equal(WhatsAppSendStatus.Sent, result.Status);
        Assert.Equal(0, _wire.Requests);
        Assert.Equal(0, _protector.Reads);
    }

    [Fact]
    public async Task OutsideSandboxMode_TheSameChannelDoesGoThroughTheProvidersAdapter()
    {
        // The control of the test above: with the mode off, the adapter is asked for the key (and here fails to read it).
        var channel = Channel(WhatsAppProviderEnvironment.Sandbox, withKey: true);

        await Assert.ThrowsAsync<SecretProtectionException>(
            () => _resolver.Resolve(channel, sandboxMode: false).SendTextAsync(new WhatsAppTextMessage(channel, Recipient, "Hola")));

        Assert.Equal(1, _protector.Reads);
    }

    [Fact]
    public async Task TheInternalSender_GivesEachMessageItsOwnId_MarkedAsSandbox_ThatFitsTheColumn()
    {
        var channel = Channel(WhatsAppProviderEnvironment.Sandbox, withKey: false);
        var message = new WhatsAppTextMessage(channel, Recipient, "Hola");

        var ids = new List<string>();
        for (var i = 0; i < 50; i++)
        {
            var result = await _sandbox.SendTextAsync(message);
            Assert.Equal(WhatsAppSendStatus.Sent, result.Status);
            ids.Add(result.ProviderMessageId!);
        }

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Matches(new Regex("^sandbox\\.out\\.[0-9a-f]{32}$"), id));
        Assert.All(ids, id => Assert.True(id.Length <= Message.ProviderMessageIdMaxLength));
        Assert.All(ids, id => Assert.DoesNotContain("wamid", id));
    }

    [Fact]
    public async Task TheInternalSender_AnswersEvenWhenTheCallerIsAlreadyCancelled_BecauseItWaitsForNothing()
    {
        var channel = Channel(WhatsAppProviderEnvironment.Production, withKey: true);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await _sandbox.SendTextAsync(new WhatsAppTextMessage(channel, Recipient, "Hola"), cancelled.Token);

        Assert.Equal(WhatsAppSendStatus.Sent, result.Status);
    }
}
