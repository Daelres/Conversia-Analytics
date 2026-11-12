using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>The sandbox mark of the data the internal sandbox creates: set at creation, never changed afterwards.</summary>
public class SandboxDataTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EverythingIsRealData_UnlessItIsCreatedAsSandbox()
    {
        var tenant = Guid.NewGuid();
        var contact = Contact.Create(tenant, "573005550101", "Laura", Now);
        var conversation = Conversation.Start(tenant, contact.Id, Guid.NewGuid(), Guid.NewGuid(), Now, ConversationSource.Organic);
        var webhookEvent = WebhookEvent.Receive(tenant, Guid.NewGuid(), new byte[32], "{}", Now);

        Assert.False(contact.IsSandbox);
        Assert.False(conversation.IsSandbox);
        Assert.False(webhookEvent.IsSandbox);
    }

    [Fact]
    public void TheSandboxMarkIsSetAtCreation()
    {
        var tenant = Guid.NewGuid();
        var contact = Contact.Create(tenant, Contact.NewSandboxWaId(new Random(1)), "Laura", Now, isSandbox: true);
        var conversation = Conversation.Start(tenant, contact.Id, Guid.NewGuid(), Guid.NewGuid(), Now, ConversationSource.Organic, isSandbox: true);
        var webhookEvent = WebhookEvent.Receive(tenant, Guid.NewGuid(), new byte[32], "{}", Now, isSandbox: true);

        Assert.True(contact.IsSandbox);
        Assert.True(conversation.IsSandbox);
        Assert.True(webhookEvent.IsSandbox);
    }

    [Fact]
    public void ASandboxNumber_StartsWithZeros_SoItCanNeverBeARealSubscriber_AndIsAValidWaId()
    {
        var random = new Random(7);
        for (var i = 0; i < 200; i++)
        {
            var waId = Contact.NewSandboxWaId(random);

            Assert.StartsWith(Contact.SandboxWaIdPrefix, waId);
            Assert.Equal(Contact.MaxWaIdLength, waId.Length);
            Assert.Equal(waId, Contact.NormalizeWaId(waId));
            Assert.True(Contact.IsSandboxWaId(waId));
        }

        // E.164 country codes never begin with 0.
        Assert.StartsWith("0", Contact.SandboxWaIdPrefix);
        Assert.False(Contact.IsSandboxWaId("573005550101"));
    }
}
