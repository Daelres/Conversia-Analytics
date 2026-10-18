using Conversia.Domain.Common;
using Conversia.Web.Authentication;
using Conversia.Web.Components;
using Conversia.Web.Dashboard;
using Conversia.Web.Demo;
using Conversia.Web.ErrorReporting;
using Conversia.Web.Endpoints;
using Conversia.Web.Http;
using Conversia.Web.Services;
using Conversia.Web.Time;
using Microsoft.AspNetCore.Localization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents();
builder.Services.AddWebAuthentication();
builder.Services.AddApiClient();
builder.Services.AddWebErrorReporting();
builder.Services.AddDemoAccounts(builder.Configuration, builder.Environment);
builder.Services.AddScoped<ShellState>();
builder.Services.AddTenantTimeZone(builder.Configuration);
builder.Services.AddScoped<DashboardLoader>();

var app = builder.Build();

// The interface is Spanish and the culture is fixed: no Accept-Language, cookie or query string selects another one.
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(SpanishCulture.Info),
    SupportedCultures = [SpanishCulture.Info],
    SupportedUICultures = [SpanishCulture.Info],
    RequestCultureProviders = [],
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/status/{0}", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseNoStoreForDynamicResponses();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseRejectedFormPage();

app.MapStaticAssets();
app.MapLogout();
app.MapPermissionEndpoints();
app.MapRazorComponents<App>();

app.Run();
