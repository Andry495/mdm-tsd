using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using TsdServer.Core;
using TsdServer.Infrastructure;

public sealed class LiveInput
{
    public string Type { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public string? Value { get; set; }
    public string? Code { get; set; }
}

public sealed class LiveSession
{
    public string Id { get; } = Guid.NewGuid().ToString("D");
    public string DeviceId { get; init; } = "";
    public string Operator { get; init; } = "";
    public int Fps { get; init; } = 4;
    public int Width { get; init; } = 720;
    public byte[]? Frame { get; set; }
    public string? Error { get; set; }
    public ConcurrentQueue<LiveInput> Inputs { get; } = new();
}

public sealed class AgentDesk
{
    readonly ConcurrentDictionary<string, LiveSession> _sessions = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _waiters = new();
    readonly ConcurrentDictionary<string, DateTime> _inflight = new();

    public LiveSession Open(string deviceId, string oper)
    {
        var session = new LiveSession { DeviceId = deviceId, Operator = oper };
        _sessions[deviceId] = session;
        Poke(deviceId);
        return session;
    }

    public void Close(string deviceId)
    {
        _sessions.TryRemove(deviceId, out _);
        Poke(deviceId);
    }

    public LiveSession? Session(string deviceId) =>
        _sessions.TryGetValue(deviceId, out var session) ? session : null;

    public void Frame(string deviceId, string sessionId, byte[] jpeg)
    {
        if (!_sessions.TryGetValue(deviceId, out var session) || session.Id != sessionId)
            return;
        session.Frame = jpeg;
        session.Error = null;
    }

    public void Fail(string deviceId, string sessionId, string error)
    {
        if (_sessions.TryGetValue(deviceId, out var session) && session.Id == sessionId)
            session.Error = error;
    }

    public void Input(string deviceId, LiveInput input)
    {
        if (!_sessions.TryGetValue(deviceId, out var session))
            return;
        session.Inputs.Enqueue(input);
        while (session.Inputs.Count > 30 && session.Inputs.TryDequeue(out _))
        {
        }
        Poke(deviceId);
    }

    public bool HasInputs(string deviceId, string sessionId) =>
        _sessions.TryGetValue(deviceId, out var session) && session.Id == sessionId && !session.Inputs.IsEmpty;

    public List<LiveInput> TakeInputs(string deviceId, string sessionId)
    {
        var list = new List<LiveInput>();
        if (!_sessions.TryGetValue(deviceId, out var session) || session.Id != sessionId)
            return list;
        while (session.Inputs.TryDequeue(out var input))
            list.Add(input);
        return list;
    }

    public async Task WaitAsync(string deviceId, CancellationToken cancellationToken)
    {
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[deviceId] = waiter;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await waiter.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _waiters.TryRemove(deviceId, out _);
        }
    }

    public bool Claim(string jobId)
    {
        var now = DateTime.UtcNow;
        while (true)
        {
            if (!_inflight.TryGetValue(jobId, out var at))
            {
                if (_inflight.TryAdd(jobId, now))
                    return true;
                continue;
            }
            if (at >= now.AddSeconds(-90))
                return false;
            if (_inflight.TryUpdate(jobId, now, at))
                return true;
        }
    }

    public void Release(string jobId) => _inflight.TryRemove(jobId, out _);

    public static bool Signed(Device device, string nonce, string signature, string extra = "")
    {
        if (string.IsNullOrWhiteSpace(device.PublicKey) || string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(signature))
            return false;
        if (!long.TryParse(nonce, out var unix))
            return false;
        if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix) > 15 * 60)
            return false;
        return Protocol.VerifySignature(device.PublicKey, nonce + "|" + device.Id + extra, signature);
    }

    public static string CertStamp(string path)
    {
        if (!File.Exists(path))
            return "";
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }

    void Poke(string deviceId)
    {
        if (_waiters.TryGetValue(deviceId, out var waiter))
            waiter.TrySetResult(true);
    }
}

public sealed class AgentCall
{
    public string DeviceId { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Signature { get; set; } = "";
    public int AgentCode { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? AndroidId { get; set; }
    public string? Name { get; set; }
    public string? UpdatePhase { get; set; }
    public string? ScreenPhase { get; set; }
    public bool ControlReady { get; set; }
    public List<AgentNote>? Notes { get; set; }
}

public sealed class AgentNote
{
    public string Area { get; set; } = "";
    public string Text { get; set; } = "";
    public long At { get; set; }
}

public static class AgentCallJson
{
    public static object Offer(InstallOffer? offer) => offer is null
        ? new { } 
        : new { jobId = offer.JobId, packageName = offer.PackageName, versionCode = offer.VersionCode, sha256 = offer.Sha256, url = offer.Url };

    public static string Inputs(IEnumerable<LiveInput> inputs) =>
        JsonSerializer.Serialize(new { events = inputs }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
