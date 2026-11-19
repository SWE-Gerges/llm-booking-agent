using Agent.Api;
using Agent.Core;
using Agent.Domains.Flights;
using Agent.Domains.Hotels;
using Agent.Domains.Shared;
using Agent.Domains.Transfers;
using Agent.Llm;
using Agent.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Chat payloads are small; refuse anything bigger before it is buffered.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);

builder.Services
    .AddAgentCore(builder.Configuration)
    .AddAgentLlm(builder.Configuration)
    .AddAgentPersistence(builder.Configuration)
    .AddSharedDomain(builder.Configuration)
    .AddFlightDomain(builder.Configuration)
    .AddHotelDomain()
    .AddTransferDomain()
    .AddAgentApi(builder.Configuration);

var app = builder.Build();

await app.Services.InitializeAgentDatabaseAsync();
app.UseAgentApi();

await app.RunAsync();

/// <summary>Entry point marker so integration tests can host the API with <c>WebApplicationFactory</c>.</summary>
public partial class Program;
