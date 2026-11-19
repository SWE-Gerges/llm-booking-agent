namespace Agent.Abstractions.Ports;

/// <summary>
/// Encrypts sensitive text (the booking-context JSON, which holds raw passenger and contact details) before it is
/// stored. Implementations must fail closed: anything that cannot be authenticated and decrypted throws.
/// </summary>
public interface IFormDataProtector
{
    string Protect(string plaintext);

    /// <exception cref="Errors.FormDataProtectionException">The value was tampered with, belongs to another key, or is not protected data.</exception>
    string Unprotect(string protectedValue);
}
