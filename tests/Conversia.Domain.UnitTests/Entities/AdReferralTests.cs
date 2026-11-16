using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>The Click-to-WhatsApp referral of a conversation (docs/WEBHOOKS.md, "Ad referral"): the ad's data, kept once.</summary>
public class AdReferralTests
{
    private static readonly DateTime T0 = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    private static Conversation Start(ConversationSource source, AdReferral? referral)
        => Conversation.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), T0, source, "Chat with us", referral: referral);

    private static readonly AdReferral Ad = AdReferral.Create("ad", "120226305854810726", "https://fb.me/3cr4Wqqkv", "Chat with us")!;

    [Fact]
    public void AConversationStartedByAnAd_KeepsItsReferral()
    {
        var conversation = Start(ConversationSource.Ads, Ad);

        Assert.Equal(("ad", "120226305854810726", "https://fb.me/3cr4Wqqkv", "Chat with us"),
            (conversation.ReferralSourceType, conversation.ReferralSourceId, conversation.ReferralSourceUrl, conversation.ReferralHeadline));
    }

    [Fact]
    public void AnOrganicConversation_HasNoReferral()
        => Assert.Null(Start(ConversationSource.Organic, Ad).ReferralSourceId);

    [Fact]
    public void ALaterReferral_MarksTheConversation_ButTheFirstOneIsKept()
    {
        var conversation = Start(ConversationSource.Organic, null);

        conversation.MarkAsAds("Chat with us", Ad);
        conversation.MarkAsAds("Otro", AdReferral.Create("post", "999", null, "Otro"));

        Assert.Equal((ConversationSource.Ads, "ad", "120226305854810726"), (conversation.Source, conversation.ReferralSourceType, conversation.ReferralSourceId));
    }

    [Fact]
    public void Create_CleansAndCutsEachValue_AndDropsAnOddType()
    {
        var referral = AdReferral.Create(" AD ", "  ", "https://fb.me/" + new string('u', 600), "Hola\0")!;

        Assert.Equal(("ad", (string?)null, AdReferral.SourceUrlMaxLength, "Hola"), (referral.SourceType, referral.SourceId, referral.SourceUrl!.Length, referral.Headline));
        Assert.Null(AdReferral.Create("ad<script>", null, null, "x")!.SourceType);
        Assert.Null(AdReferral.Create(null, " ", "", null));
    }
}
