using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Xunit.Sdk;

namespace Conversia.IntegrationTests;

/// <summary>
/// One row of the <c>ErrorLogs</c> table, read with plain SQL. The tests assert on the table the requirement describes
/// (column names, values), not on how the implementation maps it, so a wrong column name is a failing test.
/// </summary>
internal sealed class ErrorLogRow
{
    private readonly Dictionary<string, object?> _values;

    public ErrorLogRow(Dictionary<string, object?> values) => _values = values;

    public Guid Id => Get<Guid>("Id");

    public DateTime CreatedAt => Get<DateTime>("CreatedAt");

    public string Code => Get<string>("Code");

    public DateTime OccurredAt => Get<DateTime>("OccurredAt");

    public int Source => Get<int>("Source");

    public int Kind => Get<int>("Kind");

    public string? Environment => GetOrNull<string>("Environment");

    public string? ServiceVersion => GetOrNull<string>("ServiceVersion");

    public string? Instance => GetOrNull<string>("Instance");

    public string? HttpMethod => GetOrNull<string>("HttpMethod");

    public string? Path => GetOrNull<string>("Path");

    public string? Query => GetOrNull<string>("Query");

    public string? Endpoint => GetOrNull<string>("Endpoint");

    public string? RouteValues => GetOrNull<string>("RouteValues");

    public int StatusCode => Get<int>("StatusCode");

    public string? TraceId => GetOrNull<string>("TraceId");

    public string? RequestId => GetOrNull<string>("RequestId");

    public Guid? UserId => GetOrNull<Guid?>("UserId");

    public Guid? TenantId => GetOrNull<Guid?>("TenantId");

    public string? UserAgent => GetOrNull<string>("UserAgent");

    public string ExceptionType => Get<string>("ExceptionType");

    public string? ExceptionMessage => GetOrNull<string>("ExceptionMessage");

    public string? ExceptionDetails => GetOrNull<string>("ExceptionDetails");

    public string? Fingerprint => GetOrNull<string>("Fingerprint");

    public string? Context => GetOrNull<string>("Context");

    /// <summary>Every column rendered to text, to prove a value is nowhere in the row.</summary>
    public IReadOnlyList<string> AllValues
        => _values.Select(v => Convert.ToString(v.Value, CultureInfo.InvariantCulture) ?? string.Empty).ToList();

    public void AssertNowhereInRow(params string[] forbidden)
    {
        foreach (var (column, value) in _values)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            foreach (var marker in forbidden)
            {
                Assert.False(text.Contains(marker, StringComparison.OrdinalIgnoreCase), $"'{marker}' was stored in column {column}: {text}");
            }
        }
    }

    private T Get<T>(string column)
    {
        if (!_values.TryGetValue(column, out var value))
        {
            throw new XunitException($"The ErrorLogs table has no column '{column}'. Columns: {string.Join(", ", _values.Keys)}.");
        }

        if (value is null)
        {
            throw new XunitException($"Column '{column}' of ErrorLogs is NULL where a value is required.");
        }

        return (T)value;
    }

    private T? GetOrNull<T>(string column)
    {
        if (!_values.TryGetValue(column, out var value))
        {
            throw new XunitException($"The ErrorLogs table has no column '{column}'. Columns: {string.Join(", ", _values.Keys)}.");
        }

        return value is null ? default : (T)value;
    }
}

/// <summary>A row planted directly in <c>ErrorLogs</c>, with every column of the requirement filled in.</summary>
internal sealed record SeedErrorRow(
    string Code,
    DateTime OccurredAt,
    string Path = "/seeded",
    string ExceptionType = "System.InvalidOperationException",
    string Fingerprint = "0123456789abcdef");

/// <summary>Reads, plants and counts <c>ErrorLogs</c> rows on whichever database a connection string points at.</summary>
internal static class ErrorLogDb
{
    public static async Task<List<ErrorLogRow>> QueryAsync(string connectionString, string where = "1 = 1", params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM \"ErrorLogs\" WHERE {where} ORDER BY \"OccurredAt\", \"Code\"";
        command.Parameters.AddRange(parameters);

        var rows = new List<ErrorLogRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                values[reader.GetName(i)] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
            }

            rows.Add(new ErrorLogRow(values));
        }

        return rows;
    }

    public static async Task<ErrorLogRow?> FindByCodeAsync(string connectionString, string code)
        => (await QueryAsync(connectionString, "\"Code\" = @code", new NpgsqlParameter("@code", code))).SingleOrDefault();

    /// <summary>The row stored for <paramref name="code"/>; fails the test (with the table's contents for that code) when there is none.</summary>
    public static async Task<ErrorLogRow> RequireByCodeAsync(string connectionString, string code)
    {
        var row = await FindByCodeAsync(connectionString, code);
        Assert.True(row is not null, $"No ErrorLogs row was stored for the code {code} that the response announced.");
        return row!;
    }

    public static Task<List<ErrorLogRow>> ByUserAgentAsync(string connectionString, string userAgent)
        => QueryAsync(connectionString, "\"UserAgent\" = @ua", new NpgsqlParameter("@ua", userAgent));

    public static Task<int> CountAsync(string connectionString, string where = "1 = 1", params NpgsqlParameter[] parameters)
        => ScalarAsync<int>(connectionString, $"SELECT COUNT(*) FROM \"ErrorLogs\" WHERE {where}", parameters);

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T), CultureInfo.InvariantCulture);
    }

    public static async Task ExecuteAsync(string connectionString, string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    public static Task DeleteAllAsync(string connectionString) => ExecuteAsync(connectionString, "DELETE FROM \"ErrorLogs\"");

    private static readonly string[] InsertColumnNames =
    {
        "Id", "CreatedAt", "Code", "OccurredAt", "Source", "Kind", "Environment", "ServiceVersion", "Instance", "HttpMethod", "Path", "Query",
        "Endpoint", "RouteValues", "StatusCode", "TraceId", "RequestId", "UserId", "TenantId", "UserAgent", "ExceptionType", "ExceptionMessage",
        "ExceptionDetails", "Fingerprint", "Context",
    };

    private static readonly string InsertColumns = string.Join(", ", InsertColumnNames.Select(name => $"\"{name}\""));

    /// <summary>Plants one row with plain SQL (independent of the registry), e.g. to occupy a code or to age a row.</summary>
    public static Task InsertAsync(string connectionString, SeedErrorRow row)
        => ExecuteAsync(
            connectionString,
            $"INSERT INTO \"ErrorLogs\" ({InsertColumns}) VALUES (gen_random_uuid(), now(), @code, @occurredAt, 1, 1, 'Test', '0.0.0', 'seed', 'GET', @path, '', '', '', "
            + "500, '', '', NULL, NULL, 'seed-agent', @type, 'seeded message', 'seeded details', @fingerprint, '')",
            new NpgsqlParameter("@code", row.Code),
            new NpgsqlParameter("@occurredAt", NpgsqlDbType.TimestampTz) { Value = row.OccurredAt },
            new NpgsqlParameter("@path", row.Path),
            new NpgsqlParameter("@type", row.ExceptionType),
            new NpgsqlParameter("@fingerprint", row.Fingerprint));

    /// <summary>Plants <paramref name="count"/> rows (codes <c>ERR-{datePart}-000001</c>...) in one statement.</summary>
    public static Task InsertManyAsync(string connectionString, int count, DateTime occurredAt, string datePart)
        => ExecuteAsync(
            connectionString,
            $"INSERT INTO \"ErrorLogs\" ({InsertColumns}) "
            + "SELECT gen_random_uuid(), now(), 'ERR-' || @datePart || '-' || lpad(i::text, 6, '0'), @occurredAt, 1, 1, 'Test', '0.0.0', 'seed', 'GET', '/bulk', '', '', '', "
            + "500, '', '', NULL, NULL, 'seed-agent', 'System.Exception', 'bulk', 'bulk', 'aaaaaaaaaaaaaaaa', '' FROM generate_series(1, @count) AS i",
            new NpgsqlParameter("@count", count),
            new NpgsqlParameter("@datePart", datePart),
            new NpgsqlParameter("@occurredAt", NpgsqlDbType.TimestampTz) { Value = occurredAt });
}
