using Conversia.Domain.Common;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Conversia.IntegrationTests;

/// <summary>The <c>ErrorLogs</c> table: columns, limits, and the indexes.</summary>
[Collection(nameof(IntegrationTestCollection))]
public class ErrorLogsSchemaTests
{
    private readonly CustomWebApplicationFactory _shared;
    private readonly string _connectionString;

    public ErrorLogsSchemaTests(CustomWebApplicationFactory shared)
    {
        _shared = shared;
        _connectionString = shared.ServerConnectionString;
    }

    private sealed record Column(string Name, string Type, int Length, bool Nullable);

    private async Task<Dictionary<string, Column>> ColumnsAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT column_name, data_type, COALESCE(character_maximum_length, 0), is_nullable FROM information_schema.columns WHERE table_name = 'ErrorLogs'";
        var columns = new Dictionary<string, Column>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns[reader.GetString(0)] = new Column(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3) == "YES");
        }

        return columns;
    }

    [Fact]
    public async Task The_table_has_every_column_with_the_documented_type_and_limit()
    {
        var columns = await ColumnsAsync();
        Assert.NotEmpty(columns); // the table exists

        foreach (var name in new[] { "Id", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy" })
        {
            Assert.True(columns.ContainsKey(name), $"BaseEntity column {name} is missing.");
        }

        foreach (var (name, length) in ErrorContract.TextColumns)
        {
            Assert.True(columns.TryGetValue(name, out var column), $"Column {name} is missing.");
            Assert.Equal("character varying", column!.Type);
            Assert.Equal(length, column.Length);
        }

        Assert.Equal("text", columns["ExceptionDetails"].Type); // unbounded
        Assert.Equal("character", columns["Fingerprint"].Type);
        Assert.Equal(16, columns["Fingerprint"].Length);
        Assert.Equal("timestamp with time zone", columns["OccurredAt"].Type);
        foreach (var name in new[] { "Source", "Kind", "StatusCode" })
        {
            Assert.Equal("integer", columns[name].Type);
        }

        foreach (var name in new[] { "Id", "UserId", "TenantId" })
        {
            Assert.Equal("uuid", columns[name].Type);
        }

        Assert.True(columns["UserId"].Nullable);
        Assert.True(columns["TenantId"].Nullable);
        foreach (var name in new[] { "Id", "Code", "OccurredAt", "Source", "Kind", "ExceptionType" })
        {
            Assert.False(columns[name].Nullable, $"{name} must be required.");
        }
    }

    [Fact]
    public async Task The_indexes_are_a_unique_one_on_Code_one_on_OccurredAt_and_a_composite_one_on_Fingerprint_and_OccurredAt()
    {
        var indexes = new List<(string Name, bool Unique, bool Primary, string Columns)>();
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT i.relname, ix.indisunique, ix.indisprimary, string_agg(a.attname, ',' ORDER BY k.ord) "
                + "FROM pg_index ix JOIN pg_class i ON i.oid = ix.indexrelid JOIN pg_class t ON t.oid = ix.indrelid "
                + "CROSS JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ord) "
                + "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum "
                + "WHERE t.relname = 'ErrorLogs' AND t.relnamespace = 'public'::regnamespace GROUP BY i.relname, ix.indisunique, ix.indisprimary";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                indexes.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetString(3)));
            }
        }

        Assert.NotEmpty(indexes);
        Assert.Contains(indexes, i => i.Primary && i.Columns == "Id");
        Assert.Contains(indexes, i => !i.Primary && i.Unique && i.Columns == "Code");
        Assert.Contains(indexes, i => !i.Primary && !i.Unique && i.Columns == "OccurredAt");
        Assert.Contains(indexes, i => !i.Primary && !i.Unique && i.Columns == "Fingerprint,OccurredAt");
    }

    [Fact]
    public void The_table_is_an_operations_table_a_base_entity_that_no_tenant_filter_applies_to()
    {
        using var scope = _shared.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Model;

        var errorLog = Assert.Single(model.GetEntityTypes(), t => t.GetTableName() == "ErrorLogs");

        Assert.True(typeof(BaseEntity).IsAssignableFrom(errorLog.ClrType), "ErrorLog must derive from BaseEntity like every entity.");
        Assert.Empty(errorLog.GetDeclaredQueryFilters()); // rows of every tenant are visible to operations
        Assert.NotNull(errorLog.FindProperty("TenantId")); // while the tenant of the failing request is still recorded
        Assert.NotNull(errorLog.FindProperty("UserId"));
    }

    [Fact]
    public async Task The_database_itself_refuses_a_duplicate_code_and_an_oversized_one()
    {
        var code = ErrorContract.NewCode();
        await ErrorLogDb.InsertAsync(_connectionString, new SeedErrorRow(code, DateTime.UtcNow, Path: "/schema-first"));
        Assert.Equal("/schema-first", (await ErrorLogDb.RequireByCodeAsync(_connectionString, code)).Path); // precondition: the first insert worked

        var duplicate = await Assert.ThrowsAsync<PostgresException>(
            () => ErrorLogDb.InsertAsync(_connectionString, new SeedErrorRow(code, DateTime.UtcNow, Path: "/schema-second")));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

        var oversized = await Assert.ThrowsAsync<PostgresException>(
            () => ErrorLogDb.InsertAsync(_connectionString, new SeedErrorRow(ErrorContract.NewCode() + new string('X', 20), DateTime.UtcNow)));
        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, oversized.SqlState);
        Assert.Equal(1, await ErrorLogDb.CountAsync(_connectionString, "\"Code\" = @code", new NpgsqlParameter("@code", code)));
    }
}
