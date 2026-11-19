using System.Text;
using System.Text.Json;
using Agent.IntegrationTests.Support;

namespace Agent.IntegrationTests;

/// <summary>
/// The committed docs/openapi.json is the public contract. This test fails when the generated document drifts from it.
/// Regenerate with: UPDATE_OPENAPI=1 dotnet test tests/Agent.IntegrationTests --filter "FullyQualifiedName~OpenApiSnapshot"
/// </summary>
public sealed class OpenApiSnapshotTests(DefaultApiFactory factory) : IClassFixture<DefaultApiFactory>
{
    [Fact]
    public async Task Generated_document_matches_the_committed_snapshot()
    {
        var response = await factory.CreateClient().GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var generated = Format(await response.Content.ReadAsStringAsync());

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RepoPaths.OpenApiSnapshot)!);
            await File.WriteAllTextAsync(RepoPaths.OpenApiSnapshot, generated, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(RepoPaths.OpenApiSnapshot), "docs/openapi.json is missing. Regenerate it with UPDATE_OPENAPI=1 (see the class comment).");
        var committed = (await File.ReadAllTextAsync(RepoPaths.OpenApiSnapshot)).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.True(
            committed == generated,
            "The generated OpenAPI document differs from docs/openapi.json. If the API change is intended, regenerate: "
            + "UPDATE_OPENAPI=1 dotnet test tests/Agent.IntegrationTests --filter \"FullyQualifiedName~OpenApiSnapshot\"");
    }

    [Fact]
    public async Task Document_describes_every_endpoint_and_both_security_schemes()
    {
        var response = await factory.CreateClient().GetAsync("/openapi/v1.json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");

        foreach (var path in new[]
        {
            "/api/v1/chat/start", "/api/v1/chat/message", "/api/v1/chat/history", "/api/v1/chat/session",
            "/api/v1/chat/forms/passenger", "/api/v1/chat/forms/guest", "/api/v1/bookings/{bookingId}/status",
            "/api/v1/lookups/countries", "/api/v1/tools",
        })
        {
            Assert.True(paths.TryGetProperty(path, out _), $"missing path {path}");
        }

        var schemes = document.RootElement.GetProperty("components").GetProperty("securitySchemes");
        Assert.True(schemes.TryGetProperty("ClientKey", out _));
        Assert.True(schemes.TryGetProperty("SessionToken", out _));
    }

    private static string Format(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }
}
