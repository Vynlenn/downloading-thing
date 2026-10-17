using System.Security.Cryptography;
using System.Text;

namespace DeezNutzDownloader.Infrastructure.Security;

/// <summary>
/// Encrypts proxy passwords with Windows DPAPI (current user scope)
/// so the settings file never holds a cleartext secret.
/// </summary>
public static class ProxyCredentialProtector
{
    public static string Protect(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return string.Empty;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException(
                "Encrypted proxy passwords require Windows (DPAPI).");
        }

        byte[] encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(password),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);

        return Convert.ToBase64String(encrypted);
    }

    public static string? Unprotect(string? protectedPassword)
    {
        if (string.IsNullOrEmpty(protectedPassword))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException(
                "Encrypted proxy passwords require Windows (DPAPI).");
        }

        try
        {
            byte[] decrypted = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedPassword),
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception exception)
            when (
                exception is FormatException or
                CryptographicException
            )
        {
            throw new InvalidOperationException(
                "The saved proxy password could not be decrypted " +
                "(it may come from another Windows user or PC). " +
                "Re-enter it in Settings.",
                exception);
        }
    }

}
