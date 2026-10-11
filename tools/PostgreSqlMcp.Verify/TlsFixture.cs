// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PostgreSqlMcp.Verify;

/// <summary>
/// Disposable TLS-enabled PostgreSQL container. A throwaway CA and server certificate are generated per run and copied in;
/// the server certificate names the container's bridge address (a non-loopback host the verifier can reach) and 127.0.0.1.
/// Nothing is persisted and no key leaves the temporary directory, which is deleted when the fixture is disposed.
/// </summary>
internal sealed class TlsFixture : IAsyncDisposable
{
    public const string Password = "reader-disposable";
    private readonly string name = "postgresql-sharp-mcp-verify-tls-" + Guid.NewGuid().ToString("N");
    private readonly TemporaryDirectory files = new();
    private bool attempted;
    private bool started;

    /// <summary>The container's address on Docker's bridge network, the non-loopback host the verifier connects to.</summary>
    public string BridgeAddress { get; private set; } = "";
    /// <summary>Port published on 127.0.0.1 only.</summary>
    public int LoopbackPort { get; private set; }
    public string CaFile => Path.Combine(files.Path, "ca.pem");

    public async Task StartAsync()
    {
        attempted = true;
        // The entry point waits for the certificate files: the bridge address that the certificate must name exists only after the
        // container runs. Postgres demands a key it owns with owner-only mode, so ownership is fixed inside the container.
        await Processes.RunAsync(new("docker", "run", "-d", "--name", name, "-e", "POSTGRES_PASSWORD=mcp-disposable-only",
            "-p", "127.0.0.1::5432", "--entrypoint", "sh", PostgresFixture.Image, "-c",
            "until [ -e /tmp/certificates-ready ]; do sleep 0.2; done; chown postgres:postgres /tls/server.key; chmod 600 /tls/server.key; " +
            "exec docker-entrypoint.sh postgres -c log_connections=on"),
            timeout: 180);
        started = true;
        BridgeAddress = (await Processes.RunAsync(new("docker", "inspect", "-f", "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}", name))).Output.Trim();
        Check.That(IPAddress.TryParse(BridgeAddress, out IPAddress? parsed) && !IPAddress.IsLoopback(parsed),
            "The TLS fixture needs a container address reachable from the verifier (Docker bridge network on Linux).");
        IPAddress address = parsed!;
        string directory = Path.Combine(files.Path, "tls");
        Directory.CreateDirectory(directory);
        WriteCertificates(directory, address);
        await Processes.RunAsync(new("docker", "cp", directory, name + ":/"));
        // TLS is configured through ALTER SYSTEM, not the command line, so a scenario can later turn it off with a reload.
        string initialization = Path.Combine(files.Path, "tls.sql");
        File.WriteAllText(initialization, "ALTER SYSTEM SET ssl = on;\nALTER SYSTEM SET ssl_cert_file = '/tls/server.crt';\nALTER SYSTEM SET ssl_key_file = '/tls/server.key';\n");
        await Processes.RunAsync(new("docker", "cp", initialization, name + ":/docker-entrypoint-initdb.d/tls.sql"));
        await Processes.RunAsync(new("docker", "exec", name, "touch", "/tmp/certificates-ready"));
        bool ready = false;
        for (int attempt = 0; attempt < 60; attempt++)
        {
            var result = await Processes.RunAsync(new("docker", "exec", name, "pg_isready", "-h", "127.0.0.1", "-U", "postgres"), expected: null);
            if (result.ExitCode == 0) { ready = true; break; }
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        Check.That(ready, "Disposable TLS PostgreSQL fixture did not become ready.");
        await Processes.RunAsync(new("docker", "exec", "-i", name, "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres"),
            input: $"CREATE ROLE mcp_reader LOGIN PASSWORD '{Password}';");
        string endpoint = (await Processes.RunAsync(new("docker", "port", name, "5432/tcp"))).Output.Trim();
        LoopbackPort = int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
        File.Copy(Path.Combine(directory, "ca.pem"), CaFile);
    }

    /// <summary>Everything the server logged so far (PostgreSQL writes its log to stderr).</summary>
    public async Task<string> LogAsync()
    {
        ProcessResult result = await Processes.RunAsync(new("docker", "logs", name));
        return result.Output + result.Error;
    }

    /// <summary>Turns TLS off on the running server (reload only), so it answers every TLS request with "not supported".</summary>
    public async Task DisableServerTlsAsync()
    {
        await Processes.RunAsync(new("docker", "exec", "-i", name, "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres"),
            input: "ALTER SYSTEM SET ssl = off;\nSELECT pg_reload_conf();\n");
        for (int attempt = 0; attempt < 50; attempt++)
        {
            ProcessResult shown = await Processes.RunAsync(new("docker", "exec", name, "psql", "-X", "-U", "postgres", "-tAc", "SHOW ssl"));
            if (shown.Output.Trim() == "off") return;
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
        throw new VerificationException("The TLS fixture did not apply ssl = off.");
    }

    private static void WriteCertificates(string directory, IPAddress bridge)
    {
        DateTimeOffset from = DateTimeOffset.UtcNow.AddMinutes(-5), until = from.AddHours(2);
        using ECDsa caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=postgresql-sharp-mcp verifier CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using X509Certificate2 ca = caRequest.CreateSelfSigned(from, until);

        using ECDsa serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var serverRequest = new CertificateRequest("CN=postgresql-sharp-mcp verifier server", serverKey, HashAlgorithmName.SHA256);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(bridge);
        names.AddIpAddress(IPAddress.Loopback);
        serverRequest.CertificateExtensions.Add(names.Build());
        byte[] serial = RandomNumberGenerator.GetBytes(8);
        serial[0] &= 0x7F;
        serial[0] |= 0x01;
        using X509Certificate2 server = serverRequest.Create(ca, from, until, serial);

        File.WriteAllText(Path.Combine(directory, "ca.pem"), ca.ExportCertificatePem());
        File.WriteAllText(Path.Combine(directory, "server.crt"), server.ExportCertificatePem());
        string key = Path.Combine(directory, "server.key");
        File.WriteAllText(key, serverKey.ExportPkcs8PrivateKeyPem());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (attempted)
                await Processes.RunAsync(new("docker", "rm", "-f", name), expected: started ? 0 : null);
        }
        finally { files.Dispose(); }
    }
}
