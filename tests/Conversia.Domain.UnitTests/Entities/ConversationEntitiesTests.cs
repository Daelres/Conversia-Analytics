using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

public class ConversationEntitiesTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private static (MessageDirection, DateTime) In(int seconds) => (MessageDirection.Inbound, T0.AddSeconds(seconds));

    private static (MessageDirection, DateTime) Out(int seconds) => (MessageDirection.Outbound, T0.AddSeconds(seconds));

    // ---- Contact ----------------------------------------------------------------------

    [Theory]
    [InlineData("573001234567", "573001234567")]
    [InlineData("+573001234567", "573001234567")]
    [InlineData(" 573001234567 ", "573001234567")]
    [InlineData("1234567", "1234567")] // 7 digits
    [InlineData("123456789012345", "123456789012345")] // 15 digits (E.164 maximum)
    [InlineData("123456", null)] // too short
    [InlineData("1234567890123456", null)] // 16 digits
    [InlineData("+57 300 123 4567", null)] // separators are not repaired
    [InlineData("57-300-123-4567", null)]
    [InlineData("CO.13491208655302741918", null)] // business-scoped user id: not a phone number
    [InlineData("US.123456", null)]
    [InlineData("CA.123456", null)]
    [InlineData("57300123456a", null)]
    [InlineData("++573001234567", null)]
    [InlineData("٥٧٣٠٠١٢٣٤٥٦٧", null)] // non-ASCII digits
    [InlineData("", null)]
    public void Contact_NormalizeWaId_IsStrict_NothingIsStrippedOrRepaired(string waId, string? expected)
        => Assert.Equal(expected, Contact.NormalizeWaId(waId));

    [Fact]
    public void Contact_Create_AcceptsAPlusAndKeepsTheName()
    {
        var contact = Contact.Create(Tenant, "+573001234567", "  Laura  ", T0);

        Assert.Equal("573001234567", contact.WaId);
        Assert.Equal("Laura", contact.DisplayName);
        Assert.Equal((T0, T0), (contact.FirstSeenAt, contact.DisplayNameAt));
    }

    [Fact]
    public void Contact_Create_RejectsAnUnusableWaId()
    {
        Assert.Throws<DomainException>(() => Contact.Create(Tenant, "none", null, T0));
        Assert.Throws<DomainException>(() => Contact.Create(Tenant, "CO.13491208655302741918", null, T0));
    }

    [Fact]
    public void Contact_UpdateDisplayName_IgnoresBlankNames_AndNeverLetsAnOlderMessageOverwriteANewerName()
    {
        var contact = Contact.Create(Tenant, "573001234567", "Laura", T0);

        contact.UpdateDisplayName("  ", T0.AddHours(1));
        Assert.Equal("Laura", contact.DisplayName);

        contact.UpdateDisplayName("Laura G.", T0.AddHours(2));
        Assert.Equal("Laura G.", contact.DisplayName);

        contact.UpdateDisplayName("Laura (vieja)", T0.AddHours(1)); // an older delivery processed late
        Assert.Equal("Laura G.", contact.DisplayName);
        Assert.Equal(T0.AddHours(2), contact.DisplayNameAt);

        contact.UpdateDisplayName("Laura H.", T0.AddHours(2)); // the same instant: the latest processed wins
        Assert.Equal("Laura H.", contact.DisplayName);
    }

    [Fact]
    public void Contact_UpdateDisplayName_FillsAMissingNameWhateverTheTime()
    {
        var contact = Contact.Create(Tenant, "573001234567", null, T0);

        contact.UpdateDisplayName("Laura", T0.AddDays(-1));

        Assert.Equal("Laura", contact.DisplayName);
    }

    [Fact]
    public void Dates_MustBeUtc_AndAreKeptToWholeMicroseconds()
    {
        Assert.Throws<DomainException>(() => Contact.Create(Tenant, "573001234567", null, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)));

        var withTicks = new DateTime(T0.Ticks + 7, DateTimeKind.Utc); // 700 ns
        Assert.Equal(T0, Contact.Create(Tenant, "573001234567", null, withTicks).FirstSeenAt);
    }

    // ---- ConversationTimeline: derived from message times, never from arrival order ----------------

    [Fact]
    public void Timeline_OfNoMessages_IsNull()
        => Assert.Null(ConversationTimeline.From([]));

    [Fact]
    public void Timeline_InboundThenOutbound_SetsTheFirstResponse()
    {
        var timeline = ConversationTimeline.From([In(0), Out(90)])!;

        Assert.Equal(T0, timeline.StartedAt);
        Assert.Equal(T0.AddSeconds(90), timeline.LastMessageAt);
        Assert.Equal(T0, timeline.FirstInboundAt);
        Assert.Equal(T0.AddSeconds(90), timeline.FirstResponseAt);
    }

    [Fact]
    public void Timeline_IsTheSameWhateverTheArrivalOrder()
    {
        var inOrder = ConversationTimeline.From([In(0), Out(60), In(120), Out(180)]);
        var reversed = ConversationTimeline.From([Out(180), In(120), Out(60), In(0)]);

        Assert.Equal(inOrder, reversed);
        Assert.Equal(T0.AddSeconds(60), reversed!.FirstResponseAt);
    }

    [Fact]
    public void Timeline_AnOutboundBeforeTheFirstInboundIsNotAResponse()
    {
        var timeline = ConversationTimeline.From([Out(0), In(30), Out(100)])!;

        Assert.Equal(T0, timeline.StartedAt);
        Assert.Equal(T0.AddSeconds(30), timeline.FirstInboundAt);
        Assert.Equal(T0.AddSeconds(100), timeline.FirstResponseAt);
    }

    [Fact]
    public void Timeline_WithoutAnAnswerOrWithoutInbound_HasNoFirstResponse()
    {
        Assert.Null(ConversationTimeline.From([In(0), In(10)])!.FirstResponseAt);
        var outboundOnly = ConversationTimeline.From([Out(0), Out(10)])!;
        Assert.Null(outboundOnly.FirstInboundAt);
        Assert.Null(outboundOnly.FirstResponseAt);
    }

    [Fact]
    public void Timeline_AnOutboundInTheSameSecondAsTheFirstInbound_IsAResponse_BecauseTimestampsHaveOneSecondResolution()
        => Assert.Equal(T0, ConversationTimeline.From([In(0), Out(0), Out(5)])!.FirstResponseAt);

    // ---- Conversation -------------------------------------------------------------------

    private static Conversation NewConversation(ConversationSource source = ConversationSource.Organic, string? reference = null)
        => Conversation.Start(Tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), T0, source, reference);

    [Fact]
    public void Conversation_Start_IsOpenWithTheStartAsLastMessage()
    {
        var conversation = NewConversation();

        Assert.Equal(ConversationStatus.Open, conversation.Status);
        Assert.Equal(T0, conversation.StartedAt);
        Assert.Equal(T0, conversation.LastMessageAt);
        Assert.Null(conversation.FirstInboundAt);
        Assert.Null(conversation.FirstResponseAt);
        Assert.Null(conversation.AdvisorId);
    }

    [Fact]
    public void Conversation_Start_RejectsUndefinedSourceAndOverlongReference()
    {
        Assert.Throws<DomainException>(() => NewConversation((ConversationSource)99));
        Assert.Throws<DomainException>(() => NewConversation(ConversationSource.Ads, new string('x', Conversation.SourceReferenceMaxLength + 1)));
    }

    [Fact]
    public void Conversation_MarkAsAds_UpgradesOnceAndKeepsTheFirstReference()
    {
        var conversation = NewConversation();

        conversation.MarkAsAds("Depilación láser 2x1");
        conversation.MarkAsAds("Other ad");

        Assert.Equal(ConversationSource.Ads, conversation.Source);
        Assert.Equal("Depilación láser 2x1", conversation.SourceReference);
    }

    [Fact]
    public void Conversation_MarkAsAds_FillsAMissingReferenceFromALaterReferral_ButNeverReplacesOne()
    {
        var conversation = NewConversation(ConversationSource.Ads);

        conversation.MarkAsAds("https://fb.me/ad");
        conversation.MarkAsAds("Other ad");

        Assert.Equal("https://fb.me/ad", conversation.SourceReference);
    }

    [Fact]
    public void Conversation_ApplyTimeline_CopiesTheMarkers()
    {
        var conversation = NewConversation();

        conversation.ApplyTimeline(ConversationTimeline.From([In(-10), Out(50)])!);

        Assert.Equal(T0.AddSeconds(-10), conversation.StartedAt);
        Assert.Equal(T0.AddSeconds(50), conversation.LastMessageAt);
        Assert.Equal(T0.AddSeconds(-10), conversation.FirstInboundAt);
        Assert.Equal(T0.AddSeconds(50), conversation.FirstResponseAt);
    }

    // ---- Message ------------------------------------------------------------------------

    private static Message NewMessage(string id = "wamid.ABC", string type = "text", string? text = "hola")
        => Message.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), id, MessageDirection.Inbound, type, text, T0);

    [Fact]
    public void Message_Create_KeepsTheProviderIdAndTextAsReceived()
    {
        var message = NewMessage(text: " hola \n");

        Assert.Equal("wamid.ABC", message.ProviderMessageId);
        Assert.Equal(" hola \n", message.Text);
        Assert.Equal("text", message.Type);
    }

    [Fact]
    public void Message_Create_AllowsNoTextForMedia()
        => Assert.Null(NewMessage(type: "image", text: null).Text);

    [Theory]
    [InlineData("", "text")]
    [InlineData("id", "Text")]
    [InlineData("id", "")]
    [InlineData("id", "has space")]
    public void Message_Create_RejectsABlankIdOrABadType(string id, string type)
        => Assert.Throws<DomainException>(() => NewMessage(id, type));

    [Fact]
    public void Message_Create_RejectsNulOrOverlongText()
    {
        Assert.Throws<DomainException>(() => NewMessage(text: "a\0b"));
        Assert.Throws<DomainException>(() => NewMessage(text: new string('a', Message.TextMaxLength + 1)));
        Assert.NotNull(NewMessage(text: new string('a', Message.TextMaxLength)));
    }

    // ---- Opportunity --------------------------------------------------------------------

    [Fact]
    public void Opportunity_Open_StartsNewAndUnclassified()
    {
        var opportunity = Opportunity.Open(Tenant, Guid.NewGuid());

        Assert.Equal(OpportunityStage.New, opportunity.Stage);
        Assert.Null(opportunity.ServiceInterest);
        Assert.Null(opportunity.ClassifiedAt);
        Assert.Null(opportunity.ClosedAt);
    }

    // ---- WebhookEvent -------------------------------------------------------------------

    private static WebhookEvent NewEvent() => WebhookEvent.Receive(Tenant, Guid.NewGuid(), new byte[32], "{}", T0);

    [Fact]
    public void WebhookEvent_Receive_StartsPendingAndDueNow()
    {
        var webhookEvent = NewEvent();

        Assert.Equal(WebhookEventStatus.Pending, webhookEvent.Status);
        Assert.Equal(0, webhookEvent.Attempts);
        Assert.Equal(T0, webhookEvent.NextAttemptAt);
    }

    [Fact]
    public void WebhookEvent_Receive_RejectsABadHashOrAnEmptyBody()
    {
        Assert.Throws<DomainException>(() => WebhookEvent.Receive(Tenant, Guid.NewGuid(), new byte[5], "{}", T0));
        Assert.Throws<DomainException>(() => WebhookEvent.Receive(Tenant, Guid.NewGuid(), new byte[32], "", T0));
    }

    private static void FailedAttempt(WebhookEvent e, string error, int maxAttempts, TimeSpan baseDelay)
    {
        e.BeginAttempt(T0, TimeSpan.FromMinutes(2));
        e.RegisterFailure(error, T0, maxAttempts, baseDelay);
    }

    [Fact]
    public void WebhookEvent_BeginAttempt_CountsTheAttemptAtOnceAndLeasesTheEvent()
    {
        var webhookEvent = NewEvent();

        webhookEvent.BeginAttempt(T0, TimeSpan.FromMinutes(2));

        Assert.Equal((WebhookEventStatus.Pending, 1), (webhookEvent.Status, webhookEvent.Attempts));
        Assert.Equal(T0.AddMinutes(2), webhookEvent.NextAttemptAt);
    }

    [Fact]
    public void WebhookEvent_RegisterFailure_BacksOffExponentially_AndFailsAtTheLimit()
    {
        var webhookEvent = NewEvent();
        var baseDelay = TimeSpan.FromSeconds(30);

        FailedAttempt(webhookEvent, "boom 1", 3, baseDelay);
        Assert.Equal((WebhookEventStatus.Pending, 1), (webhookEvent.Status, webhookEvent.Attempts));
        Assert.Equal(T0.AddSeconds(30), webhookEvent.NextAttemptAt);

        FailedAttempt(webhookEvent, "boom 2", 3, baseDelay);
        Assert.Equal(T0.AddSeconds(60), webhookEvent.NextAttemptAt);

        FailedAttempt(webhookEvent, "boom 3", 3, baseDelay);
        Assert.Equal((WebhookEventStatus.Failed, 3), (webhookEvent.Status, webhookEvent.Attempts));
        Assert.Equal("boom 3", webhookEvent.LastError);
    }

    [Fact]
    public void WebhookEvent_RegisterFailure_CutsTheErrorToItsLimit()
    {
        var webhookEvent = NewEvent();

        FailedAttempt(webhookEvent, new string('e', WebhookEvent.LastErrorMaxLength + 50), 5, TimeSpan.Zero);

        Assert.Equal(WebhookEvent.LastErrorMaxLength, webhookEvent.LastError!.Length);
    }

    [Fact]
    public void WebhookEvent_Defer_GivesTheAttemptBack_AndNeverGoesBelowZero()
    {
        var webhookEvent = NewEvent();
        webhookEvent.BeginAttempt(T0, TimeSpan.FromMinutes(2));

        webhookEvent.Defer(T0, TimeSpan.FromSeconds(10));
        webhookEvent.Defer(T0, TimeSpan.FromSeconds(10));

        Assert.Equal((0, WebhookEventStatus.Pending), (webhookEvent.Attempts, webhookEvent.Status));
        Assert.Equal(T0.AddSeconds(10), webhookEvent.NextAttemptAt);
    }

    [Fact]
    public void WebhookEvent_Fail_IsFinalAtOnce()
    {
        var webhookEvent = NewEvent();
        webhookEvent.BeginAttempt(T0, TimeSpan.FromMinutes(2));

        webhookEvent.Fail("too big");

        Assert.Equal((WebhookEventStatus.Failed, 1, "too big"), (webhookEvent.Status, webhookEvent.Attempts, webhookEvent.LastError));
    }

    [Fact]
    public void WebhookEvent_Requeue_OnlyForFailedEvents_AndStartsAFreshBudget()
    {
        var webhookEvent = NewEvent();
        Assert.Throws<DomainException>(() => webhookEvent.Requeue(T0));

        webhookEvent.Fail("boom");
        webhookEvent.Requeue(T0.AddHours(1));

        Assert.Equal((WebhookEventStatus.Pending, 0, (string?)null), (webhookEvent.Status, webhookEvent.Attempts, webhookEvent.LastError));
        Assert.Equal(T0.AddHours(1), webhookEvent.NextAttemptAt);
    }

    [Fact]
    public void WebhookEvent_MarkProcessed_ClearsTheErrorAndStampsTheTime()
    {
        var webhookEvent = NewEvent();
        FailedAttempt(webhookEvent, "boom", 5, TimeSpan.Zero);

        webhookEvent.MarkProcessed(T0.AddMinutes(1));

        Assert.Equal(WebhookEventStatus.Processed, webhookEvent.Status);
        Assert.Equal(T0.AddMinutes(1), webhookEvent.ProcessedAt);
        Assert.Null(webhookEvent.LastError);
    }

    [Theory]
    [InlineData(1, 10, 10)]
    [InlineData(2, 10, 20)]
    [InlineData(4, 10, 80)]
    [InlineData(40, 10, 21600)] // capped at six hours
    [InlineData(3, 0, 0)]
    public void BackoffDelay_DoublesPerAttemptUpToTheCap(int failedAttempts, int baseSeconds, int expectedSeconds)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), WebhookEvent.BackoffDelay(failedAttempts, TimeSpan.FromSeconds(baseSeconds)));
}
