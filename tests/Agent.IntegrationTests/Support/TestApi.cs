using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Agent.IntegrationTests.Support;

/// <summary>Thin helper over the in-memory client so tests read like the HTTP walkthrough in requests/chat.http.</summary>
public sealed class TestApi(HttpClient http)
{
    public HttpClient Http { get; } = http;

    public async Task<(HttpResponseMessage Response, string Token)> StartAsync(object? body = null, string? clientKey = ApiFactory.ClientKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/start")
        {
            Content = JsonContent.Create(body ?? new { }),
        };
        if (clientKey is not null)
        {
            request.Headers.Add("X-Client-Key", clientKey);
        }

        var response = await Http.SendAsync(request);
        var token = string.Empty;
        if (response.IsSuccessStatusCode)
        {
            using var json = await ReadJsonAsync(response);
            token = json.RootElement.GetProperty("sessionToken").GetString()!;
        }

        return (response, token);
    }

    public async Task<string> StartSessionAsync(object? body = null)
    {
        var (response, token) = await StartAsync(body);
        response.EnsureSuccessStatusCode();
        return token;
    }

    public Task<HttpResponseMessage> SendAsync(string token, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return Http.SendAsync(request);
    }

    public Task<HttpResponseMessage> MessageAsync(string token, string message) =>
        SendAsync(token, HttpMethod.Post, "/api/v1/chat/message", new { message });

    /// <summary>Sends a message, asserts success, and returns the parsed reply.</summary>
    public async Task<JsonDocument> ChatAsync(string token, string message)
    {
        var response = await MessageAsync(token, message);
        Assert.True(response.IsSuccessStatusCode, $"POST message returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await ReadJsonAsync(response);
    }

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    public static string Reply(JsonDocument json) => json.RootElement.GetProperty("reply").GetString()!;

    public static string State(JsonDocument json) => json.RootElement.GetProperty("state").GetString()!;
}

public static class Payloads
{
    public static object Passenger(string first = "Jane", string last = "Doe") => new
    {
        passengers = new[]
        {
            new { firstName = first, lastName = last, dateOfBirth = "1990-05-01", gender = "female", nationality = "NL", passportNumber = "P1234567", passportExpiry = "2040-01-01" },
        },
        contact = new { email = "user@example.com", phone = "+15555550100" },
    };

    public static object Trip() => new
    {
        travellers = new[]
        {
            new { firstName = "Jane", lastName = "Doe", dateOfBirth = "1990-05-01", gender = "female", nationality = "NL", passportNumber = "P1234567", passportExpiry = "2040-01-01" },
            new { firstName = "John", lastName = "Doe", dateOfBirth = "1988-03-12", gender = "male", nationality = "NL", passportNumber = "P7654321", passportExpiry = "2041-02-02" },
        },
        contact = new { email = "user@example.com", phone = "+15555550100" },
        specialRequests = "Quiet seats please",
    };

    public static object Guest(string first = "Jane", string last = "Doe") => new
    {
        leadGuest = new { firstName = first, lastName = last, nationality = "NL" },
        contact = new { email = "user@example.com", phone = "+15555550100" },
        specialRequests = "High floor please",
    };
}
