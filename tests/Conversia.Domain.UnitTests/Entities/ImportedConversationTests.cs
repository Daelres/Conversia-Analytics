using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>A conversation built from a shared chat history (docs/WEBHOOKS.md, "History import"): past, closed, never mixed with live messages.</summary>
public class ImportedConversationTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Conversation Imported()
        => Conversation.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), T0, ConversationSource.Organic, isImported: true);

    [Fact]
    public void AnImportedConversation_IsClosedFromTheStart_SoNoLiveMessageJoinsIt()
    {
        var conversation = Imported();

        Assert.True(conversation.IsImported);
        Assert.Equal(ConversationStatus.Closed, conversation.Status);
        Assert.Equal(T0, conversation.ClosedAt);
    }

    [Fact]
    public void ALaterChunk_MovesItsCloseToItsLastMessage()
    {
        var conversation = Imported();

        conversation.ApplyTimeline(ConversationTimeline.From([(MessageDirection.Inbound, T0), (MessageDirection.Outbound, T0.AddDays(2))])!);

        Assert.Equal(T0.AddDays(2), conversation.ClosedAt);
    }

    [Fact]
    public void ALiveConversation_IsOpenAndNotImported()
    {
        var conversation = Conversation.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), T0, ConversationSource.Organic);

        Assert.False(conversation.IsImported);
        Assert.Equal(ConversationStatus.Open, conversation.Status);
    }

    [Fact]
    public void AClassificationWithoutTheAi_LeavesNothingPendingForIt()
    {
        var conversation = Imported();

        conversation.RequestClassification(T0, includeLlm: false);

        Assert.True(conversation.ClassificationDirty);
        Assert.False(conversation.LlmPending);
    }

    [Fact]
    public void ByDefault_TheAiIsAskedToo()
    {
        var conversation = Imported();

        conversation.RequestClassification(T0);

        Assert.True(conversation.LlmPending);
    }
}
