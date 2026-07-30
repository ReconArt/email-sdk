using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ReconArt.Email.Sender.Tests;

internal sealed record SmtpSession(int Id, List<string> Commands)
{
    public bool CarriedMail => Commands.Any(static c => c.StartsWith("MAIL", StringComparison.OrdinalIgnoreCase));

    public int DataCount => Commands.Count(static c => c.Equals("DATA", StringComparison.OrdinalIgnoreCase));

    public bool SentQuit => Commands.Any(static c => c.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase));

    /// <summary>Password extracted from the recorded AUTH PLAIN exchange, if any.</summary>
    public string? BasicPassword => Commands
        .FirstOrDefault(static c => c.StartsWith("AUTHPW ", StringComparison.Ordinal))?["AUTHPW ".Length..];

    /// <summary>Bearer token extracted from the recorded AUTH XOAUTH2 exchange(s), if any.</summary>
    public List<string> OAuthTokens => Commands
        .Where(static c => c.StartsWith("AUTHXO ", StringComparison.Ordinal))
        .Select(static c => c["AUTHXO ".Length..])
        .ToList();
}

/// <summary>
/// A gate that stalls the next <paramref name="count"/> DATA transactions until released,
/// so tests can deterministically hold multiple connection slots in-flight at once.
/// </summary>
internal sealed class SmtpDataStallGate(int count)
{
    private int _remaining = count;

    public TaskCompletionSource AllEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool TryEnter()
    {
        while (true)
        {
            int current = Volatile.Read(ref _remaining);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _remaining, current - 1, current) == current)
            {
                if (current == 1)
                {
                    AllEntered.TrySetResult();
                }

                return true;
            }
        }
    }
}

/// <summary>
/// Minimal scriptable SMTP server on a loopback socket. Speaks plaintext SMTP with
/// AUTH PLAIN and AUTH XOAUTH2, records every command per session, and supports fault
/// injection (auth rejection, 530 on MAIL, 554 on DATA, arbitrary MAIL FROM / RCPT TO
/// rejection responses) plus deterministic DATA stalls.
/// </summary>
internal sealed class TestSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly List<SmtpSession> _sessions = [];
    private string? _requiredOAuthToken;
    private string? _requiredBasicPassword;
    private int _failNextMailWith530;
    private int _failNextDataWith554;
    private int _rejectMailCount;
    private string _rejectMailResponse = "";
    private int _rejectRcptCount;
    private string _rejectRcptResponse = "";
    private SmtpDataStallGate? _dataStall;

    public TestSmtpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port { get; }

    /// <summary>XOAUTH2 attempts with any other bearer token get 535; null accepts everything.</summary>
    public string? RequiredOAuthToken
    {
        get => Volatile.Read(ref _requiredOAuthToken);
        set => Volatile.Write(ref _requiredOAuthToken, value);
    }

    /// <summary>AUTH PLAIN attempts with any other password get 535; null accepts everything.</summary>
    public string? RequiredBasicPassword
    {
        get => Volatile.Read(ref _requiredBasicPassword);
        set => Volatile.Write(ref _requiredBasicPassword, value);
    }

    /// <summary>The next <paramref name="count"/> MAIL commands are rejected with 530.</summary>
    public void FailMailWith530(int count) => Volatile.Write(ref _failNextMailWith530, count);

    /// <summary>The next <paramref name="count"/> DATA transactions are rejected with 554.</summary>
    public void FailDataWith554(int count) => Volatile.Write(ref _failNextDataWith554, count);

    /// <summary>
    /// Rejects the next <paramref name="count"/> MAIL FROM commands with the given raw response
    /// line (e.g. "550 5.7.1 Sender not allowed") - drives MailKit's SenderNotAccepted path.
    /// </summary>
    public void RejectSender(string response, int count = 1)
    {
        _rejectMailResponse = response;
        Volatile.Write(ref _rejectMailCount, count);
    }

    /// <summary>
    /// Rejects the next <paramref name="count"/> RCPT TO commands with the given raw response
    /// line (e.g. "550 5.1.1 User unknown") - drives MailKit's RecipientNotAccepted path.
    /// </summary>
    public void RejectRecipient(string response, int count = 1)
    {
        _rejectRcptResponse = response;
        Volatile.Write(ref _rejectRcptCount, count);
    }

    /// <summary>Arms a gate that stalls the next <paramref name="count"/> DATA transactions.</summary>
    public SmtpDataStallGate StallData(int count)
    {
        SmtpDataStallGate gate = new(count);
        Volatile.Write(ref _dataStall, gate);
        return gate;
    }

    public List<SmtpSession> SnapshotSessions()
    {
        lock (_sessions)
        {
            return _sessions.Select(static s => s with { Commands = [.. s.Commands] }).ToList();
        }
    }

    public List<SmtpSession> MailSessions() => SnapshotSessions().Where(static s => s.CarriedMail).ToList();

    private async Task AcceptLoopAsync()
    {
        int id = 0;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = HandleAsync(client, ++id);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private async Task HandleAsync(TcpClient client, int id)
    {
        SmtpSession session = new(id, []);
        lock (_sessions)
        {
            _sessions.Add(session);
        }

        using TcpClient c = client;
        try
        {
            NetworkStream stream = c.GetStream();
            using StreamReader reader = new(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            using StreamWriter writer = new(stream, Encoding.ASCII, 1024, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

            await writer.WriteLineAsync("220 localhost ESMTP test");

            string? line;
            while ((line = await reader.ReadLineAsync(_cts.Token)) is not null)
            {
                Record(session, line);
                string upper = line.ToUpperInvariant();
                if (upper.StartsWith("EHLO") || upper.StartsWith("HELO"))
                {
                    await writer.WriteLineAsync("250-localhost");
                    await writer.WriteLineAsync("250 AUTH PLAIN XOAUTH2");
                }
                else if (upper.StartsWith("AUTH PLAIN"))
                {
                    string payload = line.Length > "AUTH PLAIN".Length ? line["AUTH PLAIN".Length..].Trim() : string.Empty;
                    if (payload.Length == 0)
                    {
                        await writer.WriteLineAsync("334 ");
                        payload = (await reader.ReadLineAsync(_cts.Token))?.Trim() ?? string.Empty;
                    }

                    Record(session, "AUTHPW " + ExtractPlainPassword(payload));
                    string? requiredPassword = RequiredBasicPassword;
                    if (requiredPassword is null || ExtractPlainPassword(payload) == requiredPassword)
                    {
                        await writer.WriteLineAsync("235 2.7.0 Authentication successful");
                    }
                    else
                    {
                        await writer.WriteLineAsync("535 5.7.8 Authentication credentials invalid");
                    }
                }
                else if (upper.StartsWith("AUTH XOAUTH2"))
                {
                    string payload = line.Length > "AUTH XOAUTH2".Length ? line["AUTH XOAUTH2".Length..].Trim() : string.Empty;
                    if (payload.Length == 0)
                    {
                        await writer.WriteLineAsync("334 ");
                        payload = (await reader.ReadLineAsync(_cts.Token))?.Trim() ?? string.Empty;
                    }

                    string token = ExtractXOAuth2Token(payload);
                    Record(session, "AUTHXO " + token);
                    string? requiredToken = RequiredOAuthToken;
                    if (requiredToken is null || token == requiredToken)
                    {
                        await writer.WriteLineAsync("235 2.7.0 Authentication successful");
                    }
                    else
                    {
                        await writer.WriteLineAsync("535 5.7.3 Authentication unsuccessful");
                    }
                }
                else if (upper.StartsWith("MAIL"))
                {
                    if (TryConsume(ref _failNextMailWith530))
                    {
                        await writer.WriteLineAsync("530 5.7.0 Authentication required");
                    }
                    else if (TryConsume(ref _rejectMailCount))
                    {
                        await writer.WriteLineAsync(_rejectMailResponse);
                    }
                    else
                    {
                        await writer.WriteLineAsync("250 2.1.0 OK");
                    }
                }
                else if (upper.StartsWith("RCPT"))
                {
                    if (TryConsume(ref _rejectRcptCount))
                    {
                        await writer.WriteLineAsync(_rejectRcptResponse);
                    }
                    else
                    {
                        await writer.WriteLineAsync("250 2.1.5 OK");
                    }
                }
                else if (upper == "DATA")
                {
                    await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                    while ((line = await reader.ReadLineAsync(_cts.Token)) is not null && line != ".")
                    {
                    }

                    if (TryConsume(ref _failNextDataWith554))
                    {
                        await writer.WriteLineAsync("554 5.0.0 Transaction failed");
                    }
                    else
                    {
                        SmtpDataStallGate? stall = Volatile.Read(ref _dataStall);
                        if (stall is not null && stall.TryEnter())
                        {
                            await stall.Release.Task.WaitAsync(_cts.Token);
                        }

                        await writer.WriteLineAsync("250 2.0.0 OK: queued");
                    }
                }
                else if (upper.StartsWith("QUIT"))
                {
                    await writer.WriteLineAsync("221 2.0.0 Bye");
                    break;
                }
                else
                {
                    await writer.WriteLineAsync("250 2.0.0 OK");
                }
            }
        }
        catch
        {
            // Connection dropped or server shutting down - session keeps what it recorded.
        }
    }

    private void Record(SmtpSession session, string line)
    {
        lock (_sessions)
        {
            session.Commands.Add(line);
        }
    }

    private static bool TryConsume(ref int counter)
    {
        while (true)
        {
            int current = Volatile.Read(ref counter);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref counter, current - 1, current) == current)
            {
                return true;
            }
        }
    }

    private static string ExtractPlainPassword(string payload)
    {
        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            string[] parts = decoded.Split('\0');
            return parts.Length == 3 ? parts[2] : payload;
        }
        catch (FormatException)
        {
            return payload;
        }
    }

    private static string ExtractXOAuth2Token(string payload)
    {
        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            foreach (string part in decoded.Split((char)1))
            {
                if (part.StartsWith("auth=Bearer ", StringComparison.Ordinal))
                {
                    return part["auth=Bearer ".Length..];
                }
            }
        }
        catch (FormatException)
        {
        }

        return payload;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch
        {
        }

        _cts.Dispose();
    }
}
