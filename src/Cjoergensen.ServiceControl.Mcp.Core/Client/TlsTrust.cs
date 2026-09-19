using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Cjoergensen.ServiceControl.Mcp.Client;

/// <summary>
/// Builds HTTP handlers that additionally trust a configured set of private certificate authorities. Validation stays on: a server
/// certificate must still chain to a trusted root, be within its validity period and match the host name. A private CA only adds roots.
/// </summary>
public static class TlsTrust
{
    public static SocketsHttpHandler CreateHandler(string? caCertificatePath)
    {
        var handler = new SocketsHttpHandler();
        if (string.IsNullOrWhiteSpace(caCertificatePath))
        {
            return handler;
        }

        var roots = Load(caCertificatePath);
        handler.SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, errors) => IsTrusted(roots, certificate, errors)
        };
        return handler;
    }

    internal static X509Certificate2Collection Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"TrustedCaCertificatePath '{path}' does not exist.", path);
        }

        var roots = new X509Certificate2Collection();
        roots.ImportFromPemFile(path);
        return roots.Count > 0
            ? roots
            : throw new InvalidOperationException($"TrustedCaCertificatePath '{path}' contains no PEM certificates.");
    }

    internal static bool IsTrusted(X509Certificate2Collection roots, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        // A host name mismatch or a missing certificate is a real failure that no extra root can fix.
        if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
        {
            return false;
        }

        // Only the chain failed to reach a system root: check it against the configured roots instead.
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return chain.Build(leaf);
    }
}
