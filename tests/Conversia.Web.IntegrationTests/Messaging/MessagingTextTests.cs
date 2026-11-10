using Conversia.Application.DTOs.Conversations;
using Conversia.Application.Exceptions;
using Conversia.Domain.Entities;
using Conversia.Web.Services;

namespace Conversia.Web.IntegrationTests.Messaging;

/// <summary>The pure pieces of the messaging screens: what each refusal of the API reads as, when the composer is offered, and the form models.</summary>
public class MessagingTextTests
{
    /// <summary>Every code the API can put in a messaging problem, taken from the API's own definitions.</summary>
    private static readonly IReadOnlyList<string> Codes =
        [.. Enum.GetValues<MessagingProblem>().Select(MessagingException.CodeOf), SecretProtectionException.Code];

    public static TheoryData<string> DocumentedCodes
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var code in Codes)
            {
                data.Add(code);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(DocumentedCodes))]
    public void Every_code_the_api_can_answer_with_has_a_spanish_message(string code)
    {
        var message = SendFailure.Describe(code);

        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.EndsWith(".", message);
        Assert.DoesNotContain(code, message); // a person reads words, not the machine name
        Assert.Empty(Conversia.TestSupport.EnglishLeaks.Find(message));
    }

    [Fact]
    public void An_unavailable_provider_says_the_outcome_is_unknown_and_to_check_the_phone_before_sending_again()
    {
        var message = SendFailure.Describe("provider_unavailable")!;

        Assert.Contains("no se sabe si el mensaje salió", message);
        Assert.Contains("teléfono", message);
        Assert.Contains("antes de enviarlo de nuevo", message);
    }

    [Fact]
    public void A_message_that_was_sent_but_not_recorded_says_not_to_send_it_again()
    {
        var message = SendFailure.Describe("message_sent_not_recorded")!;

        Assert.Contains("se envió", message);
        Assert.Contains("No lo envíes de nuevo", message);
    }

    [Fact]
    public void The_provider_limit_explains_the_25_requests_of_the_sandbox()
    {
        var message = SendFailure.Describe("provider_limit_reached")!;

        Assert.Contains("límite", message);
        Assert.Contains("25 solicitudes en total", message);
        Assert.Contains("sandbox", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something_new")]
    public void An_unknown_code_has_no_message_of_its_own(string? code)
        => Assert.Null(SendFailure.Describe(code));

    [Fact]
    public void A_message_that_left_is_the_only_failure_after_which_the_text_is_not_kept()
    {
        Assert.True(SendFailure.MessageLeft("message_sent_not_recorded"));
        Assert.All(Codes.Where(code => code != "message_sent_not_recorded"), code => Assert.False(SendFailure.MessageLeft(code)));
    }

    [Theory]
    [InlineData(12, "12 segundos")]
    [InlineData(1, "1 segundo")]
    public void The_apis_own_limit_says_when_to_try_again(int seconds, string expected)
        => Assert.Contains(expected, SendFailure.TooManyMessages(seconds));

    [Fact]
    public void The_apis_own_limit_without_a_wait_still_reads_well()
        => Assert.Contains("más tarde", SendFailure.TooManyMessages(null));

    [Fact]
    public void A_send_that_got_no_usable_answer_says_the_outcome_is_unknown_like_an_unavailable_provider()
    {
        Assert.Contains("no se sabe si el mensaje salió", SendFailure.OutcomeUnknown);
        Assert.Contains("Verifica en el teléfono", SendFailure.OutcomeUnknown);
        Assert.Contains("antes de enviarlo de nuevo", SendFailure.OutcomeUnknown);
        Assert.Empty(Conversia.TestSupport.EnglishLeaks.Find(SendFailure.OutcomeUnknown));
    }

    [Fact]
    public void Each_call_declares_the_codes_it_expects_and_every_one_of_them_has_a_message()
    {
        Assert.Equal(Codes.Order(), SendFailure.SendCodes.Order()); // exactly what the API can answer a send with
        Assert.Equal([SecretProtectionException.Code], SendFailure.CredentialCodes);
        Assert.All(SendFailure.SendCodes.Concat(SendFailure.CredentialCodes), code => Assert.NotNull(SendFailure.Describe(code)));
    }

    // ---- when the composer is offered ----

    private static ConversationChannelState Channel(bool active = true, bool key = true, string environment = "Production")
        => new() { IsActive = active, HasProviderApiKey = key, ProviderEnvironment = environment };

    [Fact]
    public void A_manager_of_an_open_conversation_on_a_ready_line_can_write()
    {
        var state = ComposerState.For(canManage: true, "Open", Channel());

        Assert.Equal(ComposerAvailability.Available, state.Availability);
        Assert.True(state.CanSend);
        Assert.Null(state.Reason);
        Assert.False(state.Sandbox);
    }

    [Theory]
    [InlineData(false, "Open", true, true, ComposerAvailability.NoPermission, "permiso")]
    [InlineData(true, "Closed", true, true, ComposerAvailability.ConversationClosed, "cerrada")]
    [InlineData(true, "Open", false, true, ComposerAvailability.ChannelInactive, "desactivado")]
    [InlineData(true, "Open", true, false, ComposerAvailability.CredentialsMissing, "clave de API")]
    [InlineData(false, "Closed", false, false, ComposerAvailability.NoPermission, "permiso")] // the caller's own limit comes first
    [InlineData(true, "Closed", false, false, ComposerAvailability.ConversationClosed, "cerrada")]
    [InlineData(true, "Open", false, false, ComposerAvailability.ChannelInactive, "desactivado")]
    public void Each_reason_not_to_write_is_named(bool canManage, string status, bool active, bool key, ComposerAvailability expected, string reason)
    {
        var state = ComposerState.For(canManage, status, Channel(active, key));

        Assert.Equal(expected, state.Availability);
        Assert.False(state.CanSend);
        Assert.Contains(reason, state.Reason);
        Assert.False(state.Sandbox);
    }

    [Fact]
    public void A_sandbox_line_is_flagged_only_while_the_composer_is_offered()
    {
        Assert.True(ComposerState.For(true, "Open", Channel(environment: "Sandbox")).Sandbox);
        Assert.False(ComposerState.For(true, "Closed", Channel(environment: "Sandbox")).Sandbox);
        Assert.False(ComposerState.For(true, "Open", Channel(key: false, environment: "Sandbox")).Sandbox);
    }

    [Fact]
    public void An_answer_without_the_state_of_the_line_does_not_hide_the_composer()
    {
        var state = ComposerState.For(canManage: true, "Open", channel: null);

        Assert.True(state.CanSend); // the API still refuses what it must, and the page words that refusal
        Assert.False(state.Sandbox);
    }

    [Fact]
    public void Before_the_page_knows_anything_the_composer_is_not_offered_and_gives_no_reason()
    {
        Assert.False(ComposerState.Unknown.CanSend);
        Assert.Null(ComposerState.Unknown.Reason);
    }

    // ---- labels ----

    [Theory]
    [InlineData("Sandbox", "Sandbox (pruebas)")]
    [InlineData("Production", "Producción")]
    [InlineData("Other", "Other")]
    [InlineData(null, "—")]
    public void The_environment_reads_in_spanish(string? name, string expected)
        => Assert.Equal(expected, MessagingLabels.Environment(name));

    // ---- the form models ----

    [Fact]
    public void The_text_a_browser_posts_counts_a_line_break_once_like_the_counter_does()
    {
        var input = new SendMessageInput { Text = "uno\r\ndos\r\ntres" };

        Assert.Equal("uno\ndos\ntres", input.Text);
        Assert.Equal(12, input.Length);
    }

    [Fact]
    public void The_request_carries_the_text_trimmed()
        => Assert.Equal("Hola", new SendMessageInput { Text = "  Hola \r\n" }.ToRequest().Text);

    [Fact]
    public void The_limit_of_the_composer_is_the_one_of_the_message()
        => Assert.Equal(Message.TextMaxLength, SendMessageInput.MaxLength);

    [Fact]
    public void The_credentials_form_sends_no_key_when_none_was_typed_and_never_prints_one()
    {
        const string key = "d360-dummy-key-Zx9";

        Assert.Null(new ProviderCredentialsInput { ApiKey = "   ", Environment = "Production" }.ToRequest().ApiKey);
        Assert.Equal(key, new ProviderCredentialsInput { ApiKey = $" {key} ", Environment = "Sandbox" }.ToRequest().ApiKey);
        Assert.DoesNotContain(key, new ProviderCredentialsInput { ApiKey = key, Environment = "Sandbox" }.ToString());
    }
}
