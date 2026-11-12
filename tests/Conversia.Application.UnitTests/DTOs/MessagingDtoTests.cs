using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.DTOs.Organization;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Application.UnitTests.DTOs;

/// <summary>
/// The requests of the messaging endpoints agree with the domain rules, and the provider API key is write-only: it goes in through one
/// request and no response type can carry it or its ciphertext back.
/// </summary>
public class MessagingDtoTests
{
    private static List<ValidationResult> Validate(object request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }

    private static string[] Members(object request) => Validate(request).SelectMany(r => r.MemberNames).ToArray();

    // ---- SendMessageRequest --------------------------------------------------------------------

    [Theory]
    [InlineData("Hola", true)]
    [InlineData("  Hola  ", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("nul\0inside", false)]
    public void SendMessage_TextIsRequiredAndFollowsTheTextRule(string? text, bool valid)
        => Assert.Equal(valid, Validate(new SendMessageRequest { Text = text }).Count == 0);

    [Fact]
    public void SendMessage_TextLimitIsTheMessageLimit_AfterTrimming()
    {
        Assert.Empty(Validate(new SendMessageRequest { Text = " " + new string('x', Message.TextMaxLength) + " " }));
        var error = Assert.Single(Validate(new SendMessageRequest { Text = new string('x', Message.TextMaxLength + 1) }));
        Assert.Contains("Texto", error.ErrorMessage);
        Assert.Contains(Message.TextMaxLength.ToString(), error.ErrorMessage);
    }

    // ---- SetProviderCredentialsRequest ---------------------------------------------------------

    [Theory]
    [InlineData("abc-123", "Sandbox", true)]
    [InlineData("abc-123", "production", true)]
    [InlineData(null, "Sandbox", true)] // keeps the stored key; the service refuses it when there is none
    [InlineData("", "Sandbox", true)]
    [InlineData("abc-123", null, false)]
    [InlineData("abc-123", "Staging", false)]
    [InlineData("abc-123", "1", false)]
    [InlineData("two words", "Sandbox", false)]
    [InlineData("line\nbreak", "Sandbox", false)]
    public void SetProviderCredentials_FollowsTheDomainRules(string? apiKey, string? environment, bool valid)
        => Assert.Equal(valid, Validate(new SetProviderCredentialsRequest { ApiKey = apiKey, Environment = environment }).Count == 0);

    [Fact]
    public void SetProviderCredentials_ErrorsNameTheField_AndNeverRepeatTheKey()
    {
        const string key = "two words-SENSITIVE";

        var results = Validate(new SetProviderCredentialsRequest { ApiKey = key, Environment = "Sandbox" });

        var error = Assert.Single(results);
        Assert.Contains(nameof(SetProviderCredentialsRequest.ApiKey), error.MemberNames);
        Assert.DoesNotContain("SENSITIVE", error.ErrorMessage);
        Assert.Contains(nameof(SetProviderCredentialsRequest.Environment), Members(new SetProviderCredentialsRequest { ApiKey = "abc" }));
    }

    [Fact]
    public void SetProviderCredentials_DoesNotPrintTheKey()
        => Assert.DoesNotContain("abc-123", new SetProviderCredentialsRequest { ApiKey = "abc-123", Environment = "Sandbox" }.ToString());

    // ---- The key is write-only -----------------------------------------------------------------

    private static readonly Type[] RequestsThatTakeAKey = [typeof(SetProviderCredentialsRequest)];

    public static TheoryData<Type> DtoTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var type in typeof(ChannelResponse).Assembly.GetTypes()
                     .Where(t => t.IsClass && t.Namespace is not null && t.Namespace.StartsWith("Conversia.Application.DTOs", StringComparison.Ordinal))
                     .Where(t => !RequestsThatTakeAKey.Contains(t)))
        {
            data.Add(type);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DtoTypes))]
    public void NoDtoBesidesTheCredentialsRequest_CanCarryAProviderKeyOrItsCiphertext(Type dto)
    {
        // "Has a key" and "when it was set" are the only facts about the key a response may state.
        var carriers = dto.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(p => p.Name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Ciphertext", StringComparison.OrdinalIgnoreCase))
            .Where(p => p.PropertyType != typeof(bool) && p.PropertyType != typeof(DateTime?) && p.PropertyType != typeof(DateTime))
            .Select(p => $"{dto.Name}.{p.Name}")
            .ToList();

        Assert.Empty(carriers);
    }

    [Fact]
    public void TheChannelResponses_StateThatAKeyExists_WithoutCarryingIt()
    {
        const string ciphertext = "v1.1.c2VjcmV0LWNpcGhlcnRleHQ";
        var setAt = new DateTime(2026, 11, 9, 15, 0, 0, DateTimeKind.Utc);
        var (channel, secret) = WhatsAppChannel.Create(Guid.NewGuid(), Guid.NewGuid(), "3001234567");
        channel.SetProviderCredentials(ciphertext, WhatsAppProviderEnvironment.Production, setAt);

        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var response in new[] { ChannelResponse.From(channel), ChannelResponse.From(channel).WithoutWebhookAddress(), CreateChannelResponse.From(channel, secret) })
        {
            Assert.True(response.HasProviderApiKey);
            Assert.Equal(setAt, response.ProviderApiKeySetAt);
            Assert.Equal(nameof(WhatsAppProviderEnvironment.Production), response.ProviderEnvironment);
            Assert.DoesNotContain(ciphertext, JsonSerializer.Serialize(response, response.GetType(), web));
        }
    }

    [Fact]
    public void AChannelWithoutAKey_SaysSo()
    {
        var (channel, _) = WhatsAppChannel.Create(Guid.NewGuid(), Guid.NewGuid(), "3001234567");

        var response = ChannelResponse.From(channel);

        Assert.False(response.HasProviderApiKey);
        Assert.Null(response.ProviderApiKeySetAt);
        Assert.Equal(nameof(WhatsAppProviderEnvironment.Sandbox), response.ProviderEnvironment);
    }
}
