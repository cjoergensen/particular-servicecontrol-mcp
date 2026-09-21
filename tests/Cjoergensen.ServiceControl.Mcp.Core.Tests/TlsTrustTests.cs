using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Cjoergensen.ServiceControl.Mcp.Client;
using Cjoergensen.ServiceControl.Mcp.Tests.Pki;
using static Cjoergensen.ServiceControl.Mcp.Tests.Support.TestKit;

namespace Cjoergensen.ServiceControl.Mcp.Tests;

public class TlsTrustTests
{
    /// <summary>A minimal HTTPS server: enough of HTTP/1.1 to answer any request with an empty JSON array.</summary>
    sealed class TlsServer : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stop = new();

        public TlsServer(X509Certificate2 certificate)
        {
            listener.Start();
            _ = Task.Run(() => AcceptAsync(certificate));
        }

        public Uri Url => new($"https://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}/");

        async Task AcceptAsync(X509Certificate2 certificate)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token);
                    _ = Task.Run(() => ServeAsync(client, certificate));
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }
            }
        }

        static async Task ServeAsync(TcpClient client, X509Certificate2 certificate)
        {
            using (client)
            {
                try
                {
                    await using var tls = new SslStream(client.GetStream());
                    await tls.AuthenticateAsServerAsync(certificate);
                    var buffer = new byte[4096];
                    _ = await tls.ReadAsync(buffer);
                    await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n[]"));
                    await tls.FlushAsync();
                }
                catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
                {
                    // The client rejected the certificate; that is the point of some tests.
                }
            }
        }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
            stop.Dispose();
        }
    }

    static string WritePem(TestPki pki)
    {
        var path = Path.Combine(Path.GetTempPath(), "scmcp-ca-" + Guid.NewGuid().ToString("N") + ".pem");
        File.WriteAllText(path, pki.CaPem);
        return path;
    }

    [Fact]
    public async Task A_server_signed_by_a_private_ca_is_rejected_by_default()
    {
        using var pki = TestPki.Create();
        using var server = new TlsServer(pki.ServerCertificate);
        using var http = new HttpClient(TlsTrust.CreateHandler(caCertificatePath: null));

        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(server.Url, Ct));
    }

    [Fact]
    public async Task A_server_signed_by_a_configured_ca_is_trusted()
    {
        using var pki = TestPki.Create();
        var caPath = WritePem(pki);
        try
        {
            using var server = new TlsServer(pki.ServerCertificate);
            using var http = new HttpClient(TlsTrust.CreateHandler(caPath));

            using var response = await http.GetAsync(server.Url, Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("[]", await response.Content.ReadAsStringAsync(Ct));
        }
        finally
        {
            File.Delete(caPath);
        }
    }

    [Fact]
    public async Task A_different_private_ca_is_still_rejected()
    {
        using var serverPki = TestPki.Create();
        using var otherPki = TestPki.Create();
        var caPath = WritePem(otherPki);
        try
        {
            using var server = new TlsServer(serverPki.ServerCertificate);
            using var http = new HttpClient(TlsTrust.CreateHandler(caPath));

            await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(server.Url, Ct));
        }
        finally
        {
            File.Delete(caPath);
        }
    }

    [Fact]
    public void A_host_name_mismatch_is_never_forgiven_by_an_extra_root()
    {
        using var pki = TestPki.Create();
        var roots = TlsTrust.Load(WritePem(pki));

        Assert.False(TlsTrust.IsTrusted(roots, pki.ServerCertificate, SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(TlsTrust.IsTrusted(roots, certificate: null, SslPolicyErrors.RemoteCertificateNotAvailable));
        Assert.True(TlsTrust.IsTrusted(roots, certificate: null, SslPolicyErrors.None));
    }

    [Fact]
    public void A_missing_or_empty_ca_file_is_reported_clearly()
    {
        Assert.Throws<FileNotFoundException>(() => TlsTrust.Load("/definitely/not/here.pem"));

        var empty = Path.GetTempFileName();
        try
        {
            Assert.Throws<InvalidOperationException>(() => TlsTrust.Load(empty));
        }
        finally
        {
            File.Delete(empty);
        }
    }
}
