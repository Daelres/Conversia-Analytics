using Conversia.Application.Classification;
using Conversia.Domain.Enums;
using Conversia.WebhookSimulator.Generation;

namespace Conversia.WebhookSimulator.UnitTests;

/// <summary>The simulator's chats are only useful if the product reads them the way they were written: these tests run the real rule-based classifier over thousands of them.</summary>
public class ChatQualityTests
{
    private static readonly RuleBasedConversationClassifier Classifier = RuleBasedConversationClassifier.CreateDefault();

    private static ClassificationResult Classify(ChatScript chat)
    {
        var start = new DateTime(2026, 6, 1, 15, 0, 0, DateTimeKind.Utc);
        var messages = chat.Messages
            .Select(m => new SnapshotMessage(m.Speaker == Speaker.Customer ? MessageDirection.Inbound : MessageDirection.Outbound, "text", m.Text, start.AddSeconds(m.OffsetSeconds)))
            .ToList();
        var firstInbound = messages.First(m => m.Direction == MessageDirection.Inbound).SentAt;
        var firstResponse = messages.FirstOrDefault(m => m.Direction == MessageDirection.Outbound)?.SentAt;
        return Classifier.Classify(new ConversationSnapshot(Guid.NewGuid(), "Centro", start, firstInbound, firstResponse, messages));
    }

    private static ChatScript Chat(ChatKind kind, int seed, bool fromAd = false)
    {
        var random = new Random(seed);
        return ChatFactory.Create(kind, random, new ChatContext(Customers.FirstName(random), "Centro", 300, fromAd));
    }

    public static TheoryData<ChatKind> Kinds()
    {
        var data = new TheoryData<ChatKind>();
        foreach (var kind in Enum.GetValues<ChatKind>())
        {
            data.Add(kind);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_kind_of_chat_is_read_the_way_it_was_written(ChatKind kind)
    {
        for (var seed = 0; seed < 400; seed++)
        {
            var chat = Chat(kind, seed, fromAd: seed % 3 == 0);
            var result = Classify(chat);

            var expected = chat.Expected switch
            {
                ExpectedOutcome.Won => ClassificationOutcome.Won,
                ExpectedOutcome.Lost => ClassificationOutcome.Lost,
                _ => ClassificationOutcome.InFollowUp,
            };
            Assert.True(expected == result.Outcome, $"{kind} seed {seed} was read as {result.Outcome}, expected {expected}: {string.Join(" | ", chat.Messages.Select(m => $"{m.Speaker.ToString()[..1]}: {m.Text}"))}");
        }
    }

    [Fact]
    public void A_lost_sale_is_lost_for_the_reason_the_chat_gives()
    {
        for (var seed = 0; seed < 200; seed++)
        {
            Assert.Equal(LossReason.Price, Classify(Chat(ChatKind.LostPrice, seed)).LossReason);
            Assert.Equal(LossReason.Competitor, Classify(Chat(ChatKind.LostCompetitor, seed)).LossReason);
            Assert.Equal(LossReason.NotInterested, Classify(Chat(ChatKind.LostNotInterested, seed)).LossReason);
        }
    }

    [Fact]
    public void The_objections_of_the_follow_ups_are_the_ones_named()
    {
        for (var seed = 0; seed < 200; seed++)
        {
            Assert.Equal(ObjectionType.Timing, Classify(Chat(ChatKind.FollowUpThinking, seed)).Objection);
            Assert.Equal(ObjectionType.Pain, Classify(Chat(ChatKind.FollowUpPain, seed)).Objection);
            Assert.Equal(ObjectionType.Price, Classify(Chat(ChatKind.BookedAfterPriceObjection, seed)).Objection);
        }
    }

    [Fact]
    public void A_booking_always_carries_a_message_of_the_advisor_because_the_customers_words_alone_never_win()
    {
        for (var seed = 0; seed < 200; seed++)
        {
            foreach (var kind in new[] { ChatKind.Booked, ChatKind.BookedAfterPriceObjection, ChatKind.BookedByCustomerYes })
            {
                var chat = Chat(kind, seed);

                Assert.True(chat.HasBusinessReply);
                Assert.Equal(ExpectedOutcome.Won, chat.Expected);
                var customerOnly = chat with { Messages = chat.Messages.Where(m => m.Speaker == Speaker.Customer).ToList() };
                Assert.NotEqual(ClassificationOutcome.Won, Classify(customerOnly).Outcome);
            }
        }
    }

    [Fact]
    public void An_unanswered_chat_has_only_the_customer_and_the_zone_is_read()
    {
        for (var seed = 0; seed < 100; seed++)
        {
            var chat = Chat(ChatKind.Unanswered, seed);

            Assert.False(chat.HasBusinessReply);
            Assert.All(chat.Messages, m => Assert.Equal(Speaker.Customer, m.Speaker));
            Assert.NotNull(Classify(chat).ServiceInterest);
        }
    }

    [Fact]
    public void The_chats_are_varied_not_one_sentence_repeated()
    {
        var openers = Enumerable.Range(0, 300).Select(seed => Chat(ChatKind.FollowUpSilent, seed).Messages[0].Text).Distinct().ToList();
        var zones = Enumerable.Range(0, 300).Select(seed => Chat(ChatKind.Booked, seed).Zone).Distinct().ToList();

        Assert.True(openers.Count >= 40, $"Only {openers.Count} different openers.");
        Assert.True(zones.Count >= 8, $"Only {zones.Count} different zones.");
    }

    [Fact]
    public void Offsets_grow_and_the_first_message_is_at_zero()
    {
        foreach (var kind in Enum.GetValues<ChatKind>())
        {
            var chat = Chat(kind, 5);

            Assert.Equal(0, chat.Messages[0].OffsetSeconds);
            Assert.Equal(chat.Messages.Select(m => m.OffsetSeconds).OrderBy(x => x), chat.Messages.Select(m => m.OffsetSeconds));
            Assert.Equal(Speaker.Customer, chat.Messages[0].Speaker);
        }
    }

    [Fact]
    public void The_texts_are_spanish_with_no_markup_and_within_the_length_the_product_reads()
    {
        foreach (var kind in Enum.GetValues<ChatKind>())
        {
            foreach (var message in Chat(kind, 11).Messages)
            {
                Assert.InRange(message.Text.Length, 5, 600);
                Assert.DoesNotContain('<', message.Text);
                Assert.DoesNotContain('\0', message.Text);
            }
        }
    }
}
