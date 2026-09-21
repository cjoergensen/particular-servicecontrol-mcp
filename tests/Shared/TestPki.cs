using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Cjoergensen.ServiceControl.Mcp.Tests.Pki;

/// <summary>
/// A throwaway certificate authority and one server certificate it signed, generated per test run. Lets tests exercise real HTTPS with
/// a private CA (the normal situation for a ServiceControl installation) without any checked-in key material.
/// </summary>
public sealed class TestPki : IDisposable
{
    readonly X509Certificate2 ca;
    readonly X509Certificate2 server;

    TestPki(X509Certificate2 ca, X509Certificate2 server, string pfxPassword)
    {
        this.ca = ca;
        this.server = server;
        PfxPassword = pfxPassword;
    }

    public string PfxPassword { get; }

    /// <summary>The CA certificate in PEM form: what a client is told to trust.</summary>
    public string CaPem => ca.ExportCertificatePem();

    /// <summary>The server certificate and key as PKCS#12, protected by <see cref="PfxPassword"/>.</summary>
    public byte[] ServerPfx => server.Export(X509ContentType.Pfx, PfxPassword);

    public X509Certificate2 ServerCertificate => server;

    /// <summary>Creates a CA and a server certificate valid for <c>localhost</c>, the loopback addresses and any extra DNS names.</summary>
    public static TestPki Create(params string[] extraDnsNames)
    {
        const string password = "test-only-password";
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddDays(30);

        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=ServiceControl MCP Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, 0, critical: true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, critical: false));
        var caWithKey = caRequest.CreateSelfSigned(notBefore, notAfter);

        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        foreach (var name in extraDnsNames)
        {
            names.AddDnsName(name);
        }

        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        serverRequest.CertificateExtensions.Add(names.Build());
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, 0, critical: true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        serverRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromSubjectKeyIdentifier(
            caWithKey.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Single()));

        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        using var signed = serverRequest.Create(caWithKey, notBefore, notAfter, serial);

        // Re-import so the certificates own their keys independently of the RSA instances disposed above.
        var serverWithKey = X509CertificateLoader.LoadPkcs12(signed.CopyWithPrivateKey(serverKey).Export(X509ContentType.Pfx, password), password, X509KeyStorageFlags.Exportable);
        var caPublic = X509CertificateLoader.LoadCertificate(caWithKey.Export(X509ContentType.Cert));
        caWithKey.Dispose();

        return new TestPki(caPublic, serverWithKey, password);
    }

    public void Dispose()
    {
        ca.Dispose();
        server.Dispose();
    }
}
