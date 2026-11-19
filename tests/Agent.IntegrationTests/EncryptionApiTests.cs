using System.Net;
using System.Text.Json;
using Agent.IntegrationTests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.IntegrationTests;

public sealed class EncryptionApiTests
{
    private const string Script = """
        { "steps": [
          { "toolUse": { "name": "search_flights", "input": { "origin": "AMS", "destination": "LIS", "departureDate": "{{today+20}}", "adults": 1 } } },
          { "text": "found" },
          { "toolUse": { "name": "recheck_flight_price", "input": { "offerId": "{{search_flights.offers[0].id}}" } } },
          { "text": "confirmed" },
          { "toolUse": { "name": "collect_passenger_details", "input": {} } },
          { "text": "fill in the form" }
        ] }
        """;

    [Fact]
    public async Task The_flow_works_end_to_end_with_encryption_on_and_nothing_readable_lands_in_the_database()
    {
        using var factory = new ApiFactory(Script);
        var api = new TestApi(factory.CreateClient());
        var token = await api.StartSessionAsync();
        using (await api.ChatAsync(token, "flights")) { }
        using (await api.ChatAsync(token, "price")) { }
        using (await api.ChatAsync(token, "form")) { }

        var accepted = await api.SendAsync(token, HttpMethod.Post, "/api/v1/chat/forms/passenger", Payloads.Passenger("Zebediah", "Quillfeather"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var rows = ReadRawContextColumn(factory);
        Assert.NotEmpty(rows);
        Assert.All(rows, raw =>
        {
            Assert.DoesNotContain("Zebediah", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("Quillfeather", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("user@example.com", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("+15555550100", raw, StringComparison.Ordinal);
            Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(raw));
        });
    }

    [Fact]
    public async Task A_tampered_row_makes_reads_fail_closed_with_a_generic_500()
    {
        using var factory = new ApiFactory(Script);
        var api = new TestApi(factory.CreateClient());
        var token = await api.StartSessionAsync();
        using (await api.ChatAsync(token, "flights")) { }
        using (await api.ChatAsync(token, "price")) { }
        using (await api.ChatAsync(token, "form")) { }

        WriteRawContextColumn(factory, "tampered-not-ciphertext");

        var form = await api.SendAsync(token, HttpMethod.Post, "/api/v1/chat/forms/passenger", Payloads.Passenger("Zebediah", "Quillfeather"));
        var status = await api.SendAsync(token, HttpMethod.Get, "/api/v1/bookings/BK-0000000000/status");

        foreach (var response in new[] { form, status })
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("tampered", body, StringComparison.Ordinal);
            Assert.DoesNotContain("decrypt", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Zebediah", body, StringComparison.Ordinal);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(500, json.RootElement.GetProperty("status").GetInt32());
        }
    }

    private static string ConnectionString(ApiFactory factory) =>
        factory.Services.GetRequiredService<IConfiguration>()["ConnectionStrings:Sqlite"]!.Replace("Default Timeout=30", "Pooling=False", StringComparison.Ordinal);

    private static List<string> ReadRawContextColumn(ApiFactory factory)
    {
        using var connection = new SqliteConnection(ConnectionString(factory));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DataJson FROM booking_contexts";
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static void WriteRawContextColumn(ApiFactory factory, string value)
    {
        using var connection = new SqliteConnection(ConnectionString(factory));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE booking_contexts SET DataJson = $value";
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }
}
