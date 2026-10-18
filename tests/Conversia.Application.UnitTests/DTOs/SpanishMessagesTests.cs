using System.ComponentModel.DataAnnotations;
using Conversia.Application.Demo;
using Conversia.Application.DTOs.Auth;
using Conversia.Application.DTOs.Products;
using Conversia.Application.DTOs.Users;
using Conversia.Application.Exceptions;
using Conversia.Domain.Enums;
using Conversia.TestSupport;

namespace Conversia.Application.UnitTests.DTOs;

/// <summary>
/// Requirement "all text shown to users is Spanish", Application layer: the exception messages (they become the <c>detail</c> of
/// the API's handled errors), every DataAnnotations message of every request DTO (they become the validation messages of a
/// 400) and the demo account texts the Web displays. The fixed wording is pinned exactly; the rest only has to be Spanish.
/// </summary>
public class SpanishMessagesTests
{
    // ---- exceptions ----

    [Fact]
    public void The_handled_exceptions_carry_the_exact_spanish_messages_of_the_requirement()
    {
        Assert.Equal("Correo o contraseña incorrectos.", new InvalidCredentialsException().Message);
        Assert.Equal("Ese correo ya está registrado.", new DuplicateEmailException().Message);
        Assert.Equal("Producto no encontrado.", new ProductNotFoundException().Message);
        Assert.Equal("Usuario no encontrado.", new UserNotFoundException().Message);
        Assert.Equal("Ya existe un producto con ese SKU.", new DuplicateSkuException().Message);
        Assert.Equal("El producto fue modificado por otra persona. Recarga e inténtalo de nuevo.", new ProductConcurrencyException().Message);
    }

    [Fact]
    public void Every_exception_message_is_free_of_english()
    {
        foreach (var exception in new Exception[]
        {
            new InvalidCredentialsException(), new DuplicateEmailException(), new ProductNotFoundException(),
            new UserNotFoundException(), new DuplicateSkuException(), new ProductConcurrencyException(),
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
        // products
        { "create product: blank name", () => new CreateProductRequest { Name = " ", Sku = "S-1", Price = 1m, Stock = 1 } },
        { "create product: name too long", () => new CreateProductRequest { Name = Long(201), Sku = "S-1", Price = 1m, Stock = 1 } },
        { "create product: blank sku", () => new CreateProductRequest { Name = "n", Sku = "", Price = 1m, Stock = 1 } },
        { "create product: invalid sku", () => new CreateProductRequest { Name = "n", Sku = "has space", Price = 1m, Stock = 1 } },
        { "create product: sku too long", () => new CreateProductRequest { Name = "n", Sku = Long(51), Price = 1m, Stock = 1 } },
        { "create product: negative price", () => new CreateProductRequest { Name = "n", Sku = "S-1", Price = -1m, Stock = 1 } },
        { "create product: price too large", () => new CreateProductRequest { Name = "n", Sku = "S-1", Price = 1_000_000_000m, Stock = 1 } },
        { "create product: three decimals", () => new CreateProductRequest { Name = "n", Sku = "S-1", Price = 1.234m, Stock = 1 } },
        { "create product: negative stock", () => new CreateProductRequest { Name = "n", Sku = "S-1", Price = 1m, Stock = -1 } },
        { "update product: blank name", () => new UpdateProductRequest { Name = "", Price = 1m, Stock = 1, RowVersion = "AAAAAAAAB9E=" } },
        { "update product: negative price", () => new UpdateProductRequest { Name = "n", Price = -1m, Stock = 1, RowVersion = "AAAAAAAAB9E=" } },
        { "update product: negative stock", () => new UpdateProductRequest { Name = "n", Price = 1m, Stock = -1, RowVersion = "AAAAAAAAB9E=" } },
        { "update product: blank row version", () => new UpdateProductRequest { Name = "n", Price = 1m, Stock = 1, RowVersion = "" } },
        { "update product: row version not base64", () => new UpdateProductRequest { Name = "n", Price = 1m, Stock = 1, RowVersion = "not base64!" } },
        // query
        { "query: page 0", () => new ProductQuery { Page = 0 } },
        { "query: page size 0", () => new ProductQuery { PageSize = 0 } },
        { "query: page size too large", () => new ProductQuery { PageSize = ProductQuery.MaxPageSize + 1 } },
        { "query: search too long", () => new ProductQuery { Search = Long(ProductQuery.MaxSearchLength + 1) } },
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

    // ---- demo accounts ----

    [Fact]
    public void The_demo_account_labels_and_descriptions_the_web_shows_are_spanish()
    {
        Assert.Equal(5, DemoAccounts.All.Count);
        foreach (var account in DemoAccounts.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(account.SignInLabel), account.Email);
            Assert.False(string.IsNullOrWhiteSpace(account.Description), account.Email);
            Assert.True(EnglishLeaks.Find(account.SignInLabel).Count == 0, $"{account.Email}: label '{account.SignInLabel}' is English.");
            Assert.True(EnglishLeaks.Find(account.Description).Count == 0, $"{account.Email}: description '{account.Description}' is English: {string.Join(", ", EnglishLeaks.Find(account.Description))}.");
            Assert.True(EnglishLeaks.LooksSpanish(account.Description), $"{account.Email}: description '{account.Description}' does not look like Spanish.");
        }

        Assert.Equal(5, DemoAccounts.All.Select(a => a.SignInLabel).Distinct().Count()); // each shortcut button names a different account
    }
}
