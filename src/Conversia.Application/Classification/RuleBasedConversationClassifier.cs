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
/// <item>The conversation is read <b>in order</b>, message by message, and the state at the end decides.</item>
/// <item><b>Won</b> needs the customer's intent to book <i>and</i> a confirmation from the <b>business</b>: an advisor message that says the
/// booking is made (not inside a question, not negated), or the customer's affirmative, non-question reply to an advisor proposal that names
/// a day or an hour. The customer alone can never produce a Won. Once confirmed, only an explicit cancellation or withdrawal of the booking
/// ends it; a bare "no gracias" to "¿algo más?" does not.</item>
/// <item><b>Lost</b> is the customer's last stance being a refusal or a cancellation (a later request to book undoes it). The loss reason
/// comes from the objection raised with, or just before, the refusal.</item>
/// <item>Otherwise <b>InFollowUp</b> when the customer said something about the service, and <b>Undetermined</b> when nothing can be read.</item>
/// </list>
/// The work is bounded: only the last <see cref="MaxMessages"/> messages are read, and only the first <see cref="MaxCharactersPerMessage"/>
/// characters of each.
/// </summary>
public sealed class RuleBasedConversationClassifier : IConversationClassifier
{
    /// <summary>The most messages one classification reads (the latest ones).</summary>
    public const int MaxMessages = 200;

    /// <summary>The most characters of one message that are read (the first ones).</summary>
    public const int MaxCharactersPerMessage = 1000;

    private const int MaxServices = 5;

    private readonly IReadOnlyDictionary<string, string> _aliases;
    private readonly IReadOnlySet<string> _clauseBreakers;
    private readonly IReadOnlySet<string> _negators;
    private readonly int _negationWindow;
    private readonly int _cancellationWindow;

    private readonly (string Code, bool Exclusive, PhraseSet Phrases)[] _services;
    private readonly (string Code, PhraseSet Phrases)[] _generalServices;
    private readonly (string Code, PhraseSet Phrases)[] _reasons;
    private readonly (ObjectionType Type, PhraseSet Phrases)[] _objections;
    private readonly PhraseSet _competitor;
    private readonly PhraseSet _completedElsewhere;
    private readonly PhraseSet _inquiry;
    private readonly PhraseSet _bookingIntent;
    private readonly PhraseSet _bookingIntentWeak;
    private readonly PhraseSet _dayTime;
    private readonly PhraseSet _strongConfirmation;
    private readonly PhraseSet _weakAck;
    private readonly PhraseSet _proposal;
    private readonly PhraseSet _proposalCue;
    private readonly PhraseSet _advisorConfirmation;
    private readonly PhraseSet _advisorSchedule;
    private readonly PhraseSet _refusal;
    private readonly PhraseSet _withdrawal;
    private readonly PhraseSet _cancelVerbs;
    private readonly PhraseSet _cancelObjects;
    private readonly PhraseSet _cancelPayment;
    private readonly PhraseSet _cancelRequestCues;

    public RuleBasedConversationClassifier(ClassificationRuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();

        _aliases = rules.Aliases.ToDictionary(a => TextNormalizer.Normalize(a.Key), a => TextNormalizer.Normalize(a.Value), StringComparer.Ordinal);
        _clauseBreakers = rules.ClauseBreakers.Select(TextNormalizer.Normalize).ToHashSet(StringComparer.Ordinal);
        _negators = rules.Negators.Select(TextNormalizer.Normalize).ToHashSet(StringComparer.Ordinal);
        _negationWindow = rules.NegationWindow;
        _cancellationWindow = rules.Cancellation.Window;

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
        _completedElsewhere = new PhraseSet(rules.CompletedElsewhere);
        _inquiry = new PhraseSet(rules.Inquiry);
        _bookingIntent = new PhraseSet(rules.BookingIntent);
        _bookingIntentWeak = new PhraseSet(rules.BookingIntentWeak);
        _dayTime = new PhraseSet(rules.DayTime);
        _strongConfirmation = new PhraseSet(rules.CustomerConfirmation.Strong);
        _weakAck = new PhraseSet(rules.CustomerConfirmation.WeakAck);
        _proposal = new PhraseSet(rules.BookingProposal);
        _proposalCue = new PhraseSet(rules.ProposalCue);
        _advisorConfirmation = new PhraseSet(rules.AdvisorConfirmation);
        _advisorSchedule = new PhraseSet(rules.AdvisorScheduleStatement);
        _refusal = new PhraseSet(rules.Refusal);
        _withdrawal = new PhraseSet(rules.Withdrawal);
        _cancelVerbs = new PhraseSet(rules.Cancellation.Verbs);
        _cancelObjects = new PhraseSet(rules.Cancellation.Objects);
        _cancelPayment = new PhraseSet(rules.Cancellation.PaymentWords);
        _cancelRequestCues = new PhraseSet(rules.Cancellation.RequestCues);
    }

    /// <summary>The classifier of the default Spanish (Colombia) rules.</summary>
    public static RuleBasedConversationClassifier CreateDefault() => new(ClassificationRuleSet.LoadDefault());

    public Task<ClassificationResult> ClassifyAsync(ConversationSnapshot snapshot, CancellationToken cancellationToken = default)
        => Task.FromResult(Classify(snapshot, cancellationToken));

    /// <exception cref="OperationCanceledException">The token was cancelled; it is checked between messages.</exception>
    public ClassificationResult Classify(ConversationSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var all = snapshot.Messages;
        var first = Math.Max(0, all.Count - MaxMessages);
        var messages = new List<MessageSignals>(all.Count - first);
        for (var i = first; i < all.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            messages.Add(Analyze(all[i]));
        }

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

        // The conversation, in order. "engaged": the customer showed intent (or answered a proposal) and has not refused since. "booking":
        // who confirmed it. "lostAt": the message where the customer's last stance became a refusal or a cancellation.
        var engaged = false;
        var booking = Booking.None;
        var lostAt = -1;
        var customerSignal = false;
        for (var i = 0; i < messages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var m = messages[i];
            if (!m.Customer)
            {
                // Only a business that says it, and only to a customer who asked, makes a booking.
                if (m.AdvisorConfirmation && engaged && booking != Booking.ByAdvisor)
                {
                    booking = Booking.ByAdvisor;
                    lostAt = -1;
                }

                continue;
            }

            if (m.WeakAck || m.StrongConfirmation)
            {
                var proposal = PrecedingAdvisorRun(messages, i);
                m.AcceptsProposal = proposal.Proposal;
                m.AcceptsConcreteProposal = proposal.Proposal && proposal.DayTime;
            }

            customerSignal |= m.Topic || m.Services.Count > 0 || m.GeneralServices.Count > 0 || m.Reasons.Count > 0 || m.Objections.Count > 0
                              || m.Competitor || m.Inquiry || m.Intent || m.NegatedIntent || m.AcceptsProposal || m.StrongConfirmation || m.Refusal
                              || m.Cancel || m.Withdrawal;

            var endsBooking = m.Cancel || (booking != Booking.None && (m.Withdrawal || (m.Refusal && m.Competitor)));
            if (m.Reschedule)
            {
                // "Cancel my booking and give me another day": the booking is in doubt, the customer is still in the conversation.
                booking = Booking.None;
                engaged = true;
                lostAt = -1;
            }
            else if (endsBooking)
            {
                booking = Booking.None;
                engaged = false;
                lostAt = i;
            }
            else if (m.Refusal)
            {
                // After a confirmed booking a refusal is courtesy ("no gracias" to "¿algo más?"), not a cancellation.
                if (booking == Booking.None)
                {
                    engaged = false;
                    lostAt = i;
                }
            }
            else
            {
                if (m.Intent || m.StrongConfirmation || m.AcceptsProposal)
                {
                    engaged = true;
                    lostAt = -1;
                }

                if (m.AcceptsConcreteProposal && booking == Booking.None)
                {
                    booking = Booking.ByCustomer;
                }
                else if (booking == Booking.ByCustomer && m.Objection == ObjectionType.Timing)
                {
                    booking = Booking.None; // the customer put it off after saying yes, and the business never confirmed
                }
            }
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
        if (booking != Booking.None)
        {
            outcome = ClassificationOutcome.Won;
            Fire(booking == Booking.ByAdvisor ? "outcome.won.advisor_confirmation" : "outcome.won.customer_confirmation");
            confidence = booking == Booking.ByAdvisor ? 0.9m : 0.8m;
        }
        else if (lostAt >= 0)
        {
            outcome = ClassificationOutcome.Lost;
            var refusing = messages[lostAt];
            var cancelled = refusing.Cancel || (refusing.Withdrawal && !refusing.Refusal);
            lossReason = ReadLossReason(messages, lostAt, cancelled);
            Fire(cancelled ? "outcome.lost.cancellation" : "outcome.lost.refusal");
            Fire("loss." + LossReasonCode(lossReason!.Value));
            confidence = cancelled ? 0.85m : lossReason == LossReason.NotInterested ? 0.85m : 0.9m;
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

    private enum Booking
    {
        None,
        ByCustomer,
        ByAdvisor,
    }

    // ---- Reading a message ------------------------------------------------------------------------------------------------

    private sealed class MessageSignals
    {
        public bool Customer { get; init; }

        /// <summary>No text to read (media without caption, blank).</summary>
        public bool Empty { get; set; }

        public List<string> Services { get; } = [];

        /// <summary>The service in general ("depilación láser"), as opposed to a zone.</summary>
        public List<string> GeneralServices { get; } = [];

        public List<string> Reasons { get; } = [];

        public List<ObjectionType> Objections { get; } = [];

        public bool Competitor { get; set; }

        public bool Inquiry { get; set; }

        /// <summary>The customer talked about the service, an objection or the booking, even to deny it ("no duele, ¿cierto?"): they are in the conversation.</summary>
        public bool Topic { get; set; }

        /// <summary>The customer asks to book: a booking verb anywhere, or a booking noun ("cita") outside a question.</summary>
        public bool Intent { get; set; }

        public bool NegatedIntent { get; set; }

        public bool StrongConfirmation { get; set; }

        public bool WeakAck { get; set; }

        /// <summary>A confirmation or a bare "dale" in direct reply to a booking proposal of the business.</summary>
        public bool AcceptsProposal { get; set; }

        /// <summary>The proposal it answers names a day or an hour: the reply alone settles a booking.</summary>
        public bool AcceptsConcreteProposal { get; set; }

        public bool Refusal { get; set; }

        /// <summary>A cancel verb next to a booking noun ("cancelar mi cita"), outside a plain question and not about paying.</summary>
        public bool Cancel { get; set; }

        /// <summary>"No puedo ir": it ends a booking that is confirmed, and is only a timing objection before that.</summary>
        public bool Withdrawal { get; set; }

        /// <summary>It cancels or withdraws and asks for another date in the same breath: the customer is moving the booking, not dropping it.</summary>
        public bool Reschedule { get; set; }

        public bool Proposal { get; set; }

        public bool DayTime { get; set; }

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
            signals.Empty = true;
            return signals;
        }

        var raw = message.Text.Length > MaxCharactersPerMessage ? message.Text[..MaxCharactersPerMessage] : message.Text;
        var text = TextNormalizer.Tokenize(raw, _aliases, _clauseBreakers);
        if (text.Tokens.Length == 0)
        {
            signals.Empty = true;
            return signals;
        }

        // Positive mentions of services and questions about the service: what a refusal of the same message may be taking back.
        var takesBackARefusal = new List<PhraseHit>();
        foreach (var (code, exclusive, phrases) in _services)
        {
            var hits = Scan(phrases, text, negatable: true, skipQuestions: false, signals).Positive;
            if (hits.Count > 0)
            {
                signals.Services.Add(exclusive ? "!" + code : code);
                takesBackARefusal.AddRange(hits);
            }
        }

        foreach (var (code, phrases) in _generalServices)
        {
            var hits = Scan(phrases, text, negatable: true, skipQuestions: false, signals).Positive;
            if (hits.Count > 0)
            {
                signals.GeneralServices.Add(code);
                takesBackARefusal.AddRange(hits);
            }
        }

        var dayTimeSentences = _dayTime.Find(text).Select(h => h.Sentence).ToHashSet();
        if (!customer)
        {
            signals.DayTime = dayTimeSentences.Count > 0;
            signals.Proposal = Scan(_proposal, text, negatable: false, skipQuestions: false, null).Positive.Count > 0
                               || Scan(_proposalCue, text, negatable: false, skipQuestions: false, null).Positive.Any(h => dayTimeSentences.Contains(h.Sentence));
            signals.AdvisorConfirmation = Scan(_advisorConfirmation, text, negatable: true, skipQuestions: true, null).Positive.Count > 0
                                          || Scan(_advisorSchedule, text, negatable: true, skipQuestions: true, null).Positive.Any(h => dayTimeSentences.Contains(h.Sentence));
            return signals;
        }

        foreach (var (code, phrases) in _reasons)
        {
            if (Scan(phrases, text, negatable: true, skipQuestions: false, signals).Positive.Count > 0)
            {
                signals.Reasons.Add(code);
            }
        }

        foreach (var (type, phrases) in _objections)
        {
            if (Scan(phrases, text, negatable: true, skipQuestions: false, signals).Positive.Count > 0)
            {
                signals.Objections.Add(type);
            }
        }

        var competitor = Scan(_competitor, text, negatable: true, skipQuestions: false, signals).Positive;
        signals.Competitor = competitor.Count > 0;
        var inquiry = Scan(_inquiry, text, negatable: true, skipQuestions: false, signals).Positive;
        signals.Inquiry = inquiry.Count > 0;
        takesBackARefusal.AddRange(inquiry.Where(h => text.Sentences[h.Sentence].IsQuestion));

        var strongIntent = Scan(_bookingIntent, text, negatable: true, skipQuestions: false, signals);
        var weakIntent = Scan(_bookingIntentWeak, text, negatable: true, skipQuestions: true, signals);
        takesBackARefusal.AddRange(strongIntent.Positive);
        signals.Intent = strongIntent.Positive.Count > 0 || weakIntent.Positive.Count > 0;
        signals.NegatedIntent = !signals.Intent && strongIntent.Negated > 0;
        signals.StrongConfirmation = Scan(_strongConfirmation, text, negatable: true, skipQuestions: true, signals).Positive.Count > 0;
        signals.WeakAck = Scan(_weakAck, text, negatable: false, skipQuestions: true, null).Positive.Count > 0;

        // A refusal: the phrases of the rules, or "ya agendé / ya pagué" said together with a cue of another provider. A question is not one
        // ("¿y si no me interesa?"), and neither is a refusal the message takes back afterwards ("no me interesa el cuerpo completo, solo las axilas").
        var refusals = Scan(_refusal, text, negatable: false, skipQuestions: true, null).Positive;
        var cueSentences = competitor.Select(h => h.Sentence).ToHashSet();
        refusals.AddRange(Scan(_completedElsewhere, text, negatable: false, skipQuestions: true, null).Positive.Where(h => cueSentences.Contains(h.Sentence)));
        signals.Refusal = refusals.Count > 0 && !TakenBack(text, refusals, takesBackARefusal);

        signals.Cancel = ReadsAsCancellation(text);
        signals.Withdrawal = Scan(_withdrawal, text, negatable: false, skipQuestions: true, null).Positive.Count > 0;
        signals.Reschedule = (signals.Cancel || signals.Withdrawal) && !signals.Refusal && strongIntent.Positive.Count > 0;

        // A message that refuses, cancels or says the customer went elsewhere is not a request to book, whatever words it shares with one
        // ("cancelo mi cita", "ya agendé en otro lado"), unless it asks for another date in the same breath.
        if ((signals.Refusal || signals.Cancel || signals.Competitor) && !signals.Reschedule)
        {
            signals.Intent = false;
            signals.StrongConfirmation = false;
            signals.WeakAck = false;
        }

        // An objection or a deferral in the message ("sí, pero está caro", "nos vemos el próximo mes, yo te aviso") is not a yes.
        if (signals.Objections.Count > 0)
        {
            signals.StrongConfirmation = false;
            signals.WeakAck = false;
        }

        return signals;
    }

    /// <summary>
    /// Every place a phrase of the set is found, split into those that count and the negated ones. A hit marks the message as being about
    /// the topic even when it does not count (the customer is in the conversation).
    /// </summary>
    private (List<PhraseHit> Positive, int Negated) Scan(PhraseSet phrases, TokenizedText text, bool negatable, bool skipQuestions, MessageSignals? topic)
    {
        var positive = new List<PhraseHit>();
        var negated = 0;
        foreach (var hit in phrases.Find(text))
        {
            if (topic is not null)
            {
                topic.Topic = true;
            }

            if (skipQuestions && text.Sentences[hit.Sentence].IsQuestion)
            {
                continue;
            }

            if (negatable && IsNegated(text, hit))
            {
                negated++;
            }
            else
            {
                positive.Add(hit);
            }
        }

        return (positive, negated);
    }

    // A refusal is taken back when, after the last of them, the same message names a service or asks about the service in a later sentence
    // or a later clause ("... no me interesa el cuerpo completo, solo las axilas").
    private static bool TakenBack(TokenizedText text, List<PhraseHit> refusals, List<PhraseHit> positives)
    {
        var last = refusals.MaxBy(h => h.End);
        var lastClause = text.Tokens[last.End - 1].Clause;
        return positives.Any(p => p.Sentence > last.Sentence || (p.Sentence == last.Sentence && p.Start >= last.End && text.Tokens[p.Start].Clause > lastClause));
    }

    // A cancel verb and a booking noun close to each other, in a clause that is not about paying ("ya cancelé el abono"), and not just a
    // question about the policy ("¿puedo cancelar la cita?"); "¿me cancelas la cita?" is a request.
    private bool ReadsAsCancellation(TokenizedText text)
    {
        var verbs = _cancelVerbs.Find(text).Where(v => !IsNegated(text, v)).ToList();
        if (verbs.Count == 0)
        {
            return false;
        }

        var objects = _cancelObjects.Find(text).ToList();
        var payments = _cancelPayment.Find(text).ToList();
        var requests = _cancelRequestCues.Find(text).ToList();
        foreach (var verb in verbs)
        {
            var clause = text.Tokens[verb.Start].Clause;
            if (payments.Any(p => p.Sentence == verb.Sentence && text.Tokens[p.Start].Clause == clause))
            {
                continue;
            }

            if (!objects.Any(o => o.Sentence == verb.Sentence && Gap(verb, o) <= _cancellationWindow))
            {
                continue;
            }

            if (text.Sentences[verb.Sentence].IsQuestion && !requests.Any(r => r.Sentence == verb.Sentence))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static int Gap(PhraseHit a, PhraseHit b) => b.Start >= a.End ? b.Start - a.End : a.Start - b.End;

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

    // What the business said right before the customer's message: the advisor messages just above it (a customer's media with no text does
    // not interrupt). "Direct reply" means nothing of the customer's own is in between.
    private static (bool Proposal, bool DayTime) PrecedingAdvisorRun(List<MessageSignals> messages, int index)
    {
        var i = index - 1;
        while (i >= 0 && messages[i].Customer && messages[i].Empty)
        {
            i--;
        }

        var proposal = false;
        var dayTime = false;
        for (; i >= 0 && !messages[i].Customer; i--)
        {
            proposal |= messages[i].Proposal;
            dayTime |= messages[i].DayTime;
        }

        return (proposal, dayTime);
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
}
