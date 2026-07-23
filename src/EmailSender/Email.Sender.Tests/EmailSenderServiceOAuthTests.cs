using MailKit;
using MailKit.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using ReconArt.Email.Sender.Internal;
using System.ComponentModel.DataAnnotations;
using System.Net;
using Xunit;

namespace ReconArt.Email.Sender.Tests;

public sealed class EmailSenderServiceOAuthTests
{
    [Fact]
    public void CreateBasic_WithoutAuthenticationAndWithoutFromAddress_Throws()
    {
        Assert.Throws<ValidationException>(() =>
            EmailSenderOptions.CreateBasic("smtp.example.com", 25, requiresAuthentication: false));
    }

    [Fact]
    public void Validate_OAuth2Options_RequiresAllOAuthFields()
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
        Assert.Contains(results, static x => x.MemberNames.Contains(nameof(EmailSenderOptions.AccessToken)));
        Assert.Contains(results, static x => x.MemberNames.Contains(nameof(EmailSenderOptions.AccessTokenExpiresAtUtc)));
        Assert.Contains(results, static x => x.MemberNames.Contains(nameof(EmailSenderOptions.RefreshAccessTokenAsync)));
    }

    [Fact]
    public async Task TestConnectionAsync_BasicWithoutAuthentication_DoesNotAuthenticate()
    {
        EmailSenderOptions options = EmailSenderOptions.CreateBasic(
            host: "smtp.example.com",
            port: 25,
            requiresAuthentication: false,
            fromAddress: "from@example.com");
        options.MaxConcurrentConnections = 1;

        FakeSmtpClientFactory factory = new(static () => new FakeSmtpClient());
        await using EmailSenderService service = CreateService(options, factory);

        Exception? exception = await service.TestConnectionAsync();

        Assert.Null(exception);
        FakeSmtpClient client = Assert.Single(factory.CreatedClients.Skip(1));
        Assert.Equal(1, client.ConnectCount);
        Assert.Equal(0, client.BasicAuthenticationCount);
        Assert.Equal(0, client.OAuthAuthenticationCount);
    }

    [Fact]
    public async Task TestConnectionAsync_OAuth2_UsesOAuthAuthenticationAndRefreshesExpiredToken()
    {
        int refreshCalls = 0;
        EmailSenderOptions options = EmailSenderOptions.CreateOAuth2(
            host: "smtp.example.com",
            port: 587,
            username: "mailer@example.com",
            accessToken: "expired-token",
            accessTokenExpiresAtUtc: DateTime.UtcNow.AddMinutes(-5),
            refreshAccessTokenAsync: _ =>
            {
                refreshCalls++;
                return ValueTask.FromResult(new EmailSenderOAuthRefreshResult
                {
                    AccessToken = "fresh-token",
                    AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
                });
            });
        options.MaxConcurrentConnections = 1;

        FakeSmtpClientFactory factory = new(static () => new FakeSmtpClient());
        await using EmailSenderService service = CreateService(options, factory);

        Exception? exception = await service.TestConnectionAsync();

        Assert.Null(exception);
        Assert.Equal(1, refreshCalls);
        Assert.Equal("fresh-token", options.AccessToken);
        FakeSmtpClient client = Assert.Single(factory.CreatedClients.Skip(1));
        Assert.Equal(0, client.BasicAuthenticationCount);
        Assert.Equal(1, client.OAuthAuthenticationCount);
        Assert.Equal("fresh-token", Assert.Single(client.OAuthAccessTokens));
    }

    [Fact]
    public async Task TestConnectionAsync_OAuth2_ConcurrentAuthenticationFailure_RefreshesOnce()
    {
        int refreshCalls = 0;
        EmailSenderOptions options = EmailSenderOptions.CreateOAuth2(
            host: "smtp.example.com",
            port: 587,
            username: "mailer@example.com",
            accessToken: "initial-token",
            accessTokenExpiresAtUtc: DateTime.UtcNow.AddMinutes(10),
            refreshAccessTokenAsync: async _ =>
            {
                Interlocked.Increment(ref refreshCalls);
                await Task.Delay(100);
                return new EmailSenderOAuthRefreshResult
                {
                    AccessToken = "replacement-token",
                    AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
                };
            });
        options.MaxConcurrentConnections = 1;

        FakeSmtpClientFactory factory = new(static () => new FakeSmtpClient
        {
            AuthenticateOAuthAsyncHandler = (mechanism, _) =>
            {
                NetworkCredential? credentials = ((SaslMechanismOAuth2)mechanism)
                    .Credentials.GetCredential(new Uri("smtp://localhost"), mechanism.MechanismName);

                if (credentials?.Password == "initial-token")
                {
                    throw new MailKit.Security.AuthenticationException("Initial token rejected.");
                }

                return Task.CompletedTask;
            }
        });
        await using EmailSenderService service = CreateService(options, factory);

        Exception?[] results = await Task.WhenAll(
            service.TestConnectionAsync().AsTask(),
            service.TestConnectionAsync().AsTask());

        Assert.All(results, Assert.Null);
        Assert.Equal(1, refreshCalls);
        Assert.Equal("replacement-token", options.AccessToken);
        Assert.Equal(3, factory.CreatedClients.Count);
        Assert.All(factory.CreatedClients.Skip(1), client => Assert.Equal(["initial-token", "replacement-token"], client.OAuthAccessTokens));
    }

    [Fact]
    public async Task TrySendAsync_OAuth2_ConcurrentExpiredToken_RefreshesOnce()
    {
        int refreshCalls = 0;
        EmailSenderOptions options = EmailSenderOptions.CreateOAuth2(
            host: "smtp.example.com",
            port: 587,
            username: "mailer@example.com",
            accessToken: "expired-token",
            accessTokenExpiresAtUtc: DateTime.UtcNow.AddMinutes(-1),
            refreshAccessTokenAsync: async _ =>
            {
                Interlocked.Increment(ref refreshCalls);
                await Task.Delay(100);
                return new EmailSenderOAuthRefreshResult
                {
                    AccessToken = "refreshed-token",
                    AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
                };
            });
        options.MaxConcurrentConnections = 2;

        FakeSmtpClientFactory factory = new(static () => new FakeSmtpClient());
        await using EmailSenderService service = CreateService(options, factory);

        Task<bool> firstSend = service.TrySendAsync(new EmailMessage("first@example.com", "Subject", "Body")).AsTask();
        Task<bool> secondSend = service.TrySendAsync(new EmailMessage("second@example.com", "Subject", "Body")).AsTask();

        bool[] results = await Task.WhenAll(firstSend, secondSend);

        Assert.All(results, Assert.True);
        Assert.Equal(1, refreshCalls);
        Assert.Equal("refreshed-token", options.AccessToken);
        Assert.Equal(2, factory.CreatedClients.Count);
        Assert.All(factory.CreatedClients, client => Assert.Equal(1, client.OAuthAuthenticationCount));
    }

    [Fact]
    public async Task TrySendAsync_OAuth2_SendLosesAuthentication_RefreshesAndReconnects()
    {
        int refreshCalls = 0;
        EmailSenderOptions options = EmailSenderOptions.CreateOAuth2(
            host: "smtp.example.com",
            port: 587,
            username: "mailer@example.com",
            accessToken: "initial-token",
            accessTokenExpiresAtUtc: DateTime.UtcNow.AddMinutes(10),
            refreshAccessTokenAsync: _ =>
            {
                refreshCalls++;
                return ValueTask.FromResult(new EmailSenderOAuthRefreshResult
                {
                    AccessToken = "replacement-token",
                    AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
                });
            });
        options.MaxConcurrentConnections = 1;

        FakeSmtpClient smtpClient = new();
        int sendAttempts = 0;
        smtpClient.SendAsyncHandler = (_, _) =>
        {
            if (Interlocked.Increment(ref sendAttempts) == 1)
            {
                smtpClient.SetAuthenticationState(false);
                throw new ServiceNotAuthenticatedException("Authentication expired.");
            }

            return Task.CompletedTask;
        };

        FakeSmtpClientFactory factory = new(() => smtpClient);
        await using EmailSenderService service = CreateService(options, factory);

        bool sent = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.True(sent);
        Assert.Equal(1, refreshCalls);
        Assert.Equal("replacement-token", options.AccessToken);
        Assert.Equal(2, smtpClient.ConnectCount);
        Assert.Equal(1, smtpClient.DisconnectCount);
        Assert.Equal(2, smtpClient.OAuthAuthenticationCount);
        Assert.Equal(["initial-token", "replacement-token"], smtpClient.OAuthAccessTokens);
    }

    [Fact]
    public async Task TrySendAsync_OAuth2_ClearsReconnectFlagAfterSuccessfulReconnect()
    {
        int refreshCalls = 0;
        EmailSenderOptions options = EmailSenderOptions.CreateOAuth2(
            host: "smtp.example.com",
            port: 587,
            username: "mailer@example.com",
            accessToken: "initial-token",
            accessTokenExpiresAtUtc: DateTime.UtcNow.AddMinutes(10),
            refreshAccessTokenAsync: _ =>
            {
                refreshCalls++;
                return ValueTask.FromResult(new EmailSenderOAuthRefreshResult
                {
                    AccessToken = "replacement-token",
                    AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
                });
            });
        options.MaxConcurrentConnections = 1;

        FakeSmtpClient smtpClient = new();
        int sendAttempts = 0;
        smtpClient.SendAsyncHandler = (_, _) =>
        {
            if (Interlocked.Increment(ref sendAttempts) == 1)
            {
                smtpClient.SetAuthenticationState(false);
                throw new ServiceNotAuthenticatedException("Authentication expired.");
            }

            return Task.CompletedTask;
        };

        FakeSmtpClientFactory factory = new(() => smtpClient);
        await using EmailSenderService service = CreateService(options, factory);

        bool firstSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));
        bool secondSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.True(firstSend);
        Assert.True(secondSend);
        Assert.Equal(1, refreshCalls);
        Assert.Equal(2, smtpClient.ConnectCount);
        Assert.Equal(1, smtpClient.DisconnectCount);
        Assert.Equal(2, smtpClient.OAuthAuthenticationCount);
    }

    [Fact]
    public async Task TrySendAsync_OAuth2CredentialsRefreshedCallbackFailure_DoesNotFailSend()
    {
        int callbackCalls = 0;
        EmailSenderOAuthRefreshResult? callbackResult = null;
        EmailSenderOptions options = EmailSenderOptions.CreateOAuth2(
            host: "smtp.example.com",
            port: 587,
            username: "mailer@example.com",
            accessToken: "expired-token",
            accessTokenExpiresAtUtc: DateTime.UtcNow.AddMinutes(-1),
            refreshAccessTokenAsync: _ => ValueTask.FromResult(new EmailSenderOAuthRefreshResult
            {
                AccessToken = "replacement-token",
                RefreshToken = "replacement-refresh-token",
                AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
            }));
        options.MaxConcurrentConnections = 1;
        options.OnOAuth2CredentialsRefreshed = (result, _) =>
        {
            callbackCalls++;
            callbackResult = result;
            throw new InvalidOperationException("User delegate failed.");
        };

        FakeSmtpClientFactory factory = new(static () => new FakeSmtpClient());
        await using EmailSenderService service = CreateService(options, factory);

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
        EmailSenderOptions options = EmailSenderOptions.CreateBasic(
            host: "smtp.example.com",
            port: 25,
            requiresAuthentication: false,
            fromAddress: "from@example.com");
        options.MaxConcurrentConnections = 1;
        options.RetryCount = 0;

        FakeSmtpClient smtpClient = new();
        int attempts = 0;
        smtpClient.SendAsyncHandler = (_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new InvalidOperationException("Simulated send failure.");
            }

            return Task.CompletedTask;
        };

        FakeSmtpClientFactory factory = new(() => smtpClient);
        await using EmailSenderService service = CreateService(options, factory);

        bool firstSend = await service.TrySendAsync(new EmailMessage("first@example.com", "Subject", "Body"));
        bool secondSend = await service.TrySendAsync(new EmailMessage("second@example.com", "Subject", "Body"));

        Assert.False(firstSend);
        Assert.True(secondSend);
        Assert.Equal(2, smtpClient.SendCount);
    }

    [Fact]
    public async Task TrySendAsync_BasicAuthenticationFailure_ReportsAuthenticationFailed()
    {
        EmailFailureReason? failureReason = null;
        EmailSenderOptions options = EmailSenderOptions.CreateBasic(
            host: "smtp.example.com",
            port: 587,
            requiresAuthentication: true,
            username: "mailer@example.com",
            password: "password");
        options.MaxConcurrentConnections = 1;
        options.RetryCount = 0;
        options.OnEmailSendingFailure = (_, reason) =>
        {
            failureReason = reason;
            return ValueTask.CompletedTask;
        };

        FakeSmtpClient smtpClient = new()
        {
            AuthenticateBasicAsyncHandler = (_, _, _) => throw new MailKit.Security.AuthenticationException("Basic auth rejected.")
        };

        FakeSmtpClientFactory factory = new(() => smtpClient);
        await using EmailSenderService service = CreateService(options, factory);

        bool sent = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.False(sent);
        Assert.Equal(EmailFailureReason.AuthenticationFailed, failureReason);
    }

    [Fact]
    public async Task TrySendAsync_OAuth2AuthenticationFailureAfterRefresh_ReportsAuthenticationFailed()
    {
        int refreshCalls = 0;
        EmailFailureReason? failureReason = null;
        EmailSenderOptions options = EmailSenderOptions.CreateOAuth2(
            host: "smtp.example.com",
            port: 587,
            username: "mailer@example.com",
            accessToken: "initial-token",
            accessTokenExpiresAtUtc: DateTime.UtcNow.AddMinutes(10),
            refreshAccessTokenAsync: _ =>
            {
                refreshCalls++;
                return ValueTask.FromResult(new EmailSenderOAuthRefreshResult
                {
                    AccessToken = "replacement-token",
                    AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
                });
            });
        options.MaxConcurrentConnections = 1;
        options.RetryCount = 0;
        options.OnEmailSendingFailure = (_, reason) =>
        {
            failureReason = reason;
            return ValueTask.CompletedTask;
        };

        FakeSmtpClient smtpClient = new()
        {
            AuthenticateOAuthAsyncHandler = (mechanism, _) =>
            {
                NetworkCredential? credentials = ((SaslMechanismOAuth2)mechanism)
                    .Credentials.GetCredential(new Uri("smtp://localhost"), mechanism.MechanismName);

                throw new MailKit.Security.AuthenticationException($"OAuth token rejected: {credentials?.Password}");
            }
        };

        FakeSmtpClientFactory factory = new(() => smtpClient);
        await using EmailSenderService service = CreateService(options, factory);

        bool sent = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.False(sent);
        Assert.Equal(1, refreshCalls);
        Assert.Equal(EmailFailureReason.AuthenticationFailed, failureReason);
    }

    [Fact]
    public async Task TrySendAsync_RuntimeMonitor_ActivatesBasicConfigurationWhenItBecomesAvailable()
    {
        TestOptionsMonitor<EmailSenderOptions> monitor = new(CreateUnavailableOptions());

        FakeSmtpClientFactory factory = new(static () => new FakeSmtpClient());
        await using EmailSenderService service = CreateService(monitor, factory);

        bool firstSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        monitor.Set(CreateBasicOptions(requiresAuthentication: true));

        bool secondSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.False(firstSend);
        Assert.True(secondSend);
        FakeSmtpClient client = Assert.Single(factory.CreatedClients);
        Assert.Equal(1, client.ConnectCount);
        Assert.Equal(1, client.BasicAuthenticationCount);
    }

    [Fact]
    public async Task TrySendAsync_RuntimeMonitor_BasicToOAuth2_ReconnectsSlotClient()
    {
        TestOptionsMonitor<EmailSenderOptions> monitor = new(CreateBasicOptions(requiresAuthentication: true));

        FakeSmtpClientFactory factory = new(static () => new FakeSmtpClient());
        await using EmailSenderService service = CreateService(monitor, factory);

        bool firstSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        monitor.Set(CreateOAuth2Options(
                accessToken: "oauth-token",
                accessTokenExpiresAtUtc: DateTime.UtcNow.AddMinutes(30),
                refreshAccessTokenAsync: _ => ValueTask.FromResult(new EmailSenderOAuthRefreshResult
                {
                    AccessToken = "oauth-refresh-token",
                    AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(60)
                })));

        bool secondSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.True(firstSend);
        Assert.True(secondSend);
        FakeSmtpClient client = Assert.Single(factory.CreatedClients);
        Assert.Equal(1, client.BasicAuthenticationCount);
        Assert.Equal(1, client.OAuthAuthenticationCount);
        Assert.Equal(["oauth-token"], client.OAuthAccessTokens);
        Assert.Equal(1, client.DisconnectCount);
    }

    [Fact]
    public async Task TrySendAsync_RuntimeMonitor_OAuth2ToBasic_ReconnectsSlotClient()
    {
        TestOptionsMonitor<EmailSenderOptions> monitor = new(CreateOAuth2Options(
                accessToken: "oauth-token",
                accessTokenExpiresAtUtc: DateTime.UtcNow.AddMinutes(30),
                refreshAccessTokenAsync: _ => ValueTask.FromResult(new EmailSenderOAuthRefreshResult
                {
                    AccessToken = "oauth-refresh-token",
                    AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(60)
                })));

        FakeSmtpClientFactory factory = new(static () => new FakeSmtpClient());
        await using EmailSenderService service = CreateService(monitor, factory);

        bool firstSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        monitor.Set(CreateBasicOptions(requiresAuthentication: true));

        bool secondSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.True(firstSend);
        Assert.True(secondSend);
        FakeSmtpClient client = Assert.Single(factory.CreatedClients);
        Assert.Equal(1, client.OAuthAuthenticationCount);
        Assert.Equal(1, client.BasicAuthenticationCount);
        Assert.Equal(1, client.DisconnectCount);
    }

    [Fact]
    public async Task TrySendAsync_RuntimeMonitor_InFlightSendUsesCurrentOptionsAfterReconnect()
    {
        TestOptionsMonitor<EmailSenderOptions> monitor = new(CreateBasicOptions(requiresAuthentication: true));

        TaskCompletionSource noOpStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseNoOp = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int clientCount = 0;

        FakeSmtpClientFactory factory = new(() =>
        {
            clientCount++;
            if (clientCount == 1)
            {
                return new FakeSmtpClient
                {
                    NoOpAsyncHandler = async cancellationToken =>
                    {
                        noOpStarted.TrySetResult();
                        await releaseNoOp.Task.WaitAsync(cancellationToken);
                    }
                };
            }

            return new FakeSmtpClient();
        });

        await using EmailSenderService service = CreateService(monitor, factory);

        bool firstSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));
        Task<bool> secondSendTask = service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body")).AsTask();

        await noOpStarted.Task;

        monitor.Set(CreateOAuth2Options(
                accessToken: "oauth-token",
                accessTokenExpiresAtUtc: DateTime.UtcNow.AddMinutes(30),
                refreshAccessTokenAsync: _ => ValueTask.FromResult(new EmailSenderOAuthRefreshResult
                {
                    AccessToken = "oauth-refresh-token",
                    AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(60)
                })));

        Exception? testConnectionException = await service.TestConnectionAsync();
        releaseNoOp.SetResult();
        bool secondSend = await secondSendTask;
        bool thirdSend = await service.TrySendAsync(new EmailMessage("third@example.com", "Subject", "Body"));

        Assert.True(firstSend);
        Assert.Null(testConnectionException);
        Assert.True(secondSend);
        Assert.True(thirdSend);
        Assert.Equal(2, factory.CreatedClients.Count);
        Assert.Equal(1, factory.CreatedClients[0].BasicAuthenticationCount);
        Assert.Equal(1, factory.CreatedClients[0].OAuthAuthenticationCount);
        Assert.Equal(1, factory.CreatedClients[1].OAuthAuthenticationCount);
        Assert.Equal(["oauth-token"], factory.CreatedClients[0].OAuthAccessTokens);
        Assert.Equal(["oauth-token"], factory.CreatedClients[1].OAuthAccessTokens);
    }

    [Fact]
    public async Task TrySendAsync_RuntimeMonitor_RemovingConfiguration_MakesSenderUnavailable()
    {
        TestOptionsMonitor<EmailSenderOptions> monitor = new(CreateBasicOptions(requiresAuthentication: false));

        FakeSmtpClientFactory factory = new(static () => new FakeSmtpClient());
        await using EmailSenderService service = CreateService(monitor, factory);

        bool firstSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));
        FakeSmtpClient client = Assert.Single(factory.CreatedClients);

        monitor.Set(CreateUnavailableOptions());

        bool secondSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.True(firstSend);
        Assert.False(secondSend);
        Assert.Equal(0, client.DisconnectCount);
    }

    [Fact]
    public async Task TestConnectionAsync_RuntimeMonitor_WithoutValidConfiguration_ReturnsValidationException()
    {
        TestOptionsMonitor<EmailSenderOptions> monitor = new(CreateUnavailableOptions());

        FakeSmtpClientFactory factory = new(static () => new FakeSmtpClient());
        await using EmailSenderService service = CreateService(monitor, factory);

        Exception? exception = await service.TestConnectionAsync();

        Assert.IsType<ValidationException>(exception);
        Assert.Single(factory.CreatedClients);
    }

    [Fact]
    public async Task TrySendAsync_RuntimeMonitor_OAuthRefreshMutatesCurrentOptionsInstance()
    {
        int refreshCalls = 0;
        EmailSenderOptions options = CreateOAuth2Options(
            accessToken: "initial-token",
            accessTokenExpiresAtUtc: DateTime.UtcNow.AddMinutes(10),
            refreshAccessTokenAsync: _ =>
            {
                refreshCalls++;
                return ValueTask.FromResult(new EmailSenderOAuthRefreshResult
                {
                    AccessToken = "replacement-token",
                    AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
                });
            });
        TestOptionsMonitor<EmailSenderOptions> monitor = new(options);

        FakeSmtpClient smtpClient = new()
        {
            AuthenticateOAuthAsyncHandler = (mechanism, _) =>
            {
                NetworkCredential? credentials = ((SaslMechanismOAuth2)mechanism)
                    .Credentials.GetCredential(new Uri("smtp://localhost"), mechanism.MechanismName);

                if (credentials?.Password == "initial-token")
                {
                    throw new MailKit.Security.AuthenticationException("Initial token rejected.");
                }

                return Task.CompletedTask;
            }
        };

        FakeSmtpClientFactory factory = new(() => smtpClient);
        await using EmailSenderService service = CreateService(monitor, factory);

        bool firstSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));
        smtpClient.SetAuthenticationState(false);
        bool secondSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.True(firstSend);
        Assert.True(secondSend);
        Assert.Equal(1, refreshCalls);
        Assert.Equal("replacement-token", options.AccessToken);
        Assert.Equal(["initial-token", "replacement-token", "replacement-token"], smtpClient.OAuthAccessTokens);
        Assert.Single(factory.CreatedClients);
    }

    [Fact]
    public async Task TrySendAsync_RuntimeProvider_ChangedOptionsAfterAuthenticationFailure_RetriesWithFetchedToken()
    {
        int refreshCalls = 0;
        TestEmailSenderOptionsProvider provider = new(CreateOAuth2Options(
            accessToken: "initial-token",
            accessTokenExpiresAtUtc: DateTime.UtcNow.AddMinutes(10),
            refreshAccessTokenAsync: _ =>
            {
                refreshCalls++;
                return ValueTask.FromResult(new EmailSenderOAuthRefreshResult
                {
                    AccessToken = "callback-token",
                    AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
                });
            }));

        int providerCalls = 0;
        provider.OnGetOptions = () =>
        {
            if (Interlocked.Increment(ref providerCalls) == 3)
            {
                provider.Set(CreateOAuth2Options(
                    accessToken: "provider-token",
                    accessTokenExpiresAtUtc: DateTime.UtcNow.AddMinutes(30),
                    refreshAccessTokenAsync: _ =>
                    {
                        refreshCalls++;
                        return ValueTask.FromResult(new EmailSenderOAuthRefreshResult
                        {
                            AccessToken = "callback-token",
                            AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
                        });
                    }));
            }
        };

        FakeSmtpClientFactory factory = new(static () => new FakeSmtpClient
        {
            AuthenticateOAuthAsyncHandler = (mechanism, _) =>
            {
                NetworkCredential? credentials = ((SaslMechanismOAuth2)mechanism)
                    .Credentials.GetCredential(new Uri("smtp://localhost"), mechanism.MechanismName);

                if (credentials?.Password == "initial-token")
                {
                    throw new MailKit.Security.AuthenticationException("Initial token rejected.");
                }

                return Task.CompletedTask;
            }
        });
        await using EmailSenderService service = CreateService(provider, factory);

        bool sent = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.True(sent);
        Assert.Equal(0, refreshCalls);
        FakeSmtpClient client = Assert.Single(factory.CreatedClients);
        Assert.Equal(["initial-token", "provider-token"], client.OAuthAccessTokens);
        Assert.Equal(1, client.DisconnectCount);
    }

    [Fact]
    public async Task TrySendAsync_RuntimeProvider_EqualOptions_DoesNotReconnect()
    {
        TestEmailSenderOptionsProvider provider = new(CreateBasicOptions(requiresAuthentication: true));

        FakeSmtpClientFactory factory = new(static () => new FakeSmtpClient());
        await using EmailSenderService service = CreateService(provider, factory);

        bool firstSend = await service.TrySendAsync(new EmailMessage("first@example.com", "Subject", "Body"));
        bool secondSend = await service.TrySendAsync(new EmailMessage("second@example.com", "Subject", "Body"));

        Assert.True(firstSend);
        Assert.True(secondSend);
        Assert.Single(factory.CreatedClients);
        Assert.Equal(1, factory.CreatedClients[0].ConnectCount);
        Assert.Equal(1, factory.CreatedClients[0].BasicAuthenticationCount);
    }

    [Fact]
    public async Task TrySendAsync_RuntimeProvider_ChangedOptions_ReconnectsFlaggedSlotsAsTheyAreUsed()
    {
        EmailSenderOptions initialOptions = EmailSenderOptions.CreateBasic(
            host: "smtp.example.com",
            port: 587,
            requiresAuthentication: true,
            username: "mailer@example.com",
            password: "initial-password");
        initialOptions.MaxConcurrentConnections = 2;

        EmailSenderOptions replacementOptions = EmailSenderOptions.CreateBasic(
            host: "smtp.example.com",
            port: 587,
            requiresAuthentication: true,
            username: "mailer@example.com",
            password: "replacement-password");
        replacementOptions.MaxConcurrentConnections = 2;

        TestEmailSenderOptionsProvider provider = new(initialOptions);
        TaskCompletionSource firstSendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirstSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int clientIndex = 0;

        FakeSmtpClientFactory factory = new(() =>
        {
            int index = Interlocked.Increment(ref clientIndex);
            return new FakeSmtpClient
            {
                SendAsyncHandler = async (_, cancellationToken) =>
                {
                    if (index == 1)
                    {
                        firstSendStarted.TrySetResult();
                        await releaseFirstSend.Task.WaitAsync(cancellationToken);
                    }
                }
            };
        });
        await using EmailSenderService service = CreateService(provider, factory);

        Task<bool> firstSendTask = service.TrySendAsync(new EmailMessage("first@example.com", "Subject", "Body")).AsTask();
        await firstSendStarted.Task;

        provider.Set(replacementOptions);

        bool secondSend = await service.TrySendAsync(new EmailMessage("second@example.com", "Subject", "Body"));
        releaseFirstSend.SetResult();
        bool firstSend = await firstSendTask;
        bool thirdSend = await service.TrySendAsync(new EmailMessage("third@example.com", "Subject", "Body"));

        Assert.True(firstSend);
        Assert.True(secondSend);
        Assert.True(thirdSend);
        Assert.Equal(2, factory.CreatedClients.Count);
        Assert.Equal(2, factory.CreatedClients[0].BasicAuthenticationCount);
        Assert.Equal(1, factory.CreatedClients[1].BasicAuthenticationCount);
        Assert.Equal(1, factory.CreatedClients[0].DisconnectCount);
    }

    private static EmailSenderService CreateService(EmailSenderOptions options, FakeSmtpClientFactory factory) =>
        new(new TestOptionsMonitor<EmailSenderOptions>(options), NullLogger<EmailSenderService>.Instance, factory.Create);

    private static EmailSenderService CreateService(TestOptionsMonitor<EmailSenderOptions> monitor, FakeSmtpClientFactory factory) =>
        new(monitor, NullLogger<EmailSenderService>.Instance, factory.Create);

    private static EmailSenderService CreateService(TestEmailSenderOptionsProvider provider, FakeSmtpClientFactory factory) =>
        new(provider, NullLogger<EmailSenderService>.Instance, factory.Create);

    private static EmailSenderOptions CreateUnavailableOptions() => new()
    {
        MaxConcurrentConnections = 1
    };

    private static EmailSenderOptions CreateBasicOptions(bool requiresAuthentication)
    {
        EmailSenderOptions options = EmailSenderOptions.CreateBasic(
            host: "smtp.example.com",
            port: requiresAuthentication ? 587 : 25,
            requiresAuthentication: requiresAuthentication,
            username: requiresAuthentication ? "mailer@example.com" : null,
            password: requiresAuthentication ? "password" : null,
            fromAddress: requiresAuthentication ? null : "from@example.com");
        options.MaxConcurrentConnections = 1;
        return options;
    }

    private static EmailSenderOptions CreateOAuth2Options(
        string accessToken,
        DateTime accessTokenExpiresAtUtc,
        Func<CancellationToken, ValueTask<EmailSenderOAuthRefreshResult>> refreshAccessTokenAsync)
    {
        EmailSenderOptions options = EmailSenderOptions.CreateOAuth2(
            host: "smtp.example.com",
            port: 587,
            username: "mailer@example.com",
            accessToken: accessToken,
            accessTokenExpiresAtUtc: accessTokenExpiresAtUtc,
            refreshAccessTokenAsync: refreshAccessTokenAsync);
        options.MaxConcurrentConnections = 1;
        return options;
    }
}

internal sealed class TestOptionsMonitor<TOptions>(TOptions currentValue) : IOptionsMonitor<TOptions>
    where TOptions : class
{
    private readonly object _lock = new();
    private readonly List<Action<TOptions, string?>> _listeners = [];
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

    public IDisposable? OnChange(Action<TOptions, string?> listener)
    {
        lock (_lock)
        {
            _listeners.Add(listener);
        }

        return new ChangeListener(this, listener);
    }

    public void Set(TOptions value)
    {
        Action<TOptions, string?>[] listeners;
        lock (_lock)
        {
            _currentValue = value;
            listeners = [.. _listeners];
        }

        foreach (Action<TOptions, string?> listener in listeners)
        {
            listener(value, Options.DefaultName);
        }
    }

    private void Remove(Action<TOptions, string?> listener)
    {
        lock (_lock)
        {
            _listeners.Remove(listener);
        }
    }

    private sealed class ChangeListener(TestOptionsMonitor<TOptions> owner, Action<TOptions, string?> listener) : IDisposable
    {
        public void Dispose() => owner.Remove(listener);
    }
}

internal sealed class TestEmailSenderOptionsProvider(EmailSenderOptions? options) : IEmailSenderOptionsProvider
{
    private readonly object _lock = new();
    private EmailSenderOptions? _options = options;

    public Action? OnGetOptions { get; set; }

    public ValueTask<EmailSenderOptions?> GetOptionsAsync(CancellationToken cancellationToken)
    {
        OnGetOptions?.Invoke();

        lock (_lock)
        {
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

internal sealed class FakeSmtpClientFactory(Func<FakeSmtpClient> createClient)
{
    public List<FakeSmtpClient> CreatedClients { get; } = [];

    public IEmailSmtpClient Create(EmailSenderOptions options)
    {
        FakeSmtpClient client = createClient();
        CreatedClients.Add(client);
        return client;
    }
}

internal sealed class FakeSmtpClient : IEmailSmtpClient
{
    public int ConnectCount { get; private set; }

    public int DisconnectCount { get; private set; }

    public int BasicAuthenticationCount { get; private set; }

    public int OAuthAuthenticationCount { get; private set; }

    public int NoOpCount { get; private set; }

    public int SendCount { get; private set; }

    public List<string> OAuthAccessTokens { get; } = [];

    public bool IsConnected { get; private set; }

    public bool IsAuthenticated { get; private set; }

    public Func<string, int, SecureSocketOptions, CancellationToken, Task>? ConnectAsyncHandler { get; set; }

    public Func<string, string, CancellationToken, Task>? AuthenticateBasicAsyncHandler { get; set; }

    public Func<SaslMechanism, CancellationToken, Task>? AuthenticateOAuthAsyncHandler { get; set; }

    public Func<CancellationToken, Task>? NoOpAsyncHandler { get; set; }

    public Func<MimeMessage, CancellationToken, Task>? SendAsyncHandler { get; set; }

    public Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken)
    {
        ConnectCount++;
        IsConnected = true;
        return ConnectAsyncHandler?.Invoke(host, port, options, cancellationToken) ?? Task.CompletedTask;
    }

    public Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
    {
        BasicAuthenticationCount++;
        IsAuthenticated = true;
        return AuthenticateBasicAsyncHandler?.Invoke(username, password, cancellationToken) ?? Task.CompletedTask;
    }

    public Task AuthenticateAsync(SaslMechanism mechanism, CancellationToken cancellationToken)
    {
        OAuthAuthenticationCount++;
        if (mechanism is SaslMechanismOAuth2 oauthMechanism)
        {
            NetworkCredential? credentials = oauthMechanism.Credentials.GetCredential(new Uri("smtp://localhost"), oauthMechanism.MechanismName);
            OAuthAccessTokens.Add(credentials?.Password ?? string.Empty);
        }

        IsAuthenticated = true;
        return AuthenticateOAuthAsyncHandler?.Invoke(mechanism, cancellationToken) ?? Task.CompletedTask;
    }

    public Task NoOpAsync(CancellationToken cancellationToken)
    {
        NoOpCount++;
        return NoOpAsyncHandler?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    public Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        SendCount++;
        return SendAsyncHandler?.Invoke(message, cancellationToken) ?? Task.CompletedTask;
    }

    public Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default)
    {
        DisconnectCount++;
        IsConnected = false;
        IsAuthenticated = false;
        return Task.CompletedTask;
    }

    public void SetAuthenticationState(bool isAuthenticated) => IsAuthenticated = isAuthenticated;

    public void Dispose()
    {
    }
}
