using Conversia.Application.Classification;
using Conversia.Application.Classification.Llm;
using Conversia.Domain.Enums;

namespace Conversia.Application.UnitTests.Classification.Llm;

/// <summary>
/// Personal data is replaced by stable per-conversation placeholders before any text leaves the process (design D9, spec CC-8). Every
/// value here is synthetic. Over-masking a price costs a little analysis; leaking an identity number costs a privacy incident.
/// </summary>
public class PseudonymizerTests
{
    private static readonly DateTime T0 = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private static ConversationSnapshot Snapshot(
        IReadOnlyList<string> texts, string? contact = null, string? advisor = null, string? branch = null, IReadOnlyList<string>? numbers = null)
    {
        var messages = texts.Select((t, i) => new SnapshotMessage(i % 2 == 0 ? MessageDirection.Inbound : MessageDirection.Outbound, "text", t, T0.AddMinutes(i), Guid.NewGuid())).ToList();
        var names = new[] { contact, advisor, branch }.OfType<string>().ToList();
        return new ConversationSnapshot(Guid.NewGuid(), branch, T0, T0, T0, messages, Guid.NewGuid(), names, numbers ?? [], contact, advisor);
    }

    private static IReadOnlyList<string?> Mask(IReadOnlyList<string> texts, string? contact = null, string? advisor = null, string? branch = null, IReadOnlyList<string>? numbers = null)
        => [.. Pseudonymizer.Pseudonymize(Snapshot(texts, contact, advisor, branch, numbers)).Messages.Select(m => m.Text)];

    private static string? Mask(string text, string? contact = null, string? advisor = null, string? branch = null, IReadOnlyList<string>? numbers = null)
        => Mask([text], contact, advisor, branch, numbers)[0];

    // ---- Names --------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheContactsNameAndNumber_NeverReachThePayload()
    {
        // CC-8 S1.
        var masked = Mask(["Hola, soy Maria Perez, mi número es +57 300 123 4567", "Hola María, ¿en qué te ayudo?"], contact: "Maria Perez");

        Assert.Equal("Hola, soy [CLIENTE], mi número es [TEL_1]", masked[0]);
        Assert.Equal("Hola [CLIENTE], ¿en qué te ayudo?", masked[1]);
    }

    [Fact]
    public void Names_AreMatchedAsWholeTokens_IgnoringAccentsAndCase()
    {
        Assert.Equal("[CLIENTE] [CLIENTE] quiere cita, Mariana no", Mask("MARTÍNEZ martinez quiere cita, Mariana no", contact: "Lucia Martinez"));
    }

    [Fact]
    public void TheAdvisorAndTheBranch_AreMaskedToo()
    {
        // CC-8 S14 (names).
        Assert.Equal("Soy [ASESOR] de [SEDE], te esperamos", Mask("Soy Carlos de Sede Chapinero, te esperamos", advisor: "Carlos Ruiz", branch: "Sede Chapinero"));
    }

    [Theory]
    [InlineData("Sol", "hay sol hoy")]
    [InlineData("Rosa", "quiero las uñas rosa")]
    [InlineData("Luz Paz", "no hay luz en paz")]
    [InlineData("Ana María López", "mañana voy con mariana")]
    public void ShortOrStoplistedNameTokens_AreNotReplacedInOrdinaryWords(string name, string text)
    {
        // CC-8 S13: a minimum token length and a stoplist.
        Assert.Equal(text, Mask(text, contact: name));
    }

    [Fact]
    public void ANameMadeOfStoplistedTokens_IsStillReplacedWhenWrittenInFull()
        => Assert.Equal("hola [CLIENTE], hay luz", Mask("hola Luz Paz, hay luz", contact: "Luz Paz"));

    // ---- Phones -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("llámame al 3001234567")]
    [InlineData("llámame al 300-123-4567")]
    [InlineData("llámame al 300 123 4567")]
    [InlineData("llámame al +57 300 123 4567")]
    [InlineData("llámame al 0057 3001234567")]
    [InlineData("llámame al 573001234567")]
    [InlineData("llámame al +1 415 555 0100")]
    public void PhoneShapes_BecomeATelPlaceholder(string text)
    {
        // CC-8 S2.
        Assert.Equal("llámame al [TEL_1]", Mask(text));
    }

    [Fact]
    public void TheSamePhone_KeepsItsPlaceholder_AndAnotherGetsTheNext()
    {
        // CC-8 S3, across messages and formats.
        var masked = Mask(["mi cel 3001234567", "anotado +57 300 123 4567", "y el de mi hermana 311 222 3344"]);

        Assert.Equal(["mi cel [TEL_1]", "anotado [TEL_1]", "y el de mi hermana [TEL_2]"], masked);
    }

    [Fact]
    public void ABusinessScopedUserId_IsMasked()
        => Assert.Equal("mi id es [TEL_1]", Mask("mi id es CO.13491208655302741918")); // CC-8 S5

    [Fact]
    public void ABareRunWithAPhoneCue_IsAPhone()
        => Assert.Equal("mi cel es [TEL_1]", Mask("mi cel es 6012345678")); // CC-8 S12

    [Fact]
    public void KnownNumbers_AreMaskedInAnyFormat()
    {
        // CC-8 S14: the contact's WhatsApp id and the channel number, written with any separators.
        var masked = Mask(["escríbeme al 57 300-123.4567", "o a la línea (601) 555 0199"], numbers: ["573001234567", "6015550199"]);

        Assert.Equal(["escríbeme al [TEL_1]", "o a la línea [TEL_2]"], masked);
    }

    // ---- Identity numbers and amounts ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("mi cédula es 1020304050", "mi cédula es [ID]")]
    [InlineData("cc 52.123.456", "cc [ID]")]
    [InlineData("C.C. 52123456", "C.C. [ID]")]
    [InlineData("documento: 123456", "documento: [ID]")]
    [InlineData("TI 1001234567", "TI [ID]")]
    [InlineData("pasaporte 98765432", "pasaporte [ID]")]
    [InlineData("NIT 900123456", "NIT [ID]")]
    public void AnIdCue_MasksTheNumberAfterIt_WhateverItsShape(string text, string expected)
        => Assert.Equal(expected, Mask(text)); // CC-8 S8

    [Theory]
    [InlineData("12.345.678")]
    [InlineData("1.020.304.050")]
    [InlineData("1234567890")]
    [InlineData("8765432")]
    public void ALongNumberWithoutACue_IsMasked(string number)
        => Assert.Equal("es el [ID]", Mask("es el " + number)); // CC-8 S9, S11: fall-through = mask

    [Theory]
    [InlineData("$60.000 la sesión")]
    [InlineData("1.200.000 pesos")]
    [InlineData("60 mil")]
    [InlineData("paquete en 1200000 pesos")]
    [InlineData("son 850000")]
    [InlineData("COP 1.500.000")]
    [InlineData("$ 2.400.000 el paquete")]
    [InlineData("150k")]
    [InlineData("a las 3 pm, 6 sesiones")]
    public void Amounts_AndShortNumbers_AreKept(string text)
        => Assert.Equal(text, Mask(text)); // CC-8 S4, S10

    // ---- Emails, links, handles ----------------------------------------------------------------------------------------

    [Fact]
    public void EmailsLinksAndHandles_BecomePlaceholders()
    {
        // CC-8 S6.
        Assert.Equal("escríbeme a [EMAIL] o mira [URL] y [URL], sígueme en [USUARIO]",
            Mask("escríbeme a ana.gomez+promo@example.com o mira https://example.com/p?id=7 y example.co/promo, sígueme en @ana_gomez.co"));
    }

    // ---- What leaves the process ---------------------------------------------------------------------------------------

    [Fact]
    public void TheResult_KeepsTheShapeOfTheConversation_ButNoKnownValue()
    {
        var snapshot = Snapshot(["Hola", "Hola Ana"], contact: "Ana Gómez", advisor: "Carlos Ruiz", branch: "Sede Centro", numbers: ["573001234567"]);
        snapshot = snapshot with { Messages = [.. snapshot.Messages, new SnapshotMessage(MessageDirection.Inbound, "image", null, T0.AddMinutes(5), Guid.NewGuid())] };

        var masked = Pseudonymizer.Pseudonymize(snapshot);

        Assert.Equal(snapshot.Messages.Select(m => (m.Direction, m.Type, m.SentAt, m.Id)), masked.Messages.Select(m => (m.Direction, m.Type, m.SentAt, m.Id)));
        Assert.Null(masked.Messages[2].Text);
        Assert.Equal(((string?)null, 0, 0, (string?)null, (string?)null), (masked.BranchName, masked.Names.Count, masked.Numbers.Count, masked.ContactName, masked.AdvisorName));
    }

    [Fact]
    public void ThePlaceholders_AreNeverReadAsData()
    {
        // A second pass changes nothing: placeholders are not names or numbers.
        var once = Mask("soy Maria Perez, cc 1020304050, cel 3001234567", contact: "Maria Perez");

        Assert.Equal(once, Mask(once!, contact: "Maria Perez"));
    }

    // ---- Review findings: every leaking input is a case ----------------------------------------------------------------

    private static IReadOnlyList<string?> MaskSides(params (MessageDirection Direction, string Text)[] messages)
    {
        var snapshot = new ConversationSnapshot(Guid.NewGuid(), null, T0, T0, T0,
            [.. messages.Select((m, i) => new SnapshotMessage(m.Direction, "text", m.Text, T0.AddMinutes(i), Guid.NewGuid()))]);
        return [.. Pseudonymizer.Pseudonymize(snapshot).Messages.Select(m => m.Text)];
    }

    [Theory]
    [InlineData("escríbeme al 300 123 4567")]
    [InlineData("escríbeme al 300 123 4567")]
    [InlineData("escríbeme al 300 123 4567")]
    [InlineData("escríbeme al 300　123　4567")]
    [InlineData("escríbeme al 300  123 4567")]
    [InlineData("escríbeme al 300, 123, 4567")]
    [InlineData("escríbeme al 300/123/4567")]
    [InlineData("escríbeme al 300 - 123 - 4567")]
    [InlineData("escríbeme al 300​123‌4567")]
    [InlineData("escríbeme al ３００１２３４５６７")]
    [InlineData("escríbeme al ٣٠٠١٢٣٤٥٦٧")]
    [InlineData("escríbeme al 3OO1234567")]
    [InlineData("escríbeme al 3oo 123 4567")]
    public void UnicodeSpacesSeparatorsAndLookalikeDigits_DoNotLetAPhoneThrough(string text)
    {
        var masked = Mask(text)!;

        Assert.Equal("escríbeme al [TEL_1]", masked);
    }

    [Fact]
    public void DigitsGluedToLetters_AreStillAPhone()
        => Assert.Equal("cel[TEL_1] o whatsapp[TEL_2]", Mask("cel3001234567 o whatsapp3112223344"));

    [Theory]
    [InlineData("Oooo qué bien, OK")]
    [InlineData("zonas 1, 2, 3, 4, 5, 6 y 7")]
    [InlineData("precios: 60, 90, 120, 200 mil")]
    [InlineData("precios: 60, 90, 120, 200 y 250")]
    [InlineData("tallas 38/40/42/44")]
    [InlineData("sesiones 1/2/3")]
    public void OrdinaryWordsAndShortListsOfNumbers_AreKept(string text)
        => Assert.Equal(text, Mask(text));

    [Theory]
    [InlineData("mi correo es josé@gmail.com")]
    [InlineData("mi correo es peña.ñandú@correo.com.co")]
    [InlineData("mi correo es juan @ gmail . com")]
    [InlineData("mi correo es juan arroba gmail punto com")]
    [InlineData("mi correo es JUAN.PEREZ ARROBA hotmail PUNTO com PUNTO co")]
    public void EmailsWithAccentsSpacesOrSpelledOut_AreMasked(string text)
        => Assert.Equal("mi correo es [EMAIL]", Mask(text));

    [Fact]
    public void ANumberSplitAcrossTwoMessagesOfTheSameSide_IsMasked()
    {
        var masked = MaskSides((MessageDirection.Inbound, "mi número es 300 123"), (MessageDirection.Inbound, "4567"), (MessageDirection.Outbound, "Gracias"));

        Assert.Equal(["mi número es [TEL_1]", "[TEL_1]", "Gracias"], masked);
    }

    [Fact]
    public void NumbersOfDifferentSides_OrThatDoNotFormAPhone_AreNotJoined()
    {
        var masked = MaskSides((MessageDirection.Inbound, "son 300"), (MessageDirection.Outbound, "4567 sesiones"), (MessageDirection.Inbound, "tengo 3"), (MessageDirection.Inbound, "200 dudas"));

        Assert.Equal(["son 300", "4567 sesiones", "tengo 3", "200 dudas"], masked);
    }

    [Theory]
    [InlineData("cuesta 1.200.000")]
    [InlineData("el paquete vale 2.400.000 con descuento")]
    [InlineData("precio de la sesión: 1.350.000")]
    [InlineData("son 1.200.000 de pesos")]
    [InlineData("unos 2 millones")]
    [InlineData("3.500.000 millones")]
    [InlineData("nací el 12/05/1990")]
    [InlineData("la cita es el 05-11-2026")]
    [InlineData("desde el 2026-11-05")]
    [InlineData("el 12.05.1990")]
    public void PricesNearAPriceCue_AndDates_AreNotIds(string text)
        => Assert.Equal(text, Mask(text));

    [Fact]
    public void ADottedNumberWithAnIdCue_IsStillAnId_EvenNearAPriceCue()
        => Assert.Equal("para el precio necesito tu cc [ID]", Mask("para el precio necesito tu cc 52.123.456"));

    [Fact]
    public void AThreeLetterNameToken_IsMasked_WhenItIsNotAnOrdinaryWord()
        => Assert.Equal("soy [CLIENTE], hola [CLIENTE]", Mask("soy ana, hola Ana María López", contact: "Ana María López"));

    [Theory]
    [InlineData("mira linktr.ee/ana")]
    [InlineData("mira tienda.com.mx/promo")]
    [InlineData("mira bit.ly/3xYz")]
    [InlineData("mira mi-spa.store")]
    [InlineData("mira laser.clinic")]
    public void GenericDomains_AreMasked(string text)
        => Assert.Equal("mira [URL]", Mask(text));

    [Theory]
    [InlineData("ok.gracias por todo")]
    [InlineData("listo.la cita es mañana")]
    [InlineData("Dr.Gómez me atendió")]
    public void WordsJoinedByAMissingSpace_AreNotDomains(string text)
        => Assert.Equal(text, Mask(text));

    [Theory]
    [InlineData("mi IG: ana.gomez", "mi IG: [USUARIO]")]
    [InlineData("mi insta es ana_gomez", "mi insta es [USUARIO]")]
    [InlineData("búscame en instagram anagomez92", "búscame en instagram [USUARIO]")]
    [InlineData("FB: juan.perez.92", "FB: [USUARIO]")]
    [InlineData("tiktok @juanp", "tiktok [USUARIO]")]
    [InlineData("por instagram vi la promo", "por instagram vi la promo")]
    [InlineData("por instagram vimos la promo", "por instagram vimos la promo")]
    public void HandlesAfterASocialNetworkCue_AreMasked(string text, string expected)
        => Assert.Equal(expected, Mask(text));
}
