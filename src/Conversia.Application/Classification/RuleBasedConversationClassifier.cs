using Conversia.Domain.Enums;

namespace Conversia.Application.Classification;

/// <summary>
/// Reads a Spanish sales conversation with the dictionaries of a <see cref="ClassificationRuleSet"/>: no model, no network, no state, so the
/// same snapshot always gives the same result. It is a stopgap until an LLM adapter implements <see cref="IConversationClassifier"/> (see
/// docs/CLASSIFICATION.md), tuned to be conservative: when the text is ambiguous the answer is "in follow-up" or "undetermined", never a
/// confident Won.
/// <list type="bullet">
/// <item>Text is normalized (case, accents, punctuation, emoji, stretched letters) and matched as phrases of words.</item>
/// <item>A mention that is negated within its clause ("no me parece caro", "no quiero agendar") does not count.</item>
/// <item><b>Won</b> needs the customer's intent to book <i>and</i> a confirmation: the business says the booking is made, or the customer
/// confirms after having shown intent or after a proposal. A bare "dale" counts as accepting only right after a booking proposal.</item>
/// <item><b>Lost</b> is the customer's last stance being a refusal or a cancellation (a later request to book undoes it). The loss reason
/// comes from the objection raised with, or just before, the refusal.</item>
/// <item>Otherwise <b>InFollowUp</b> when the customer said something about the service, and <b>Undetermined</b> when nothing can be read.</item>
/// </list>
/// </summary>
public sealed class RuleBasedConversationClassifier : IConversationClassifier
{
    private const int MaxServices = 5;

    private readonly IReadOnlyDictionary<string, string> _aliases;
    private readonly IReadOnlySet<string> _clauseBreakers;
    private readonly IReadOnlySet<string> _negators;
    private readonly int _negationWindow;

    private readonly (string Code, bool Exclusive, PhraseSet Phrases)[] _services;
    private readonly (string Code, PhraseSet Phrases)[] _generalServices;
    private readonly (string Code, PhraseSet Phrases)[] _reasons;
    private readonly (ObjectionType Type, PhraseSet Phrases)[] _objections;
    private readonly PhraseSet _competitor;
    private readonly PhraseSet _inquiry;
    private readonly PhraseSet _bookingIntent;
    private readonly PhraseSet _strongConfirmation;
    private readonly PhraseSet _weakAck;
    private readonly PhraseSet _proposal;
    private readonly PhraseSet _advisorConfirmation;
    private readonly PhraseSet _refusal;
    private readonly PhraseSet _cancellation;

    public RuleBasedConversationClassifier(ClassificationRuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();

        _aliases = rules.Aliases.ToDictionary(a => TextNormalizer.Normalize(a.Key), a => TextNormalizer.Normalize(a.Value), StringComparer.Ordinal);
        _clauseBreakers = rules.ClauseBreakers.Select(TextNormalizer.Normalize).ToHashSet(StringComparer.Ordinal);
        _negators = rules.Negators.Select(TextNormalizer.Normalize).ToHashSet(StringComparer.Ordinal);
        _negationWindow = rules.NegationWindow;

        _services = [.. rules.Services.Select(s => (s.Code!, s.Exclusive, new PhraseSet(s.Phrases)))];
        _generalServices = [.. rules.GeneralServices.Select(s => (s.Code!, new PhraseSet(s.Phrases)))];
        _reasons = [.. rules.InterestReasons.Select(s => (s.Code!, new PhraseSet(s.Phrases)))];
        _objections =
        [
            (ObjectionType.Price, new PhraseSet(rules.Objections.Price)),
            (ObjectionType.Timing, new PhraseSet(rules.Objections.Timing)),
            (ObjectionType.Location, new PhraseSet(rules.Objections.Location)),
            (ObjectionType.Trust, new PhraseSet(rules.Objections.Trust)),
            (ObjectionType.Pain, new PhraseSet(rules.Objections.Pain)),
            (ObjectionType.Other, new PhraseSet(rules.Objections.Other)),
        ];
        _competitor = new PhraseSet(rules.Competitor);
        _inquiry = new PhraseSet(rules.Inquiry);
        _bookingIntent = new PhraseSet(rules.BookingIntent);
        _strongConfirmation = new PhraseSet(rules.CustomerConfirmation.Strong);
        _weakAck = new PhraseSet(rules.CustomerConfirmation.WeakAck);
        _proposal = new PhraseSet(rules.BookingProposal);
        _advisorConfirmation = new PhraseSet(rules.AdvisorConfirmation);
        _refusal = new PhraseSet(rules.Refusal);
        _cancellation = new PhraseSet(rules.Cancellation);
    }

    /// <summary>The classifier of the default Spanish (Colombia) rules.</summary>
    public static RuleBasedConversationClassifier CreateDefault() => new(ClassificationRuleSet.LoadDefault());

    public Task<ClassificationResult> ClassifyAsync(ConversationSnapshot snapshot, CancellationToken cancellationToken = default)
        => Task.FromResult(Classify(snapshot));

    public ClassificationResult Classify(ConversationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var messages = snapshot.Messages.Select(Analyze).ToList();
        var codes = new List<string>();
        void Fire(string code)
        {
            if (!codes.Contains(code))
            {
                codes.Add(code);
            }
        }

        var service = ReadService(messages, Fire);
        var reason = ReadReason(messages, Fire);
        var objection = ReadObjection(messages, Fire);

        // Customer signals that matter for the ending, by position in the conversation.
        var lastRefusal = -1;
        var lastPositive = -1;
        var customerSignal = false;
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (!m.Customer)
            {
                continue;
            }

            m.AcceptsProposal = m.WeakAck && NearestPreviousAdvisor(messages, i)?.Proposal == true;
            if (m.Refusal || m.Cancel)
            {
                lastRefusal = i;
            }

            if (m.Intent || m.StrongConfirmation || m.AcceptsProposal)
            {
                lastPositive = i;
            }

            customerSignal |= m.Topic || m.Services.Count > 0 || m.GeneralServices.Count > 0 || m.Reasons.Count > 0 || m.Objections.Count > 0
                              || m.Competitor || m.Inquiry || m.Intent || m.NegatedIntent || m.AcceptsProposal || m.StrongConfirmation || m.Refusal || m.Cancel;
        }

        foreach (var m in messages)
        {
            if (m.Proposal)
            {
                Fire("proposal.advisor");
            }

            if (m.Intent)
            {
                Fire("intent.booking");
            }

            if (m.NegatedIntent)
            {
                Fire("intent.negated");
            }

            if (m.AcceptsProposal)
            {
                Fire("ack.accepts_proposal");
            }

            if (m.Competitor)
            {
                Fire("competitor");
            }

            if (m.Inquiry)
            {
                Fire("inquiry");
            }
        }

        ClassificationOutcome outcome;
        LossReason? lossReason = null;
        decimal confidence;
        if (lastRefusal >= 0 && lastRefusal > lastPositive)
        {
            outcome = ClassificationOutcome.Lost;
            var refusing = messages[lastRefusal];
            lossReason = ReadLossReason(messages, lastRefusal, refusing.Cancel && !refusing.Refusal);
            var cancelled = refusing.Cancel;
            Fire(cancelled ? "outcome.lost.cancellation" : "outcome.lost.refusal");
            Fire("loss." + LossReasonCode(lossReason!.Value));
            confidence = cancelled ? 0.85m : lossReason == LossReason.NotInterested ? 0.85m : 0.9m;
        }
        else if (ReadWin(messages) is { } win)
        {
            outcome = ClassificationOutcome.Won;
            Fire(win.ByAdvisor ? "outcome.won.advisor_confirmation" : "outcome.won.customer_confirmation");
            confidence = win.ByAdvisor ? 0.9m : 0.8m;
        }
        else if (customerSignal)
        {
            outcome = ClassificationOutcome.InFollowUp;
            Fire("outcome.in_follow_up");
            var strength = (service is not null ? 1 : 0) + (reason is not null ? 1 : 0) + (objection != ObjectionType.None ? 1 : 0)
                           + (codes.Contains("intent.booking") || codes.Contains("ack.accepts_proposal") ? 2 : 0) + (codes.Contains("inquiry") ? 1 : 0);
            confidence = Math.Min(0.8m, 0.5m + (0.1m * strength));
        }
        else
        {
            outcome = ClassificationOutcome.Undetermined;
            Fire("outcome.undetermined");
            confidence = 0.2m;
        }

        return new ClassificationResult(service, reason, objection, outcome, lossReason, Math.Clamp(confidence, 0m, 1m), codes);
    }

    // ---- Reading a message ------------------------------------------------------------------------------------------------

    private sealed class MessageSignals
    {
        public bool Customer { get; init; }

        public List<string> Services { get; } = [];

        /// <summary>The service in general ("depilación láser"), as opposed to a zone.</summary>
        public List<string> GeneralServices { get; } = [];

        public List<string> Reasons { get; } = [];

        public List<ObjectionType> Objections { get; } = [];

        public bool Competitor { get; set; }

        public bool Inquiry { get; set; }

        /// <summary>The customer talked about the service, an objection or the booking, even to deny it ("no duele, ¿cierto?"): they are in the conversation.</summary>
        public bool Topic { get; set; }

        public bool Intent { get; set; }

        public bool NegatedIntent { get; set; }

        public bool StrongConfirmation { get; set; }

        public bool WeakAck { get; set; }

        public bool AcceptsProposal { get; set; }

        public bool Refusal { get; set; }

        public bool Cancel { get; set; }

        public bool Proposal { get; set; }

        public bool AdvisorConfirmation { get; set; }

        /// <summary>The most important objection of this message (price before timing before ..., as in the rules).</summary>
        public ObjectionType Objection => Objections.Count == 0 ? ObjectionType.None : Objections[0];
    }

    private MessageSignals Analyze(SnapshotMessage message)
    {
        var customer = message.Direction == MessageDirection.Inbound;
        var signals = new MessageSignals { Customer = customer };
        if (string.IsNullOrWhiteSpace(message.Text))
        {
            return signals;
        }

        var text = TextNormalizer.Tokenize(message.Text, _aliases, _clauseBreakers);
        if (text.Tokens.Length == 0)
        {
            return signals;
        }

        foreach (var (code, exclusive, phrases) in _services)
        {
            if (Count(phrases, text, negatable: true, signals).Positive > 0)
            {
                signals.Services.Add(exclusive ? "!" + code : code);
            }
        }

        foreach (var (code, phrases) in _generalServices)
        {
            if (Count(phrases, text, negatable: true, signals).Positive > 0)
            {
                signals.GeneralServices.Add(code);
            }
        }

        if (!customer)
        {
            signals.Proposal = Count(_proposal, text, negatable: false).Positive > 0;
            signals.AdvisorConfirmation = ConfirmationsOutsideQuestions(_advisorConfirmation, text) > 0;
            return signals;
        }

        foreach (var (code, phrases) in _reasons)
        {
            if (Count(phrases, text, negatable: true, signals).Positive > 0)
            {
                signals.Reasons.Add(code);
            }
        }

        foreach (var (type, phrases) in _objections)
        {
            if (Count(phrases, text, negatable: true, signals).Positive > 0)
            {
                signals.Objections.Add(type);
            }
        }

        signals.Competitor = Count(_competitor, text, negatable: true, signals).Positive > 0;
        signals.Inquiry = Count(_inquiry, text, negatable: true, signals).Positive > 0;

        var intent = Count(_bookingIntent, text, negatable: true, signals);
        signals.Intent = intent.Positive > 0;
        signals.NegatedIntent = intent.Positive == 0 && intent.Negated > 0;
        signals.StrongConfirmation = Count(_strongConfirmation, text, negatable: true).Positive > 0;
        signals.WeakAck = Count(_weakAck, text, negatable: false).Positive > 0;
        signals.Refusal = Count(_refusal, text, negatable: false).Positive > 0;
        signals.Cancel = Count(_cancellation, text, negatable: true).Positive > 0;

        // A message that refuses, cancels or says the customer already went elsewhere is not a request to book, whatever words it shares
        // with one ("cancelo mi cita", "ya agendé en otro lado").
        if (signals.Refusal || signals.Cancel || signals.Competitor)
        {
            signals.Intent = false;
            signals.StrongConfirmation = false;
            signals.WeakAck = false;
        }

        return signals;
    }

    private (int Positive, int Negated) Count(PhraseSet phrases, TokenizedText text, bool negatable, MessageSignals? topic = null)
    {
        var positive = 0;
        var negated = 0;
        foreach (var hit in phrases.Find(text))
        {
            if (topic is not null)
            {
                topic.Topic = true;
            }

            if (negatable && IsNegated(text, hit))
            {
                negated++;
            }
            else
            {
                positive++;
            }
        }

        return (positive, negated);
    }

    // A confirmation phrased as a question ("¿quedó agendada?") confirms nothing.
    private int ConfirmationsOutsideQuestions(PhraseSet phrases, TokenizedText text)
        => phrases.Find(text).Count(hit => !text.Sentences[hit.Sentence].IsQuestion && !IsNegated(text, hit));

    // A negator among the few words that come before the phrase, in its own clause.
    private bool IsNegated(TokenizedText text, PhraseHit hit)
    {
        var sentence = text.Sentences[hit.Sentence];
        var clause = text.Tokens[hit.Start].Clause;
        for (var i = Math.Max(sentence.Start, hit.Start - _negationWindow); i < hit.Start; i++)
        {
            if (text.Tokens[i].Clause == clause && _negators.Contains(text.Tokens[i].Text))
            {
                return true;
            }
        }

        return false;
    }

    private static MessageSignals? NearestPreviousAdvisor(List<MessageSignals> messages, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (!messages[i].Customer)
            {
                return messages[i];
            }
        }

        return null;
    }

    // ---- Reading the conversation -----------------------------------------------------------------------------------------

    private string? ReadService(List<MessageSignals> messages, Action<string> fire)
    {
        var fromCustomer = Distinct(messages.Where(m => m.Customer).SelectMany(m => m.Services));
        if (fromCustomer.Count > 0)
        {
            return FinishService(fromCustomer, "service.", fire);
        }

        var general = messages.Where(m => m.Customer).SelectMany(m => m.GeneralServices).FirstOrDefault();
        if (general is not null)
        {
            fire("service." + general);
            return general;
        }

        var fromAdvisor = Distinct(messages.Where(m => !m.Customer).SelectMany(m => m.Services));
        return fromAdvisor.Count > 0 ? FinishService(fromAdvisor, "service.from_advisor.", fire) : null;
    }

    private static List<string> Distinct(IEnumerable<string> codes) => [.. codes.Distinct(StringComparer.Ordinal)];

    // "!" marks an exclusive service (full body): it alone is the answer.
    private static string FinishService(List<string> found, string codePrefix, Action<string> fire)
    {
        var exclusive = found.FirstOrDefault(c => c.StartsWith('!'));
        var chosen = exclusive is not null ? [exclusive[1..]] : found.Select(c => c.TrimStart('!')).Take(MaxServices).ToList();
        foreach (var code in chosen)
        {
            fire(codePrefix + code);
        }

        return string.Join(',', chosen);
    }

    private static string? ReadReason(List<MessageSignals> messages, Action<string> fire)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var code in messages.Where(m => m.Customer).SelectMany(m => m.Reasons))
        {
            if (!counts.TryAdd(code, 1))
            {
                counts[code]++;
            }
            else
            {
                order.Add(code);
            }
        }

        if (order.Count == 0)
        {
            return null;
        }

        var best = order.OrderByDescending(c => counts[c]).First(); // OrderByDescending is stable: ties keep the order of first mention
        fire("reason." + best);
        return best;
    }

    private static ObjectionType ReadObjection(List<MessageSignals> messages, Action<string> fire)
    {
        var last = messages.LastOrDefault(m => m.Customer && m.Objections.Count > 0);
        if (last is null)
        {
            return ObjectionType.None;
        }

        fire("objection." + last.Objection.ToString().ToLowerInvariant());
        return last.Objection;
    }

    // The loss reason: a competitor named with (or in the customer message just before) the refusal, else the objection of the refusing
    // message, else the latest objection before it; with none, a refusal is "not interested" and a cancellation is "other".
    private static LossReason ReadLossReason(List<MessageSignals> messages, int refusalIndex, bool cancellationOnly)
    {
        var refusal = messages[refusalIndex];
        var previousCustomer = messages.Take(refusalIndex).LastOrDefault(m => m.Customer);
        if (refusal.Competitor || previousCustomer is { Competitor: true })
        {
            return LossReason.Competitor;
        }

        var objection = refusal.Objection;
        if (objection == ObjectionType.None)
        {
            objection = messages.Take(refusalIndex).LastOrDefault(m => m.Customer && m.Objections.Count > 0)?.Objection ?? ObjectionType.None;
        }

        return objection switch
        {
            ObjectionType.Price => LossReason.Price,
            ObjectionType.Timing => LossReason.Timing,
            ObjectionType.Location => LossReason.Location,
            ObjectionType.None => cancellationOnly ? LossReason.Other : LossReason.NotInterested,
            _ => LossReason.Other,
        };
    }

    private static string LossReasonCode(LossReason reason) => reason switch
    {
        LossReason.NotInterested => "not_interested",
        LossReason.NoResponse => "no_response",
        _ => reason.ToString().ToLowerInvariant(),
    };

    // Won: the customer's intent to book, and then a confirmation: the business says it is booked, or the customer confirms after
    // showing intent or answering a proposal. Either way it is an affirmative, positive statement (questions and negations never count).
    private static (bool ByAdvisor, int Index)? ReadWin(List<MessageSignals> messages)
    {
        var customerIntentSeen = false;
        var proposalSeen = false;
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (m.Customer)
            {
                // The confirmation cannot be its own evidence: "confirmo" with no booking talk before it proves nothing.
                if (m.StrongConfirmation && (customerIntentSeen || proposalSeen || m.Intent))
                {
                    return (false, i);
                }

                customerIntentSeen |= m.Intent || m.AcceptsProposal || m.StrongConfirmation;
            }
            else
            {
                if (m.AdvisorConfirmation && customerIntentSeen)
                {
                    return (true, i);
                }

                proposalSeen |= m.Proposal;
            }
        }

        return null;
    }
}
