using System.Security.Cryptography;
using Agent.Abstractions.Errors;
using Agent.Abstractions.Ports;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agent.Persistence.Security;

public sealed class FormDataProtectionOptions
{
    public const string SectionName = "DataProtection";

    /// <summary>
    /// Folder that holds the Data Protection key ring. Required outside Development. Mount a persistent volume
    /// here: keys lost with the container make every stored booking context unreadable (reads fail closed).
    /// </summary>
    public string? KeyRingPath { get; set; }
}

internal sealed class FormDataProtectionOptionsValidator(IHostEnvironment environment) : IValidateOptions<FormDataProtectionOptions>
{
    public ValidateOptionsResult Validate(string? name, FormDataProtectionOptions options) =>
        environment.IsProduction() && string.IsNullOrWhiteSpace(options.KeyRingPath)
            ? ValidateOptionsResult.Fail("DataProtection:KeyRingPath is required in Production so the key ring survives restarts.")
            : ValidateOptionsResult.Success;
}

/// <summary>
/// <see cref="IFormDataProtector"/> over ASP.NET Core Data Protection. The purpose string is versioned: a future
/// format change uses a new purpose and keeps the old one readable for migration.
/// </summary>
internal sealed class DataProtectionFormDataProtector : IFormDataProtector
{
    public const string Purpose = "Agent.Persistence.BookingContext.v1";

    private readonly IDataProtector _protector;

    public DataProtectionFormDataProtector(IDataProtectionProvider provider) => _protector = provider.CreateProtector(Purpose);

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue)
    {
        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // The message deliberately says nothing about the cause or the content.
            throw new FormDataProtectionException("Stored data could not be decrypted.", ex);
        }
    }
}

internal static class DataProtectionSetup
{
    public const string ApplicationName = "llm-booking-agent";

    public static IServiceCollection AddAgentDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FormDataProtectionOptions>()
            .Bind(configuration.GetSection(FormDataProtectionOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FormDataProtectionOptions>, FormDataProtectionOptionsValidator>();

        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);
        var keyRingPath = configuration[$"{FormDataProtectionOptions.SectionName}:KeyRingPath"];
        if (!string.IsNullOrWhiteSpace(keyRingPath))
        {
            builder.PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
        }

        services.AddSingleton<IFormDataProtector, DataProtectionFormDataProtector>();
        return services;
    }
}
