# Conversia

SaaS platform for SMBs that captures and analyzes WhatsApp sales conversations and turns them into metrics and executive dashboards. Working name, provisional.

This repository currently contains only the project scaffold (Clean Architecture, .NET 10, PostgreSQL). See `CLAUDE.md` for product context and `docs/decisiones.md` for the decision log.

## Prerequisites

- .NET SDK 10.0.300 or later in the 10.0.x feature band (see `global.json`)
- Docker (for the local PostgreSQL)

## Quick start

```bash
cp .env.example .env          # then set a real POSTGRES_PASSWORD
docker compose up -d          # PostgreSQL on 127.0.0.1:5432
dotnet build
dotnet test
dotnet run --project src/Conversia.Api
```

Health check: `GET /health` (see `src/Conversia.Api/Properties/launchSettings.json` for the local URL).

## Configuration

Secrets and connection strings are never stored in the repository. Provide them through environment variables, for example:

```bash
export ConnectionStrings__Default="Host=localhost;Port=5432;Database=conversia;Username=conversia;Password=<your password>"
```

## Layout

- `src/Conversia.Domain`: entities and domain rules, no dependencies.
- `src/Conversia.Application`: use cases, depends on Domain.
- `src/Conversia.Infrastructure`: persistence (EF Core + Npgsql) and external services, depends on Application.
- `src/Conversia.Api`: ASP.NET Core minimal API, depends on Application and Infrastructure.
- `tests/Conversia.UnitTests`: unit tests (xUnit).
- `tests/Conversia.IntegrationTests`: API integration tests (xUnit + WebApplicationFactory).
