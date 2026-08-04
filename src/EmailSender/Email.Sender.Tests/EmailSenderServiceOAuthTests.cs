using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;
using Xunit;

namespace ReconArt.Email.Sender.Tests;

/// <summary>
/// Behavioral tests for <see cref="EmailSenderService"/> driven over a real loopback SMTP
/// socket (<see cref="TestSmtpServer"/>) - the service is exercised through its public
/// surface exactly as production traffic would.
/// </summary>
public sealed class EmailSenderServiceOAuthTests
{
    [Fact]
    public void CreateBasic_WithoutAuthenticationAndWithoutFromAddress_Throws()
    {
        Assert.Throws<ValidationException>(() =>
            EmailSenderOptions.CreateBasic("smtp.example.com", 25, requiresAuthentication: false));
    }

    [Fact]
    public void Validate_OAuth2Options_RequiresRefreshCallbackButNotInitialToken()
    {
        EmailSenderOptions options = new()
        {
            Host = "smtp.example.com",
            Port = 587,
            AuthenticationType = EmailSenderAuthenticationType.OAuth2,
            Username = "mailer@example.com"
        };

        List<ValidationResult> results = [];
        bool isValid = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, static x => x.MemberNames.Contains(nameof(EmailSenderOptions.RefreshAccessTokenAsync)));
        // An initial access token and its expiry are optional - the service obtains a token
        // via the refresh callback before first use when they are absent.
        Assert.DoesNotContain(results, static x => x.MemberNames.Contains(nameof(EmailSenderOptions.AccessToken)));
        Assert.DoesNotContain(results, static x => x.MemberNames.Contains(nameof(EmailSenderOptions.AccessTokenExpiresAtUtc)));
    }

    [Fact]
    public async Task TestConnectionAsync_BasicWithoutAuthentication_DoesNotAuthenticate()
    {
        await using TestSmtpServer server = new();
        await using EmailSenderService service = CreateService(CreateBasicOptions(server.Port, requiresAuthentication: false));

        Exception? exception = await service.TestConnectionAsync();

        Assert.Null(exception);
        SmtpSession session = Assert.Single(server.SnapshotSessions());
        Assert.DoesNotContain(session.Commands, static c => c.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TestConnectionAsync_OAuth2_RefreshesExpiredTokenBeforeAuthenticating()
    {
        await using TestSmtpServer server = new();
        server.RequiredOAuthToken = "replacement-token";

        int refreshCalls = 0;
        EmailSenderOptions options = CreateOAuth2Options(server.Port, "expired-token", DateTime.UtcNow.AddMinutes(-5), _ =>
        {
            Interlocked.Increment(ref refreshCalls);
            return ValueTask.FromResult(RefreshResult("replacement-token"));
        });
        await using EmailSenderService service = CreateService(options);

        Exception? exception = await service.TestConnectionAsync();

        Assert.Null(exception);
        Assert.Equal(1, refreshCalls);
        Assert.Equal("replacement-token", options.AccessToken);
        Assert.All(server.SnapshotSessions(), static s => Assert.DoesNotContain("expired-token", s.OAuthTokens));
        Assert.Contains(server.SnapshotSessions(), static s => s.OAuthTokens.Contains("replacement-token"));
    }

    [Fact]
    public async Task TestConnectionAsync_OAuth2_ConcurrentAuthenticationFailure_RefreshesOnce()
    {
        await using TestSmtpServer server = new();
        server.RequiredOAuthToken = "replacement-token";

        int refreshCalls = 0;
        EmailSenderOptions options = CreateOAuth2Options(server.Port, "initial-token", DateTime.UtcNow.AddMinutes(10), async _ =>
        {
            Interlocked.Increment(ref refreshCalls);
            await Task.Delay(100);
            return RefreshResult("replacement-token");
        });
        await using EmailSenderService service = CreateService(options);

        Exception?[] results = await Task.WhenAll(
            service.TestConnectionAsync().AsTask(),
            service.TestConnectionAsync().AsTask());

        Assert.All(results, Assert.Null);
        Assert.Equal(1, refreshCalls);
        Assert.Equal("replacement-token", options.AccessToken);
    }

    [Fact]
    public async Task TestConnectionAsync_WithoutValidConfiguration_ReturnsException()
    {
        await using TestSmtpServer server = new();
        TestOptionsMonitor<EmailSenderOptions> monitor = new(new EmailSenderOptions());
        await using EmailSenderService service = CreateService(monitor);

        Exception? exception = await service.TestConnectionAsync();

        Assert.IsType<InvalidOperationException>(exception);
        Assert.Empty(server.SnapshotSessions());
    }

    [Fact]
    public async Task TestConnectionAsync_WithPassedBasicOptions_UsesCandidateOptionsWithoutFetchingRuntimeProvider()
    {
        await using TestSmtpServer server = new();
        server.RequiredBasicPassword = "candidate-password";
        TestEmailSenderOptionsProvider provider = new(CreateBasicOptions(server.Port, requiresAuthentication: true, password: "runtime-password"));
        await using EmailSenderService service = CreateService(provider);
        EmailSenderOptions candidateOptions = CreateBasicOptions(server.Port, requiresAuthentication: true, password: "candidate-password");

        Exception? exception = await service.TestConnectionAsync(candidateOptions);

        Assert.Null(exception);
        Assert.Equal(0, provider.Calls);
        SmtpSession session = Assert.Single(server.SnapshotSessions());
        Assert.Equal("candidate-password", session.BasicPassword);
        Assert.False(session.CarriedMail);
        Assert.True(session.SentQuit);
    }

    [Fact]
    public async Task TestConnectionAsync_WithPassedOptions_DoesNotDisturbRuntimeConnection()
    {
        await using TestSmtpServer server = new();
        server.RequiredBasicPassword = "runtime-password";
        EmailSenderOptions runtimeOptions = CreateBasicOptions(server.Port, requiresAuthentication: true, password: "runtime-password");
        await using EmailSenderService service = CreateService(runtimeOptions);

        Assert.True(await service.TrySendAsync(new EmailMessage("first@example.com", "Subject", "Body")));

        server.RequiredBasicPassword = "candidate-password";
        EmailSenderOptions candidateOptions = CreateBasicOptions(server.Port, requiresAuthentication: true, password: "candidate-password");
        Exception? exception = await service.TestConnectionAsync(candidateOptions);

        Assert.Null(exception);

        server.RequiredBasicPassword = "runtime-password";
        Assert.True(await service.TrySendAsync(new EmailMessage("second@example.com", "Subject", "Body")));

        List<SmtpSession> sessions = server.SnapshotSessions();
        SmtpSession runtimeSession = Assert.Single(sessions, static s => s.BasicPassword == "runtime-password");
        SmtpSession candidateSession = Assert.Single(sessions, static s => s.BasicPassword == "candidate-password");
        Assert.Equal(2, runtimeSession.DataCount);
        Assert.False(candidateSession.CarriedMail);
        Assert.True(candidateSession.SentQuit);
    }

    [Fact]
    public async Task TestConnectionAsync_WithPassedOAuthOptions_RefreshesAndRetriesRejectedTokenWithoutFetchingRuntimeProvider()
    {
        await using TestSmtpServer server = new();
        server.RequiredOAuthToken = "accepted-token";
        int refreshCalls = 0;
        TestEmailSenderOptionsProvider provider = new(CreateOAuth2Options(server.Port, "runtime-token", DateTime.UtcNow.AddMinutes(30), _ =>
        {
            throw new InvalidOperationException("Runtime refresh callback should not be used.");
        }));
        await using EmailSenderService service = CreateService(provider);
        EmailSenderOptions candidateOptions = CreateOAuth2Options(server.Port, "rejected-token", DateTime.UtcNow.AddMinutes(30), _ =>
        {
            Interlocked.Increment(ref refreshCalls);
            return ValueTask.FromResult(RefreshResult("accepted-token"));
        });

        Exception? exception = await service.TestConnectionAsync(candidateOptions);

        Assert.Null(exception);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(1, refreshCalls);
        Assert.Equal("accepted-token", candidateOptions.AccessToken);
        List<SmtpSession> sessions = server.SnapshotSessions();
        Assert.Equal(2, sessions.Count);
        Assert.Equal(["rejected-token"], sessions[0].OAuthTokens);
        Assert.True(sessions[0].SentQuit);
        Assert.Equal(["accepted-token"], sessions[1].OAuthTokens);
        Assert.True(sessions[1].SentQuit);
    }

    [Fact]
    public async Task TestConnectionAsync_WithPassedOAuthOptionsWithoutInitialToken_RefreshesBeforeConnecting()
    {
        await using TestSmtpServer server = new();
        server.RequiredOAuthToken = "accepted-token";
        int refreshCalls = 0;
        await using EmailSenderService service = CreateService(CreateBasicOptions(server.Port, requiresAuthentication: false));
        EmailSenderOptions candidateOptions = CreateOAuth2Options(server.Port, accessToken: null, default, _ =>
        {
            Interlocked.Increment(ref refreshCalls);
            return ValueTask.FromResult(RefreshResult("accepted-token"));
        });

        Exception? exception = await service.TestConnectionAsync(candidateOptions);

        Assert.Null(exception);
        Assert.Equal(1, refreshCalls);
        Assert.Equal("accepted-token", candidateOptions.AccessToken);
        SmtpSession session = Assert.Single(server.SnapshotSessions());
        Assert.Equal(["accepted-token"], session.OAuthTokens);
        Assert.True(session.SentQuit);
    }

    [Fact]
    public async Task TestConnectionAsync_WithPassedOAuthOptionsWithUnknownExpiry_UsesTokenWithoutRefreshing()
    {
        await using TestSmtpServer server = new();
        server.RequiredOAuthToken = "candidate-token";
        int refreshCalls = 0;
        await using EmailSenderService service = CreateService(CreateBasicOptions(server.Port, requiresAuthentication: false));
        EmailSenderOptions candidateOptions = CreateOAuth2Options(server.Port, "candidate-token", default, _ =>
        {
            Interlocked.Increment(ref refreshCalls);
            return ValueTask.FromResult(RefreshResult("unused-token"));
        });

        Exception? exception = await service.TestConnectionAsync(candidateOptions);

        Assert.Null(exception);
        Assert.Equal(0, refreshCalls);
        SmtpSession session = Assert.Single(server.SnapshotSessions());
        Assert.Equal(["candidate-token"], session.OAuthTokens);
    }

    [Fact]
    public async Task TrySendAsync_OAuth2_ConcurrentExpiredToken_RefreshesOnce()
    {
        await using TestSmtpServer server = new();
        server.RequiredOAuthToken = "refreshed-token";

        int refreshCalls = 0;
        EmailSenderOptions options = CreateOAuth2Options(server.Port, "expired-token", DateTime.UtcNow.AddMinutes(-1), async _ =>
        {
            Interlocked.Increment(ref refreshCalls);
            await Task.Delay(100);
            return RefreshResult("refreshed-token");
        });
        await using EmailSenderService service = CreateService(options, maxConcurrentConnections: 2);

        bool[] results = await Task.WhenAll(
            service.TrySendAsync(new EmailMessage("first@example.com", "Subject", "Body")).AsTask(),
            service.TrySendAsync(new EmailMessage("second@example.com", "Subject", "Body")).AsTask());

        Assert.All(results, Assert.True);
        Assert.Equal(1, refreshCalls);
        Assert.Equal("refreshed-token", options.AccessToken);
        Assert.All(server.MailSessions(), static s => Assert.Equal(["refreshed-token"], s.OAuthTokens));
    }

    [Fact]
    public async Task TrySendAsync_OAuth2_NoInitialAccessToken_ObtainsOneBeforeFirstUse()
    {
        await using TestSmtpServer server = new();
        server.RequiredOAuthToken = "obtained-token";

        // The caller holds only a refresh callback - no initial access token, no expiry.
        int refreshCalls = 0;
        EmailSenderOptions options = CreateOAuth2Options(server.Port, accessToken: null, default, _ =>
        {
            Interlocked.Increment(ref refreshCalls);
            return ValueTask.FromResult(RefreshResult("obtained-token"));
        });
        await using EmailSenderService service = CreateService(options);

        bool firstSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));
        bool secondSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.True(firstSend);
        Assert.True(secondSend);
        Assert.Equal(1, refreshCalls);
        SmtpSession session = Assert.Single(server.MailSessions());
        Assert.Equal(["obtained-token"], session.OAuthTokens);
    }

    [Fact]
    public async Task TrySendAsync_OAuth2_UnknownExpiry_UsesTokenUntilServerRejectsIt()
    {
        await using TestSmtpServer server = new();

        // Unknown expiry must mean "use optimistically", not "treat as stale" - the
        // refresh delegate must not be invoked while the server accepts the token.
        int refreshCalls = 0;
        EmailSenderOptions options = CreateOAuth2Options(server.Port, "unexpiring-token", default, _ =>
        {
            Interlocked.Increment(ref refreshCalls);
            return ValueTask.FromResult(RefreshResult("refreshed-token"));
        });
        await using EmailSenderService service = CreateService(options);

        Assert.True(await service.TrySendAsync(new EmailMessage("first@example.com", "Subject", "Body")));
        Assert.Equal(0, refreshCalls);

        // Once the server starts rejecting it, the reactive path takes over.
        server.RequiredOAuthToken = "refreshed-token";
        server.FailMailWith530(1);

        Assert.True(await service.TrySendAsync(new EmailMessage("second@example.com", "Subject", "Body")));
        Assert.Equal(1, refreshCalls);
    }

    [Fact]
    public async Task TrySendAsync_OAuth2_SendLosesAuthentication_RefreshesAndReconnects()
    {
        await using TestSmtpServer server = new();
        server.FailMailWith530(1);

        int refreshCalls = 0;
        EmailSenderOptions options = CreateOAuth2Options(server.Port, "session-token", DateTime.UtcNow.AddMinutes(10), _ =>
        {
            Interlocked.Increment(ref refreshCalls);
            return ValueTask.FromResult(RefreshResult("refreshed-token"));
        });
        await using EmailSenderService service = CreateService(options);

        bool firstSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));
        bool secondSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.True(firstSend);
        Assert.True(secondSend);
        Assert.Equal(1, refreshCalls);
        Assert.Equal("refreshed-token", options.AccessToken);

        List<SmtpSession> mailSessions = server.MailSessions();
        Assert.Equal(2, mailSessions.Count);
        Assert.Equal(["session-token"], mailSessions[0].OAuthTokens);
        Assert.True(mailSessions[0].SentQuit, "the de-authenticated session should be torn down gracefully");
        Assert.Equal(["refreshed-token"], mailSessions[1].OAuthTokens);
        // The second message reuses the re-authenticated session - no third connection.
        Assert.Equal(2, mailSessions[1].DataCount);
    }

    [Fact]
    public async Task TrySendAsync_OAuth2CredentialsRefreshedCallbackFailure_DoesNotFailSend()
    {
        await using TestSmtpServer server = new();

        int callbackCalls = 0;
        EmailSenderOAuthRefreshResult? callbackResult = null;
        EmailSenderOptions options = CreateOAuth2Options(server.Port, "expired-token", DateTime.UtcNow.AddMinutes(-1),
            _ => ValueTask.FromResult(RefreshResult("replacement-token", "replacement-refresh-token")));
        options.OnOAuth2CredentialsRefreshed = (result, _) =>
        {
            callbackCalls++;
            callbackResult = result;
            throw new InvalidOperationException("User delegate failed.");
        };
        await using EmailSenderService service = CreateService(options);

        bool sent = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.True(sent);
        Assert.Equal(1, callbackCalls);
        Assert.NotNull(callbackResult);
        Assert.Equal("replacement-token", callbackResult.AccessToken);
        Assert.Equal("replacement-refresh-token", callbackResult.RefreshToken);
        Assert.Equal("replacement-token", options.AccessToken);
        Assert.Equal("replacement-refresh-token", options.RefreshToken);
    }

    [Fact]
    public async Task TrySendAsync_ReleasesConnectionSlotAfterFailure()
    {
        await using TestSmtpServer server = new();
        server.FailDataWith554(1);

        EmailSenderOptions options = CreateBasicOptions(server.Port, requiresAuthentication: false);
        options.RetryCount = 0;
        await using EmailSenderService service = CreateService(options);

        bool firstSend = await service.TrySendAsync(new EmailMessage("first@example.com", "Subject", "Body"));
        bool secondSend = await service.TrySendAsync(new EmailMessage("second@example.com", "Subject", "Body"));

        Assert.False(firstSend);
        Assert.True(secondSend);
        Assert.Equal(1, service.GetFailedMessagesCount());
    }

    [Fact]
    public async Task TrySendAsync_BasicAuthenticationFailure_ReportsAuthenticationFailed()
    {
        await using TestSmtpServer server = new();
        server.RequiredBasicPassword = "the-real-password";

        EmailFailureReason? failureReason = null;
        EmailSenderOptions options = CreateBasicOptions(server.Port, requiresAuthentication: true, password: "wrong-password");
        options.RetryCount = 0;
        options.OnEmailSendingFailure = (_, reason) =>
        {
            failureReason = reason;
            return ValueTask.CompletedTask;
        };
        await using EmailSenderService service = CreateService(options);

        bool sent = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.False(sent);
        Assert.Equal(EmailFailureReason.AuthenticationFailed, failureReason);
    }

    [Fact]
    public async Task TrySendAsync_OAuth2AuthenticationFailureAfterRefresh_ReportsAuthenticationFailed()
    {
        await using TestSmtpServer server = new();
        server.RequiredOAuthToken = "token-nobody-has";

        int refreshCalls = 0;
        EmailFailureReason? failureReason = null;
        EmailSenderOptions options = CreateOAuth2Options(server.Port, "initial-token", DateTime.UtcNow.AddMinutes(10), _ =>
        {
            Interlocked.Increment(ref refreshCalls);
            return ValueTask.FromResult(RefreshResult("replacement-token"));
        });
        options.RetryCount = 0;
        options.OnEmailSendingFailure = (_, reason) =>
        {
            failureReason = reason;
            return ValueTask.CompletedTask;
        };
        await using EmailSenderService service = CreateService(options);

        bool sent = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.False(sent);
        Assert.Equal(1, refreshCalls);
        Assert.Equal(EmailFailureReason.AuthenticationFailed, failureReason);
    }

    [Fact]
    public async Task TrySendAsync_OAuth2_DeadRefreshToken_FailsFastAndCoolsDown()
    {
        await using TestSmtpServer server = new();
        server.RequiredOAuthToken = "token-nobody-has";

        int refreshCalls = 0;
        EmailSenderOptions options = CreateOAuth2Options(server.Port, "expired-token", DateTime.UtcNow.AddMinutes(-5), _ =>
        {
            Interlocked.Increment(ref refreshCalls);
            throw new InvalidOperationException("Refresh token revoked.");
        });
        options.RetryCount = 0;
        await using EmailSenderService service = CreateService(options);

        bool firstSend = await service.TrySendAsync(new EmailMessage("first@example.com", "Subject", "Body"));
        bool secondSend = await service.TrySendAsync(new EmailMessage("second@example.com", "Subject", "Body"));

        Assert.False(firstSend);
        Assert.False(secondSend);
        Assert.Equal(1, refreshCalls);
        Assert.Equal(2, service.GetFailedMessagesCount());
    }

    [Fact]
    public async Task TrySendAsync_RuntimeMonitor_ActivatesConfigurationWhenItBecomesAvailable()
    {
        await using TestSmtpServer server = new();
        TestOptionsMonitor<EmailSenderOptions> monitor = new(new EmailSenderOptions());
        await using EmailSenderService service = CreateService(monitor);

        bool firstSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        monitor.Set(CreateBasicOptions(server.Port, requiresAuthentication: true));

        bool secondSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.False(firstSend);
        Assert.True(secondSend);
        SmtpSession session = Assert.Single(server.MailSessions());
        Assert.Equal("password", session.BasicPassword);
    }

    [Fact]
    public async Task TrySendAsync_RuntimeMonitor_RemovingConfiguration_MakesSenderUnavailable()
    {
        await using TestSmtpServer server = new();
        TestOptionsMonitor<EmailSenderOptions> monitor = new(CreateBasicOptions(server.Port, requiresAuthentication: false));
        await using EmailSenderService service = CreateService(monitor);

        bool firstSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        monitor.Set(new EmailSenderOptions());

        bool secondSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.True(firstSend);
        Assert.False(secondSend);
        Assert.Single(server.MailSessions());
    }

    [Fact]
    public async Task TrySendAsync_RuntimeMonitor_BasicToOAuth2_ReconnectsConnection()
    {
        await using TestSmtpServer server = new();
        TestOptionsMonitor<EmailSenderOptions> monitor = new(CreateBasicOptions(server.Port, requiresAuthentication: true));
        await using EmailSenderService service = CreateService(monitor);

        bool firstSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        monitor.Set(CreateOAuth2Options(server.Port, "oauth-token", DateTime.UtcNow.AddMinutes(30),
            static _ => ValueTask.FromResult(RefreshResult("unused-token"))));

        bool secondSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.True(firstSend);
        Assert.True(secondSend);
        List<SmtpSession> mailSessions = server.MailSessions();
        Assert.Equal(2, mailSessions.Count);
        Assert.Equal("password", mailSessions[0].BasicPassword);
        Assert.True(mailSessions[0].SentQuit);
        Assert.Equal(["oauth-token"], mailSessions[1].OAuthTokens);
    }

    [Fact]
    public async Task TrySendAsync_RuntimeMonitor_OAuth2ToBasic_ReconnectsConnection()
    {
        await using TestSmtpServer server = new();
        TestOptionsMonitor<EmailSenderOptions> monitor = new(CreateOAuth2Options(server.Port, "oauth-token", DateTime.UtcNow.AddMinutes(30),
            static _ => ValueTask.FromResult(RefreshResult("unused-token"))));
        await using EmailSenderService service = CreateService(monitor);

        bool firstSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        monitor.Set(CreateBasicOptions(server.Port, requiresAuthentication: true));

        bool secondSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.True(firstSend);
        Assert.True(secondSend);
        List<SmtpSession> mailSessions = server.MailSessions();
        Assert.Equal(2, mailSessions.Count);
        Assert.Equal(["oauth-token"], mailSessions[0].OAuthTokens);
        Assert.True(mailSessions[0].SentQuit);
        Assert.Equal("password", mailSessions[1].BasicPassword);
    }

    [Fact]
    public async Task TrySendAsync_RuntimeProvider_EqualOptions_DoesNotReconnect()
    {
        await using TestSmtpServer server = new();
        // A fresh, value-identical options instance per fetch must not produce a new
        // credential generation, and therefore no reconnect.
        FreshInstanceProvider provider = new(() => CreateBasicOptions(server.Port, requiresAuthentication: true));
        await using EmailSenderService service = CreateService(provider);

        bool firstSend = await service.TrySendAsync(new EmailMessage("first@example.com", "Subject", "Body"));
        bool secondSend = await service.TrySendAsync(new EmailMessage("second@example.com", "Subject", "Body"));

        Assert.True(firstSend);
        Assert.True(secondSend);
        SmtpSession session = Assert.Single(server.MailSessions());
        Assert.Equal(2, session.DataCount);
        Assert.Equal("password", session.BasicPassword);
    }

    [Fact]
    public async Task TrySendAsync_RuntimeProvider_StaleTokenRows_RefreshesOnceViaOverlay()
    {
        await using TestSmtpServer server = new();
        server.RequiredOAuthToken = "fresh-token";

        // The provider materializes a fresh instance with the same stale persisted token on
        // every fetch - the service's token overlay must prevent a refresh per message.
        int refreshCalls = 0;
        FreshInstanceProvider provider = new(() => CreateOAuth2Options(server.Port, "stale-row-token", DateTime.UtcNow.AddMinutes(-5), _ =>
        {
            Interlocked.Increment(ref refreshCalls);
            return ValueTask.FromResult(RefreshResult("fresh-token"));
        }));
        await using EmailSenderService service = CreateService(provider);

        Assert.True(await service.TrySendAsync(new EmailMessage("first@example.com", "Subject", "Body")));
        Assert.True(await service.TrySendAsync(new EmailMessage("second@example.com", "Subject", "Body")));
        Assert.True(await service.TrySendAsync(new EmailMessage("third@example.com", "Subject", "Body")));

        Assert.Equal(1, refreshCalls);
        SmtpSession session = Assert.Single(server.MailSessions());
        Assert.Equal(["fresh-token"], session.OAuthTokens);
        Assert.Equal(3, session.DataCount);
    }

    [Fact]
    public async Task TrySendAsync_RuntimeProvider_SourceRotatedToken_UsesItWithoutRefreshing()
    {
        await using TestSmtpServer server = new();

        int refreshCalls = 0;
        Func<CancellationToken, ValueTask<EmailSenderOAuthRefreshResult>> refresh = _ =>
        {
            Interlocked.Increment(ref refreshCalls);
            return ValueTask.FromResult(RefreshResult("callback-token"));
        };
        TestEmailSenderOptionsProvider provider = new(CreateOAuth2Options(server.Port, "provider-token-1", DateTime.UtcNow.AddMinutes(30), refresh));
        await using EmailSenderService service = CreateService(provider);

        bool firstSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        provider.Set(CreateOAuth2Options(server.Port, "provider-token-2", DateTime.UtcNow.AddMinutes(30), refresh));

        bool secondSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.True(firstSend);
        Assert.True(secondSend);
        // The source rotated the token itself - it is authoritative; the refresh delegate
        // must not be invoked, and the rotated token must be used on the wire.
        Assert.Equal(0, refreshCalls);
        List<SmtpSession> mailSessions = server.MailSessions();
        Assert.Equal(2, mailSessions.Count);
        Assert.Equal(["provider-token-1"], mailSessions[0].OAuthTokens);
        Assert.Equal(["provider-token-2"], mailSessions[1].OAuthTokens);
    }

    [Fact]
    public async Task TrySendAsync_RuntimeProvider_ChangedPassword_ReconnectsEachSlotAsUsed()
    {
        await using TestSmtpServer server = new();
        TestEmailSenderOptionsProvider provider = new(CreateBasicOptions(server.Port, requiresAuthentication: true, password: "password-1"));
        await using EmailSenderService service = CreateService(provider, maxConcurrentConnections: 2);

        // Hold both connection slots in-flight at once so both authenticate with password-1.
        SmtpDataStallGate firstGate = server.StallData(2);
        Task<bool> send1 = service.TrySendAsync(new EmailMessage("first@example.com", "Subject", "Body")).AsTask();
        Task<bool> send2 = service.TrySendAsync(new EmailMessage("second@example.com", "Subject", "Body")).AsTask();
        await firstGate.AllEntered.Task;
        firstGate.Release.SetResult();
        Assert.All(await Task.WhenAll(send1, send2), Assert.True);

        provider.Set(CreateBasicOptions(server.Port, requiresAuthentication: true, password: "password-2"));

        // Same again - both slots are now stale and must each rebuild with password-2.
        SmtpDataStallGate secondGate = server.StallData(2);
        Task<bool> send3 = service.TrySendAsync(new EmailMessage("third@example.com", "Subject", "Body")).AsTask();
        Task<bool> send4 = service.TrySendAsync(new EmailMessage("fourth@example.com", "Subject", "Body")).AsTask();
        await secondGate.AllEntered.Task;
        secondGate.Release.SetResult();
        Assert.All(await Task.WhenAll(send3, send4), Assert.True);

        List<SmtpSession> mailSessions = server.MailSessions();
        Assert.Equal(4, mailSessions.Count);
        Assert.Equal(2, mailSessions.Count(static s => s.BasicPassword == "password-1"));
        Assert.Equal(2, mailSessions.Count(static s => s.BasicPassword == "password-2"));
    }

    [Fact]
    public async Task TrySendAsync_OAuth2_RequiresAuthenticationFalse_StillUsesUsernameAsFrom()
    {
        await using TestSmtpServer server = new();

        // OAuth2 with a username that is an email, no FromAddress, and RequiresAuthentication
        // explicitly false - legal per the contract (RequiresAuthentication applies to Basic only).
        // The message must send using the username as From, not be dropped as "malformed".
        EmailSenderOptions options = CreateOAuth2Options(server.Port, "oauth-token", DateTime.UtcNow.AddMinutes(30),
            static _ => ValueTask.FromResult(RefreshResult("unused-token")));
        options.RequiresAuthentication = false;
        await using EmailSenderService service = CreateService(options);

        bool sent = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.True(sent);
        SmtpSession session = Assert.Single(server.MailSessions());
        Assert.Contains(session.Commands, static c => c.StartsWith("MAIL FROM:<mailer@example.com>", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TrySendAsync_BasicAuthRejected_FailsFastWithoutExhaustingRetries()
    {
        await using TestSmtpServer server = new();
        server.RequiredBasicPassword = "correct-password";

        EmailSenderOptions options = CreateBasicOptions(server.Port, requiresAuthentication: true, password: "wrong-password");
        options.RetryCount = 3;
        options.RetryDelayInMilliseconds = 1;
        await using EmailSenderService service = CreateService(options);

        bool sent = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.False(sent);
        // A pinned Basic credential snapshot cannot be corrected by retrying, so authentication
        // must be attempted exactly once - not RetryCount + 1 times.
        int authAttempts = server.SnapshotSessions()
            .Sum(s => s.Commands.Count(static c => c.StartsWith("AUTHPW", StringComparison.Ordinal)));
        Assert.Equal(1, authAttempts);
    }

    [Fact]
    public async Task GetOptions_ConcurrentSends_CoalesceOntoASingleProviderFetch()
    {
        await using TestSmtpServer server = new();
        ConcurrencyTrackingProvider provider = new(server.Port);
        await using EmailSenderService service = CreateService(provider, maxConcurrentConnections: 8);

        Task<bool>[] sends = Enumerable.Range(0, 8)
            .Select(_ => service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body")).AsTask())
            .ToArray();

        Assert.All(await Task.WhenAll(sends), Assert.True);

        // Single-flight: the options source is never queried concurrently, however many messages
        // are in flight at once.
        Assert.Equal(1, provider.MaxConcurrentCalls);
        // And concurrent callers coalesce onto one fetch, so far fewer than the 16 fetches
        // (8 schedule-time + 8 send-time) the un-coalesced path would have issued.
        Assert.True(provider.Calls < 8, $"expected coalesced fetches, got {provider.Calls}");
    }

    [Fact]
    public async Task TrySendAsync_PermanentRecipientRejection_NonExchangeWording_FailsFastAsInvalidAddress()
    {
        await using TestSmtpServer server = new();
        // Postfix-style permanent rejection - deliberately NOT Exchange's "5.1.3 Invalid address".
        // Classification must key off the 5xx status + RCPT context, not the message text.
        server.RejectRecipient("550 5.1.1 <target@example.com>: Recipient address rejected: User unknown", count: 10);

        EmailFailureReason? reason = null;
        EmailSenderOptions options = CreateBasicOptions(server.Port, requiresAuthentication: false);
        options.RetryCount = 3;
        options.RetryDelayInMilliseconds = 1;
        options.OnEmailSendingFailure = (_, r) =>
        {
            reason = r;
            return ValueTask.CompletedTask;
        };
        await using EmailSenderService service = CreateService(options);

        bool sent = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.False(sent);
        Assert.Equal(EmailFailureReason.InvalidAddress, reason);
        int rcptAttempts = server.SnapshotSessions()
            .Sum(s => s.Commands.Count(static c => c.StartsWith("RCPT", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, rcptAttempts); // fail-fast: a permanent 5xx recipient rejection is not retried
    }

    [Fact]
    public async Task TrySendAsync_PermanentSenderRejection_NonExchangeWording_FailsFastAsSendAsDenied()
    {
        await using TestSmtpServer server = new();
        // Generic 5xx sender rejection, no Exchange "5.2.252 SendAsDenied" text in sight.
        server.RejectSender("550 5.7.1 Sender address rejected: not authorized", count: 10);

        EmailFailureReason? reason = null;
        EmailSenderOptions options = CreateBasicOptions(server.Port, requiresAuthentication: false);
        options.RetryCount = 3;
        options.RetryDelayInMilliseconds = 1;
        options.OnEmailSendingFailure = (_, r) =>
        {
            reason = r;
            return ValueTask.CompletedTask;
        };
        await using EmailSenderService service = CreateService(options);

        bool sent = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.False(sent);
        Assert.Equal(EmailFailureReason.SendAsDenied, reason);
        int mailAttempts = server.SnapshotSessions()
            .Sum(s => s.Commands.Count(static c => c.StartsWith("MAIL", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, mailAttempts);
    }

    [Fact]
    public async Task TrySendAsync_TransientRecipientRejection_RetriesAndSucceeds()
    {
        await using TestSmtpServer server = new();
        // 4xx is transient - reject once, then accept. Must NOT fail-fast; the retry delivers.
        server.RejectRecipient("450 4.2.1 Mailbox temporarily unavailable", count: 1);

        EmailSenderOptions options = CreateBasicOptions(server.Port, requiresAuthentication: false);
        options.RetryCount = 3;
        options.RetryDelayInMilliseconds = 1;
        await using EmailSenderService service = CreateService(options);

        bool sent = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.True(sent);
        int rcptAttempts = server.SnapshotSessions()
            .Sum(s => s.Commands.Count(static c => c.StartsWith("RCPT", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(2, rcptAttempts); // one transient rejection, one accepted retry
    }

    private static EmailSenderService CreateService(EmailSenderOptions options, int maxConcurrentConnections = 1) =>
        CreateService(new TestOptionsMonitor<EmailSenderOptions>(options), maxConcurrentConnections);

    private static EmailSenderService CreateService(TestOptionsMonitor<EmailSenderOptions> monitor, int maxConcurrentConnections = 1) =>
        new(monitor, Startup(maxConcurrentConnections), NullLogger<EmailSenderService>.Instance);

    private static EmailSenderService CreateService(IEmailSenderOptionsProvider provider, int maxConcurrentConnections = 1) =>
        new(provider, Options.Create(Startup(maxConcurrentConnections)), NullLogger<EmailSenderService>.Instance);

    private static EmailSenderStartupOptions Startup(int maxConcurrentConnections) => new()
    {
        MaxConcurrentConnections = maxConcurrentConnections
    };

    private static EmailSenderOAuthRefreshResult RefreshResult(string accessToken, string? refreshToken = null) => new()
    {
        AccessToken = accessToken,
        RefreshToken = refreshToken,
        AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
    };

    private static EmailSenderOptions CreateBasicOptions(int port, bool requiresAuthentication, string password = "password") =>
        EmailSenderOptions.CreateBasic(
            host: "127.0.0.1",
            port: port,
            requiresAuthentication: requiresAuthentication,
            username: requiresAuthentication ? "mailer@example.com" : null,
            password: requiresAuthentication ? password : null,
            fromAddress: requiresAuthentication ? null : "from@example.com");

    private static EmailSenderOptions CreateOAuth2Options(
        int port,
        string? accessToken,
        DateTime accessTokenExpiresAtUtc,
        Func<CancellationToken, ValueTask<EmailSenderOAuthRefreshResult>> refreshAccessTokenAsync) =>
        EmailSenderOptions.CreateOAuth2(
            host: "127.0.0.1",
            port: port,
            username: "mailer@example.com",
            refreshAccessTokenAsync: refreshAccessTokenAsync,
            accessToken: accessToken,
            accessTokenExpiresAtUtc: accessTokenExpiresAtUtc);
}

internal sealed class TestOptionsMonitor<TOptions>(TOptions currentValue) : IOptionsMonitor<TOptions>
    where TOptions : class
{
    private readonly object _lock = new();
    private TOptions _currentValue = currentValue;

    public TOptions CurrentValue
    {
        get
        {
            lock (_lock)
            {
                return _currentValue;
            }
        }
    }

    public TOptions Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<TOptions, string?> listener) => null;

    public void Set(TOptions value)
    {
        lock (_lock)
        {
            _currentValue = value;
        }
    }
}

internal sealed class TestEmailSenderOptionsProvider(EmailSenderOptions? options) : IEmailSenderOptionsProvider
{
    private readonly object _lock = new();
    private EmailSenderOptions? _options = options;

    public int Calls { get; private set; }

    public ValueTask<EmailSenderOptions?> GetOptionsAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Calls++;
            return ValueTask.FromResult(_options);
        }
    }

    public void Set(EmailSenderOptions? options)
    {
        lock (_lock)
        {
            _options = options;
        }
    }
}

/// <summary>
/// Materializes a brand-new options instance on every fetch, the way a database-backed
/// provider would - nothing the service mutates on a fetched instance survives to the next.
/// </summary>
internal sealed class FreshInstanceProvider(Func<EmailSenderOptions?> factory) : IEmailSenderOptionsProvider
{
    public ValueTask<EmailSenderOptions?> GetOptionsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(factory());
}

/// <summary>
/// Records how many times it is queried and the peak number of *concurrent* queries. Each call
/// holds briefly, so absent single-flight coalescing, overlapping callers would push the peak above 1.
/// </summary>
internal sealed class ConcurrencyTrackingProvider(int port) : IEmailSenderOptionsProvider
{
    private int _inside;

    public int Calls;
    public int MaxConcurrentCalls;

    public async ValueTask<EmailSenderOptions?> GetOptionsAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        int concurrent = Interlocked.Increment(ref _inside);
        for (int observed = Volatile.Read(ref MaxConcurrentCalls);
             observed < concurrent && Interlocked.CompareExchange(ref MaxConcurrentCalls, concurrent, observed) != observed;
             observed = Volatile.Read(ref MaxConcurrentCalls))
        {
        }

        try
        {
            await Task.Delay(50, cancellationToken);
            return EmailSenderOptions.CreateBasic("127.0.0.1", port, requiresAuthentication: false, fromAddress: "from@example.com");
        }
        finally
        {
            Interlocked.Decrement(ref _inside);
        }
    }
}
