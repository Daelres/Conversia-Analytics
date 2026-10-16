var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health");

app.Run();

// Exposes the entry point to WebApplicationFactory in the integration tests.
public partial class Program;
