using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using TsdServer.Core;

namespace TsdServer.Infrastructure;

public sealed class AgentChannel
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly PortalOptions _options;

    public AgentChannel(PortalOptions options) => _options = options;

    public async Task<AgentReport?> GetReportAsync(string address, string publicKey, CancellationToken cancellationToken)
    {
        using var ssl = await OpenAsync(address, cancellationToken);
        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        await WriteJsonAsync(ssl, new { type = "hello", nonce }, cancellationToken);
        var hello = await ReadJsonAsync(ssl, cancellationToken);
        if (hello is null || hello.Type != "helloOk" || string.IsNullOrWhiteSpace(hello.DeviceId))
            return null;
        if (!Protocol.VerifySignature(publicKey, nonce + "|" + hello.DeviceId, hello.Signature ?? ""))
            return null;
        await WriteJsonAsync(ssl, new { type = "getReport" }, cancellationToken);
        var report = await ReadJsonAsync(ssl, cancellationToken);
        if (report is null || report.Type != "report")
            return null;
        return new AgentReport(report.AgentCode, report.AgentName, report.Packages, report.CompletedBlocks, report.LastError);
    }

    public async Task<JobAnswer?> InstallAsync(string address, string publicKey, object command, CancellationToken cancellationToken)
    {
        using var ssl = await OpenAsync(address, cancellationToken);
        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        await WriteJsonAsync(ssl, new { type = "hello", nonce }, cancellationToken);
        var hello = await ReadJsonAsync(ssl, cancellationToken);
        if (hello is null || hello.Type != "helloOk" || !Protocol.VerifySignature(publicKey, nonce + "|" + hello.DeviceId, hello.Signature ?? ""))
            return new JobAnswer(false, "устройство не подтвердило ключ");
        await WriteJsonAsync(ssl, command, cancellationToken);
        var result = await ReadJsonAsync(ssl, cancellationToken);
        if (result is null || result.Type != "jobResult")
            return new JobAnswer(false, "агент не прислал результат");
        return new JobAnswer(result.Ok, result.Error);
    }

    async Task<SslStream> OpenAsync(string address, CancellationToken cancellationToken)
    {
        var cert = X509Certificate2.CreateFromPemFile(_options.CertPath, _options.CertKeyPath);
        var portable = new X509Certificate2(cert.Export(X509ContentType.Pfx));
        cert.Dispose();
        var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(address, _options.CommandPort, timeout.Token);
        var ssl = new SslStream(client.GetStream(), false, static (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = address,
            ClientCertificates = new X509CertificateCollection { portable },
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, cancellationToken);
        return ssl;
    }

    static async Task WriteJsonAsync(SslStream ssl, object body, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        var length = payload.Length + 1;
        var frame = new byte[4 + length];
        frame[0] = (byte)(length >> 24);
        frame[1] = (byte)(length >> 16);
        frame[2] = (byte)(length >> 8);
        frame[3] = (byte)length;
        frame[4] = 1;
        payload.CopyTo(frame.AsSpan(5));
        await ssl.WriteAsync(frame, cancellationToken);
    }

    static async Task<AgentMessage?> ReadJsonAsync(SslStream ssl, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var header = new byte[4];
        await ReadExactAsync(ssl, header, timeout.Token);
        var length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        if (length < 1 || length > 1_000_000)
            return null;
        var body = new byte[length];
        await ReadExactAsync(ssl, body, timeout.Token);
        if (body[0] != 1)
            return null;
        return JsonSerializer.Deserialize<AgentMessage>(body.AsSpan(1), Json);
    }

    static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        }
    }
}

public sealed record AgentReport(int? AgentCode, string? AgentName, List<ReportedPackage>? Packages, List<string>? CompletedBlocks, string? LastError);

public sealed record JobAnswer(bool Ok, string? Error);

public sealed class AgentMessage
{
    public string Type { get; set; } = "";
    public string? DeviceId { get; set; }
    public string? Signature { get; set; }
    public int? AgentCode { get; set; }
    public string? AgentName { get; set; }
    public List<ReportedPackage>? Packages { get; set; }
    public List<string>? CompletedBlocks { get; set; }
    public string? LastError { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
}

public sealed class ReportedPackage
{
    public string PackageName { get; set; } = "";
    public int VersionCode { get; set; }
}
