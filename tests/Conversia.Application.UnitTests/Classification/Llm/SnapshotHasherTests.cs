using Conversia.Application.Classification;
using Conversia.Application.Classification.Llm;
using Conversia.Domain.Enums;

namespace Conversia.Application.UnitTests.Classification.Llm;

/// <summary>The snapshot hash (design D23, spec SM-5 S1): any input that changes what the model would read, or how, changes the hash.</summary>
public class SnapshotHasherTests
{
    private static readonly DateTime T0 = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Id1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Id2 = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private static readonly SnapshotHashInputs Inputs = new("es-CO.1.pre1.ctxabcd1234", "claude-haiku-5-5", ExtractionSchema.Version, ExtractAgeRange: false, ConversationPreprocessorOptions.Default.Fingerprint);

    private static ConversationSnapshot Snapshot(
        string first = "Hola", string second = "60 mil", Guid? id1 = null, MessageDirection direction = MessageDirection.Inbound, string? branch = "Sede Centro",
        IReadOnlyList<string>? names = null, IReadOnlyList<string>? numbers = null, string? contact = "Ana Gómez", string? advisor = "Carlos")
        => new(Guid.NewGuid(), branch, T0, T0, T0,
            [new SnapshotMessage(direction, "text", first, T0, id1 ?? Id1), new SnapshotMessage(MessageDirection.Outbound, "text", second, T0.AddMinutes(1), Id2)],
            Guid.NewGuid(), names ?? ["Ana Gómez", "Carlos", "Sede Centro"], numbers ?? ["573001112233", "573005550100"], contact, advisor);

    private static string Hash(ConversationSnapshot snapshot, SnapshotHashInputs? inputs = null) => SnapshotHasher.Compute(snapshot, inputs ?? Inputs);

    [Fact]
    public void TheHash_IsASha256HexDigest_AndDeterministic()
    {
        var hash = Hash(Snapshot());

        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, Hash(Snapshot())); // a fresh conversation id, tenant and times: not inputs
    }

    public static TheoryData<string, ConversationSnapshot> ChangedSnapshots => new()
    {
        { "a message text", Snapshot(first: "Hola!") },
        { "a message id", Snapshot(id1: Guid.NewGuid()) },
        { "a direction", Snapshot(direction: MessageDirection.Outbound) },
        { "the branch", Snapshot(branch: "Sede Norte") },
        { "a known name", Snapshot(names: ["Ana Gómez", "Carla", "Sede Centro"]) },
        { "text moved between messages", Snapshot(first: "Hola6", second: "0 mil") },
        { "a known number (it changes the masking)", Snapshot(numbers: ["573001112299", "573005550100"]) },
        { "which name is the contact", Snapshot(contact: "Carlos", advisor: "Ana Gómez") },
        { "no advisor", Snapshot(advisor: null) },
    };

    [Theory]
    [MemberData(nameof(ChangedSnapshots))]
    public void ChangingAnyPartOfTheSnapshot_ChangesTheHash(string name, ConversationSnapshot changed)
        => Assert.True(Hash(Snapshot()) != Hash(changed), name);

    public static TheoryData<string, SnapshotHashInputs> ChangedInputs => new()
    {
        { "prompt version", Inputs with { PromptVersion = "es-CO.2" } },
        { "model", Inputs with { ModelId = "claude-other" } },
        { "schema version", Inputs with { SchemaVersion = "2" } },
        { "age extraction", Inputs with { ExtractAgeRange = true } },
        { "preprocessing", Inputs with { PreprocessingFingerprint = (ConversationPreprocessorOptions.Default with { TokenBudget = 5000 }).Fingerprint } },
    };

    [Theory]
    [MemberData(nameof(ChangedInputs))]
    public void ChangingAnyOtherInput_ChangesTheHash(string name, SnapshotHashInputs inputs)
        => Assert.True(Hash(Snapshot()) != Hash(Snapshot(), inputs), name);

    [Fact]
    public void TheOrderOfTheKnownNamesAndNumbers_DoesNotMatter()
        => Assert.Equal(Hash(Snapshot()), Hash(Snapshot(names: ["Sede Centro", "Carlos", "Ana Gómez"], numbers: ["573005550100", "573001112233"])));

    [Fact]
    public void OnlyTheCappedWindow_Counts()
    {
        var text = new string('a', RuleBasedConversationClassifier.MaxCharactersPerMessage);
        Assert.Equal(Hash(Snapshot(first: text + "x")), Hash(Snapshot(first: text + "y")));

        var old = Enumerable.Range(0, RuleBasedConversationClassifier.MaxMessages).Select(i => new SnapshotMessage(MessageDirection.Inbound, "text", "m" + i, T0, Guid.Empty)).ToList();
        var a = new ConversationSnapshot(Guid.NewGuid(), null, T0, null, null, [new SnapshotMessage(MessageDirection.Inbound, "text", "outside A", T0, Id1), .. old]);
        var b = new ConversationSnapshot(Guid.NewGuid(), null, T0, null, null, [new SnapshotMessage(MessageDirection.Inbound, "text", "outside B", T0, Id2), .. old]);
        Assert.Equal(Hash(a), Hash(b));
    }
}
