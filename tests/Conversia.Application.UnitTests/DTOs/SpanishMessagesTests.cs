using System.ComponentModel.DataAnnotations;
using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Auth;
using Conversia.Application.DTOs.Conversations;
using Conversia.Application.DTOs.Organization;
using Conversia.Application.DTOs.Users;
using Conversia.Application.Exceptions;
using Conversia.Domain.Enums;
using Conversia.TestSupport;

namespace Conversia.Application.UnitTests.DTOs;

/// <summary>
/// Requirement "all text shown to users is Spanish", Application layer: the exception messages (they become the <c>detail</c> of
/// the API's handled errors), every DataAnnotations message of every request DTO (they become the validation messages of a
/// 400). The fixed wording is pinned exactly; the rest only has to be Spanish.
/// </summary>
public class SpanishMessagesTests
{
    // ---- exceptions ----

    [Fact]
    public void The_handled_exceptions_carry_the_exact_spanish_messages_of_the_requirement()
    {
        Assert.Equal("Correo o contraseña incorrectos.", new InvalidCredentialsException().Message);
        Assert.Equal("Ese correo ya está registrado.", new DuplicateEmailException().Message);
        Assert.Equal("Usuario no encontrado.", new UserNotFoundException().Message);
        Assert.Equal("Sucursal no encontrada.", new BranchNotFoundException().Message);
        Assert.Equal("Ya existe una sucursal con ese nombre.", new DuplicateBranchNameException().Message);
    }

    [Fact]
    public void Every_exception_message_is_free_of_english()
    {
        foreach (var exception in new Exception[]
        {
            new InvalidCredentialsException(), new DuplicateEmailException(), new UserNotFoundException(),
            new BranchNotFoundException(), new DuplicateBranchNameException(),
        })
        {
            Assert.Empty(EnglishLeaks.Find(exception.Message));
            Assert.True(EnglishLeaks.LooksSpanish(exception.Message), exception.Message);
        }
    }

    // ---- DTO validation messages ----

    private static List<ValidationResult> Validate(object request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }

    private static string Long(int length) => new('x', length);

    public static TheoryData<string, Func<object>> InvalidRequests => new()
    {
        // register
        { "register: blank tenant name", () => new RegisterTenantRequest { TenantName = "", Email = "a@b.test", Password = "Sup3r-Secret!" } },
        { "register: tenant name too long", () => new RegisterTenantRequest { TenantName = Long(201), Email = "a@b.test", Password = "Sup3r-Secret!" } },
        { "register: blank email", () => new RegisterTenantRequest { TenantName = "t", Email = "", Password = "Sup3r-Secret!" } },
        { "register: malformed email", () => new RegisterTenantRequest { TenantName = "t", Email = "not-an-email", Password = "Sup3r-Secret!" } },
        { "register: email too long", () => new RegisterTenantRequest { TenantName = "t", Email = Long(244) + "@example.test", Password = "Sup3r-Secret!" } },
        { "register: blank password", () => new RegisterTenantRequest { TenantName = "t", Email = "a@b.test", Password = "" } },
        { "register: password too short", () => new RegisterTenantRequest { TenantName = "t", Email = "a@b.test", Password = "Abcdefghi1k" } },
        { "register: password without uppercase", () => new RegisterTenantRequest { TenantName = "t", Email = "a@b.test", Password = "abcdefghijk1" } },
        { "register: password without lowercase", () => new RegisterTenantRequest { TenantName = "t", Email = "a@b.test", Password = "ABCDEFGHIJK1" } },
        { "register: password without digit", () => new RegisterTenantRequest { TenantName = "t", Email = "a@b.test", Password = "Abcdefghijkl" } },
        { "register: password too long", () => new RegisterTenantRequest { TenantName = "t", Email = "a@b.test", Password = "Aa1" + Long(126) } },
        // login
        { "login: blank email", () => new LoginRequest { Email = "", Password = "x" } },
        { "login: malformed email", () => new LoginRequest { Email = "not-an-email", Password = "x" } },
        { "login: email too long", () => new LoginRequest { Email = Long(244) + "@example.test", Password = "x" } },
        { "login: blank password", () => new LoginRequest { Email = "a@b.test", Password = "" } },
        // create user
        { "create user: blank email", () => new CreateUserRequest { Email = "", Password = "Sup3r-Secret!" } },
        { "create user: malformed email", () => new CreateUserRequest { Email = "not-an-email", Password = "Sup3r-Secret!" } },
        { "create user: email too long", () => new CreateUserRequest { Email = Long(244) + "@example.test", Password = "Sup3r-Secret!" } },
        { "create user: blank password", () => new CreateUserRequest { Email = "a@b.test", Password = "" } },
        { "create user: weak password", () => new CreateUserRequest { Email = "a@b.test", Password = "abcdefghijk1" } },
        // branches
        { "create branch: blank name", () => new CreateBranchRequest { Name = " " } },
        { "create branch: name too long", () => new CreateBranchRequest { Name = Long(101) } },
        // conversation query
        { "query: page 0", () => new ConversationQuery { Page = 0 } },
        { "query: page size 0", () => new ConversationQuery { PageSize = 0 } },
        { "query: page size too large", () => new ConversationQuery { PageSize = ConversationQuery.MaxPageSize + 1 } },
        // permissions
        { "permission: undefined value", () => new AssignPermissionRequest { Permission = (Permission)999 } },
        { "permission: zero", () => new AssignPermissionRequest { Permission = 0 } },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void Every_validation_message_of_every_rule_is_spanish(string rule, Func<object> build)
    {
        var errors = Validate(build());

        Assert.True(errors.Count > 0, $"{rule}: the request was accepted, so there is no message to check.");
        foreach (var error in errors)
        {
            var message = error.ErrorMessage;
            Assert.False(string.IsNullOrWhiteSpace(message), $"{rule}: an error without a message.");
            Assert.True(EnglishLeaks.Find(message).Count == 0, $"{rule}: '{message}' contains English wording: {string.Join(", ", EnglishLeaks.Find(message))}.");
            Assert.True(EnglishLeaks.LooksSpanish(message), $"{rule}: '{message}' does not look like Spanish.");
        }
    }

    [Fact]
    public void A_weak_password_message_still_never_echoes_the_password()
    {
        const string weak = "hunter2-weak";

        var errors = Validate(new RegisterTenantRequest { TenantName = "t", Email = "a@b.test", Password = weak });

        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.DoesNotContain(weak, e.ErrorMessage));
        Assert.All(errors, e => Assert.Empty(EnglishLeaks.Find(e.ErrorMessage)));
    }
}
