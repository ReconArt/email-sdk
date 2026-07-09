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
    public async Task TrySendAsync_DynamicProvider_ActivatesBasicConfigurationWhenItBecomesAvailable()
    {
        TestEmailSenderOptionsProvider provider = new(new EmailSenderOptionsSnapshot
        {
            Options = null,
            ConfigurationRevision = "missing-1"
        });

        FakeSmtpClientFactory factory = new(static () => new FakeSmtpClient());
        await using EmailSenderService service = CreateDynamicService(provider, factory);

        bool firstSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        provider.SetSnapshot(new EmailSenderOptionsSnapshot
        {
            Options = CreateBasicOptions(requiresAuthentication: true),
            ConfigurationRevision = "basic-1"
        });

        bool secondSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.False(firstSend);
        Assert.True(secondSend);
        FakeSmtpClient client = Assert.Single(factory.CreatedClients);
        Assert.Equal(1, client.ConnectCount);
        Assert.Equal(1, client.BasicAuthenticationCount);
    }

    [Fact]
    public async Task TrySendAsync_DynamicProvider_BasicToOAuth2_ReinitializesSlotClient()
    {
        TestEmailSenderOptionsProvider provider = new(new EmailSenderOptionsSnapshot
        {
            Options = CreateBasicOptions(requiresAuthentication: true),
            ConfigurationRevision = "basic-1"
        });

        FakeSmtpClientFactory factory = new(static () => new FakeSmtpClient());
        await using EmailSenderService service = CreateDynamicService(provider, factory);

        bool firstSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        provider.SetSnapshot(new EmailSenderOptionsSnapshot
        {
            Options = CreateOAuth2Options(
                accessToken: "oauth-token",
                accessTokenExpiresAtUtc: DateTime.UtcNow.AddMinutes(30),
                refreshAccessTokenAsync: _ => ValueTask.FromResult(new EmailSenderOAuthRefreshResult
                {
                    AccessToken = "oauth-refresh-token",
                    AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(60)
                })),
            ConfigurationRevision = "oauth-1"
        });

        bool secondSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.True(firstSend);
        Assert.True(secondSend);
        Assert.Equal(2, factory.CreatedClients.Count);
        Assert.Equal(1, factory.CreatedClients[0].BasicAuthenticationCount);
        Assert.Equal(0, factory.CreatedClients[0].OAuthAuthenticationCount);
        Assert.Equal(0, factory.CreatedClients[1].BasicAuthenticationCount);
        Assert.Equal(1, factory.CreatedClients[1].OAuthAuthenticationCount);
        Assert.Equal(["oauth-token"], factory.CreatedClients[1].OAuthAccessTokens);
    }

    [Fact]
    public async Task TrySendAsync_DynamicProvider_OAuth2ToBasic_ReinitializesSlotClient()
    {
        TestEmailSenderOptionsProvider provider = new(new EmailSenderOptionsSnapshot
        {
            Options = CreateOAuth2Options(
                accessToken: "oauth-token",
                accessTokenExpiresAtUtc: DateTime.UtcNow.AddMinutes(30),
                refreshAccessTokenAsync: _ => ValueTask.FromResult(new EmailSenderOAuthRefreshResult
                {
                    AccessToken = "oauth-refresh-token",
                    AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(60)
                })),
            ConfigurationRevision = "oauth-1"
        });

        FakeSmtpClientFactory factory = new(static () => new FakeSmtpClient());
        await using EmailSenderService service = CreateDynamicService(provider, factory);

        bool firstSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        provider.SetSnapshot(new EmailSenderOptionsSnapshot
        {
            Options = CreateBasicOptions(requiresAuthentication: true),
            ConfigurationRevision = "basic-1"
        });

        bool secondSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.True(firstSend);
        Assert.True(secondSend);
        Assert.Equal(2, factory.CreatedClients.Count);
        Assert.Equal(1, factory.CreatedClients[0].OAuthAuthenticationCount);
        Assert.Equal(0, factory.CreatedClients[1].OAuthAuthenticationCount);
        Assert.Equal(1, factory.CreatedClients[1].BasicAuthenticationCount);
    }

    [Fact]
    public async Task TrySendAsync_DynamicProvider_StructuralReload_RebuildsSlotUsingCurrentRevision()
    {
        TestEmailSenderOptionsProvider provider = new(new EmailSenderOptionsSnapshot
        {
            Options = CreateBasicOptions(requiresAuthentication: true),
            ConfigurationRevision = "basic-1"
        });

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

        await using EmailSenderService service = CreateDynamicService(provider, factory);

        bool firstSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));
        Task<bool> secondSendTask = service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body")).AsTask();

        await noOpStarted.Task;

        provider.SetSnapshot(new EmailSenderOptionsSnapshot
        {
            Options = CreateOAuth2Options(
                accessToken: "oauth-token",
                accessTokenExpiresAtUtc: DateTime.UtcNow.AddMinutes(30),
                refreshAccessTokenAsync: _ => ValueTask.FromResult(new EmailSenderOAuthRefreshResult
                {
                    AccessToken = "oauth-refresh-token",
                    AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(60)
                })),
            ConfigurationRevision = "oauth-1"
        });

        Exception? testConnectionException = await service.TestConnectionAsync();
        releaseNoOp.SetResult();
        bool secondSend = await secondSendTask;

        Assert.True(firstSend);
        Assert.Null(testConnectionException);
        Assert.True(secondSend);
        Assert.Equal(3, factory.CreatedClients.Count);
        Assert.Equal(1, factory.CreatedClients[0].BasicAuthenticationCount);
        Assert.Equal(0, factory.CreatedClients[0].OAuthAuthenticationCount);
        Assert.Equal(1, factory.CreatedClients[1].OAuthAuthenticationCount);
        Assert.Equal(0, factory.CreatedClients[2].BasicAuthenticationCount);
        Assert.Equal(1, factory.CreatedClients[2].OAuthAuthenticationCount);
        Assert.Equal(["oauth-token"], factory.CreatedClients[2].OAuthAccessTokens);
    }

    [Fact]
    public async Task TrySendAsync_DynamicProvider_RemovingConfiguration_DeactivatesSenderAndDisconnectsIdleClient()
    {
        TestEmailSenderOptionsProvider provider = new(new EmailSenderOptionsSnapshot
        {
            Options = CreateBasicOptions(requiresAuthentication: false),
            ConfigurationRevision = "basic-1"
        });

        FakeSmtpClientFactory factory = new(static () => new FakeSmtpClient());
        await using EmailSenderService service = CreateDynamicService(provider, factory);

        bool firstSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));
        FakeSmtpClient client = Assert.Single(factory.CreatedClients);

        provider.SetSnapshot(new EmailSenderOptionsSnapshot
        {
            Options = null,
            ConfigurationRevision = "missing-2"
        });

        bool secondSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.True(firstSend);
        Assert.False(secondSend);
        Assert.Equal(1, client.DisconnectCount);
    }

    [Fact]
    public async Task TestConnectionAsync_DynamicProvider_WithoutConfiguration_ReturnsNotConfigured()
    {
        TestEmailSenderOptionsProvider provider = new(new EmailSenderOptionsSnapshot
        {
            Options = null,
            ConfigurationRevision = "missing-1"
        });

        FakeSmtpClientFactory factory = new(static () => new FakeSmtpClient());
        await using EmailSenderService service = CreateDynamicService(provider, factory);

        Exception? exception = await service.TestConnectionAsync();

        InvalidOperationException invalidOperationException = Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("Email sender is not configured.", invalidOperationException.Message);
        Assert.Empty(factory.CreatedClients);
    }

    [Fact]
    public async Task TrySendAsync_DynamicProvider_OAuthRefreshKeepsRuntimeTokensWhenRevisionDoesNotChange()
    {
        int refreshCalls = 0;
        TestEmailSenderOptionsProvider provider = new(new EmailSenderOptionsSnapshot
        {
            Options = CreateOAuth2Options(
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
                }),
            ConfigurationRevision = "oauth-1"
        })
        {
            GetCurrentAsyncHandler = _ => ValueTask.FromResult(new EmailSenderOptionsSnapshot
            {
                Options = CreateOAuth2Options(
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
                    }),
                ConfigurationRevision = "oauth-1"
            })
        };

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
        await using EmailSenderService service = CreateDynamicService(provider, factory);

        bool firstSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));
        smtpClient.SetAuthenticationState(false);
        bool secondSend = await service.TrySendAsync(new EmailMessage("target@example.com", "Subject", "Body"));

        Assert.True(firstSend);
        Assert.True(secondSend);
        Assert.Equal(1, refreshCalls);
        Assert.Equal(["initial-token", "replacement-token", "replacement-token"], smtpClient.OAuthAccessTokens);
        Assert.Single(factory.CreatedClients);
    }

    private static EmailSenderService CreateService(EmailSenderOptions options, FakeSmtpClientFactory factory) =>
        new(new TestOptionsMonitor<EmailSenderOptions>(options), NullLogger<EmailSenderService>.Instance, factory.Create);

    private static EmailSenderService CreateDynamicService(TestEmailSenderOptionsProvider provider, FakeSmtpClientFactory factory) =>
        new(provider, NullLogger<EmailSenderService>.Instance, factory.Create);

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
    public TOptions CurrentValue => currentValue;

    public TOptions Get(string? name) => currentValue;

    public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
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

internal sealed class TestEmailSenderOptionsProvider(EmailSenderOptionsSnapshot snapshot) : IEmailSenderOptionsProvider
{
    private EmailSenderOptionsSnapshot _snapshot = snapshot;

    public Func<CancellationToken, ValueTask<EmailSenderOptionsSnapshot>>? GetCurrentAsyncHandler { get; set; }

    public ValueTask<EmailSenderOptionsSnapshot> GetCurrentAsync(CancellationToken cancellationToken) =>
        GetCurrentAsyncHandler?.Invoke(cancellationToken) ?? ValueTask.FromResult(_snapshot);

    public void SetSnapshot(EmailSenderOptionsSnapshot snapshot) => _snapshot = snapshot;
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
