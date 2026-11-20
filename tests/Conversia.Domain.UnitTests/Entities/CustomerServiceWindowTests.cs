using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>WhatsApp's customer service window: 24 hours from the customer's last message (docs/MESSAGING.md, "Customer service window").</summary>
public class CustomerServiceWindowTests
{
    private static readonly DateTime T0 = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoCustomerMessage_TheWindowIsClosedAndHasNoEnd()
    {
        Assert.False(CustomerServiceWindow.IsOpen(null, T0));
        Assert.Null(CustomerServiceWindow.ClosesAt(null));
    }

    [Fact]
    public void TheWindowClosesExactly24HoursAfterTheCustomersLastMessage()
    {
        Assert.Equal(T0.AddHours(24), CustomerServiceWindow.ClosesAt(T0));
        Assert.True(CustomerServiceWindow.IsOpen(T0, T0));
        Assert.True(CustomerServiceWindow.IsOpen(T0, T0.AddHours(24).AddTicks(-10)));
        Assert.False(CustomerServiceWindow.IsOpen(T0, T0.AddHours(24))); // the closing instant itself is outside
        Assert.False(CustomerServiceWindow.IsOpen(T0, T0.AddDays(3)));
    }

    [Fact]
    public void ClosingSoon_IsTheLastTwoHoursOfAnOpenWindow()
    {
        Assert.False(CustomerServiceWindow.IsClosingSoon(T0, T0.AddHours(21)));
        Assert.True(CustomerServiceWindow.IsClosingSoon(T0, T0.AddHours(22)));
        Assert.True(CustomerServiceWindow.IsClosingSoon(T0, T0.AddHours(23).AddMinutes(59)));
        Assert.False(CustomerServiceWindow.IsClosingSoon(T0, T0.AddHours(24))); // closed, not closing
        Assert.False(CustomerServiceWindow.IsClosingSoon(null, T0));
    }

    [Fact]
    public void Timeline_KeepsTheLastMessageOfEachSide()
    {
        var timeline = ConversationTimeline.From(
        [
            (MessageDirection.Inbound, T0),
            (MessageDirection.Outbound, T0.AddMinutes(5)),
            (MessageDirection.Inbound, T0.AddHours(2)), // the customer wrote again: the window starts over from here
            (MessageDirection.Outbound, T0.AddMinutes(1)),
        ])!;

        Assert.Equal(T0.AddHours(2), timeline.LastInboundAt);
        Assert.Equal(T0.AddMinutes(5), timeline.LastOutboundAt);
    }

    [Fact]
    public void Timeline_WithoutCustomerMessages_HasNoLastInbound()
    {
        var timeline = ConversationTimeline.From([(MessageDirection.Outbound, T0)])!;

        Assert.Null(timeline.LastInboundAt);
        Assert.Equal(T0, timeline.LastOutboundAt);
    }

    [Fact]
    public void Conversation_AppliesTheLastMessagesOfEachSide_AndKnowsWhetherItAwaitsAReply()
    {
        var conversation = Conversation.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), T0, ConversationSource.Organic);

        conversation.ApplyTimeline(ConversationTimeline.From([(MessageDirection.Inbound, T0)])!);
        Assert.Equal(T0, conversation.LastInboundAt);
        Assert.Null(conversation.LastOutboundAt);
        Assert.True(conversation.AwaitingReply);

        // A reply in the same second counts as a reply (timestamps have a resolution of one second).
        conversation.ApplyTimeline(ConversationTimeline.From([(MessageDirection.Inbound, T0), (MessageDirection.Outbound, T0)])!);
        Assert.False(conversation.AwaitingReply);

        conversation.ApplyTimeline(ConversationTimeline.From(
            [(MessageDirection.Inbound, T0), (MessageDirection.Outbound, T0.AddMinutes(1)), (MessageDirection.Inbound, T0.AddMinutes(2))])!);
        Assert.True(conversation.AwaitingReply);
        Assert.Equal(T0.AddMinutes(2), conversation.LastInboundAt);
    }

    [Fact]
    public void Conversation_WithOnlyBusinessMessages_DoesNotAwaitAReply()
    {
        var conversation = Conversation.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), T0, ConversationSource.Organic);
        conversation.ApplyTimeline(ConversationTimeline.From([(MessageDirection.Outbound, T0)])!);

        Assert.False(conversation.AwaitingReply);
    }

    [Fact]
    public void ReadReceipt_IsNeededOnlyForCustomerMessagesNewerThanTheLastOneMarked()
    {
        var conversation = Conversation.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), T0, ConversationSource.Organic);
        Assert.False(conversation.NeedsReadReceipt);

        conversation.ApplyTimeline(ConversationTimeline.From([(MessageDirection.Inbound, T0)])!);
        Assert.True(conversation.NeedsReadReceipt);

        conversation.MarkCustomerMessagesRead(T0);
        Assert.False(conversation.NeedsReadReceipt);
        Assert.Equal(T0, conversation.CustomerReadMarkedAt);

        conversation.ApplyTimeline(ConversationTimeline.From([(MessageDirection.Inbound, T0), (MessageDirection.Inbound, T0.AddSeconds(1))])!);
        Assert.True(conversation.NeedsReadReceipt);
    }
}
