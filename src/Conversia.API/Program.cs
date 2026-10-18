using System.Text;
using Conversia.API.Configuration;
using Conversia.API.Localization;
using Conversia.API.Middleware;
using Conversia.API.RateLimiting;
using Conversia.Application.Interfaces;
using Conversia.Application.Services;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Authorization;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Persistence;
using Conversia.Infrastructure.Repositories;
using Conversia.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers().AddSpanishMessages();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddHttpContextAccessor();

builder.Services.AddLoginRateLimiting(builder.Configuration);
builder.Services.AddErrorLogging(builder.Configuration);
builder.Services.AddWebhookIngestion(builder.Configuration);
builder.Services.AddConversations(builder.Configuration);
builder.Services.AddOptionalForwardedHeaders(builder.Configuration);
builder.Services.AddDemoData(builder.Configuration, builder.Environment);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<ITenantRepository, TenantRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IUserPermissionRepository, UserPermissionRepository>();
builder.Services.AddScoped<IProductRepository, ProductRepository>();
builder.Services.AddScoped<IBranchRepository, BranchRepository>();
builder.Services.AddScoped<IAdvisorRepository, AdvisorRepository>();
builder.Services.AddScoped<IWhatsAppChannelRepository, WhatsAppChannelRepository>();
builder.Services.AddScoped<IPasswordHasherService, PasswordHasherService>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IPermissionService, PermissionService>();
builder.Services.AddScoped<IUserManagementService, UserManagementService>();
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<IOrganizationService, OrganizationService>();
// One instance per scope under both interfaces: the tenant a webhook request or the worker sets is the one the DbContext filters by.
builder.Services.AddScoped<CurrentTenantService>();
builder.Services.AddScoped<ICurrentTenantService>(sp => sp.GetRequiredService<CurrentTenantService>());
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<CurrentTenantService>());
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
jwtOptions.SigningKey = JwtSigningKeyValidator.GetValidatedSigningKey(builder.Configuration);
builder.Services.AddSingleton(Options.Create(jwtOptions));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Without this, the JWT bearer handler remaps the "sub" claim to the
        // legacy ClaimTypes.NameIdentifier URI on authentication, breaking
        // every JwtRegisteredClaimNames.Sub lookup downstream (controllers,
        // PermissionAuthorizationHandler) even though the token itself is
        // valid — a well-known ASP.NET Core JWT bearer gotcha.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey))
        };
    });

// One policy per Permission enum value, named after the enum
// (docs/USERS_TENANTS_PERMISSIONS.md, "Authorization").
builder.Services.AddAuthorization(options =>
{
    foreach (var permission in Enum.GetValues<Permission>())
    {
        options.AddPolicy(permission.ToString(), policy =>
            policy.Requirements.Add(new PermissionRequirement(permission)));
    }
});

var app = builder.Build();

// Must be the first middleware so it wraps the entire pipeline — nothing
// unhandled should ever reach the caller as a raw 500 (docs/WORKFLOW.md).
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseSpanishCulture();

// Before everything that reads the client address (the rate limiters); does nothing unless proxies are configured.
app.UseOptionalForwardedHeaders();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// Before authentication and model binding so throttled logins cost no DB or hashing work.
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// Anonymous and minimal on purpose: the status word only (Healthy, Degraded or Unhealthy), nothing about tenants, events or errors.
app.MapHealthChecks("/health");

app.MapControllers();

app.Run();

// Exposed so WebApplicationFactory<Program> in the integration test project
// can boot this API in-process (see docs/WORKFLOW.md, Role A test rules).
public partial class Program
{
}
