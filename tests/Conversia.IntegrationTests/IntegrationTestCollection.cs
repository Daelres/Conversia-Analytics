namespace Conversia.IntegrationTests;

/// <summary>
/// Shares one Testcontainers PostgreSQL instance (via <see cref="CustomWebApplicationFactory"/>)
/// across every test class in this assembly — the container is spun up once
/// per test run and torn down automatically at the end, per docs/ARCHITECTURE.md.
/// </summary>
[CollectionDefinition(nameof(IntegrationTestCollection))]
public class IntegrationTestCollection : ICollectionFixture<CustomWebApplicationFactory>
{
}
