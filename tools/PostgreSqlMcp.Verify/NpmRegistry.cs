using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PostgreSqlMcp.Verify;

/// <summary>
/// Loopback-only npm registry serving exactly the built tarballs, so npm resolves the main package's
/// optionalDependencies and selects the platform package exactly as it would from npmjs.org.
/// Any other package or path is a 404: verification never reaches a public registry.
/// </summary>
internal sealed class NpmRegistry : IAsyncDisposable
{
    private readonly HttpListener listener = new();
    private readonly Dictionary<string, byte[]> packuments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> tarballs = new(StringComparer.Ordinal);
    private readonly Task serving;
    public string Url { get; }

    private NpmRegistry(string url, IEnumerable<(string Name, JsonObject Manifest, string Tarball)> packages)
    {
        Url = url;
        foreach (var (name, manifest, tarball) in packages)
        {
            string version = manifest["version"].Text();
            string file = Path.GetFileName(tarball);
            byte[] bytes = File.ReadAllBytes(tarball);
            manifest["dist"] = new JsonObject
            {
                ["tarball"] = Url + "-/tarballs/" + file,
                ["integrity"] = "sha512-" + Convert.ToBase64String(SHA512.HashData(bytes)),
                ["shasum"] = Convert.ToHexStringLower(SHA1.HashData(bytes))
            };
            var packument = new JsonObject
            {
                ["name"] = name,
                ["dist-tags"] = new JsonObject { ["latest"] = version },
                ["versions"] = new JsonObject { [version] = manifest }
            };
            packuments[name] = Encoding.UTF8.GetBytes(packument.ToJsonString());
            tarballs[file] = tarball;
        }
        listener.Prefixes.Add(Url);
        listener.Start();
        serving = ServeAsync();
    }

    public static async Task<NpmRegistry> StartAsync(IEnumerable<string> packageTarballs)
    {
        var packages = new List<(string, JsonObject, string)>();
        foreach (string tarball in packageTarballs)
        {
            JsonObject manifest = await ManifestAsync(tarball);
            packages.Add((manifest["name"].Text(), manifest, tarball));
        }
        // HttpListener needs a concrete port: reserve a free loopback one, then bind it.
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new NpmRegistry($"http://127.0.0.1:{port}/", packages);
    }

    /// <summary>The package.json packed into an npm tarball.</summary>
    public static async Task<JsonObject> ManifestAsync(string tarball)
    {
        await using FileStream file = File.OpenRead(tarball);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var archive = new TarReader(gzip);
        while (await archive.GetNextEntryAsync() is { } entry)
            if (entry.Name == "package/package.json" && entry.DataStream is not null)
                return JsonNode.Parse(entry.DataStream)!.AsObject();
        throw new VerificationException($"{Path.GetFileName(tarball)} has no package.json.");
    }

    private async Task ServeAsync()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            using HttpListenerResponse response = context.Response;
            try
            {
                string path = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath.TrimStart('/'));
                if (context.Request.HttpMethod != "GET") response.StatusCode = 405;
                else if (packuments.TryGetValue(path, out byte[]? packument)) await WriteAsync(response, "application/json", packument);
                else if (path.StartsWith("-/tarballs/", StringComparison.Ordinal) && tarballs.TryGetValue(path["-/tarballs/".Length..], out string? tarball))
                    await WriteAsync(response, "application/octet-stream", await File.ReadAllBytesAsync(tarball));
                else
                {
                    response.StatusCode = 404;
                    await WriteAsync(response, "application/json", """{"error":"not_found"}"""u8.ToArray());
                }
            }
            catch (Exception exception) when (exception is HttpListenerException or IOException) { }
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, string type, byte[] body)
    {
        response.ContentType = type;
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body);
    }

    public async ValueTask DisposeAsync()
    {
        listener.Stop();
        listener.Close();
        await serving;
    }
}
