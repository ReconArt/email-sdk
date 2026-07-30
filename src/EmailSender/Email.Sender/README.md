# ReconArt.Email.Sender

## Overview

`ReconArt.Email.Sender` is a robust .NET library designed for sending emails using the SMTP protocol. It provides a comprehensive suite of features that make it suitable for a wide range of email sending scenarios, from simple email dispatch to basic queuing and health monitoring.

## Features

- **Targets .NET 8, .NET 9, and .NET 10**: Leverages the latest .NET frameworks for optimal performance and compatibility.
- **Thread-safe Design**: Utilizes a connection pool, ensuring thread safety and efficient resource management by prioritizing "hot" connections.
- **Email Sending and Queuing**: Capable of sending emails immediately or queuing them for asynchronous dispatch.
- **Health Monitoring**: Includes a separate service for monitoring the health and liveness of the email sender, ensuring reliability.
- **Customizable Options**: Offers a comprehensive suite of configuration options to tailor the email sending process to your specific needs.

## Installation

To install the `ReconArt.Email.Sender` package, use the NuGet Package Manager or the Package Manager Console with the following command:

```powershell
Install-Package ReconArt.Email.Sender
```

## Usage

### Standalone Usage

To use the `EmailSenderService` in a standalone application, you can directly instantiate it with the necessary options and logger configuration. Here's how you can set it up:

```csharp
using Microsoft.Extensions.Logging;
using ReconArt.Email;

// Configure email sender options
var emailSenderOptions = EmailSenderOptions.CreateBasic(
    host: "smtp.example.com",
    port: 587,
    requiresAuthentication: true,
    username: "your-username",
    password: "your-password",
    // FromAddress is only necessary in the event Username is not an actual email address,
    // or no authentication is involved.
    fromAddress: "no-reply@example.com");

// Create the email sender service
var emailSenderService = new EmailSenderService(emailSenderOptions, new EmailSenderStartupOptions(), configureLogger: builder =>
{
    builder.AddConsole();
});

// Use the email sender service to send an email
var emailMessage = new EmailMessage("recipient@example.com", "Subject", "Body");

await emailSenderService.TrySendAsync(emailMessage);
```

### Standalone Usage with OAuth2

To use OAuth2, create the options in code and provide a callback that returns refreshed token values.

```csharp
using Microsoft.Extensions.Logging;
using ReconArt.Email;

var emailSenderOptions = EmailSenderOptions.CreateOAuth2(
    host: "smtp.example.com",
    port: 587,
    username: "mailer@example.com",
    accessToken: initialToken.AccessToken,
    accessTokenExpiresAtUtc: initialToken.ExpiresAtUtc,
    refreshAccessTokenAsync: async cancellationToken =>
    {
        var refreshedToken = await myTokenProvider.RefreshAsync(cancellationToken);

        return new EmailSenderOAuthRefreshResult
        {
            AccessToken = refreshedToken.AccessToken,
            RefreshToken = refreshedToken.RefreshToken,
            AccessTokenExpiresAtUtc = refreshedToken.ExpiresAtUtc
        };
    },
    onOAuth2CredentialsRefreshed: async (refreshedToken, cancellationToken) =>
    {
        await myTokenStore.SaveAsync(refreshedToken, cancellationToken);
    });

var emailSenderService = new EmailSenderService(emailSenderOptions, new EmailSenderStartupOptions(), configureLogger: builder =>
{
    builder.AddConsole();
});

await emailSenderService.TrySendAsync(new EmailMessage("recipient@example.com", "Subject", "Body"));
```

### Integration with ASP.NET Core

To integrate the `EmailSenderService` with an ASP.NET Core application, you can use the provided extension methods to register it within the dependency injection container. Here's how you can set it up:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReconArt.Email;

public class Startup
{

	public Startup(IConfiguration configuration)
	{
		Configuration = configuration;
	}

	public IConfiguration Configuration { get; }
		
    public void ConfigureServices(IServiceCollection services)
    {
        // Register the email sender service using the extension method
        services.AddEmailSenderService(configuration);

        // Other service registrations...
    }
}
```

In this setup, the `AddEmailSenderService` extension method is used to register the `EmailSenderService` with the ASP.NET Core dependency injection system. This method allows you to optionally load options from a configuration source, such as appsettings.json, and optionally override them with a delegate if needed.

The method can be called without providing any arguments. In such case, an instance of `EmailSenderOptions` with the default values will be used.

Startup options (`EmailSenderStartupOptions` - pool size, queue size, and the certificate-validation callback) bind from the `EmailSender:Startup` configuration section (or `<sectionName>:Startup` when a custom section name is supplied) and can be overridden with the optional `configureStartupOptions` delegate on `AddEmailSenderService`.

For OAuth2 scenarios, prefer creating `EmailSenderOptions` in code via `CreateOAuth2(...)`, because access tokens and refresh callbacks are typically not a good fit for appsettings-driven configuration.

### Dynamic Runtime Configuration

When SMTP settings are supplied at runtime from a database, cache, secret store, or another external source, register either an `IEmailSenderOptionsProvider` or an `IOptionsMonitor<EmailSenderOptions>`.

Use `IEmailSenderOptionsProvider` when fetching options requires custom business logic:

```csharp
using Microsoft.Extensions.DependencyInjection;
using ReconArt.Email;

services.AddEmailSenderService<DatabaseEmailSenderOptionsProvider>();
```

The provider returns `null` while the sender should be treated as unavailable. Sends will fail gracefully until the provider returns a valid `EmailSenderOptions.CreateBasic(...)` or `EmailSenderOptions.CreateOAuth2(...)` instance.

```csharp
public sealed class DatabaseEmailSenderOptionsProvider : IEmailSenderOptionsProvider
{
    public async ValueTask<EmailSenderOptions?> GetOptionsAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetCurrentAsync(cancellationToken);
        if (settings is null)
        {
            return null;
        }

        return EmailSenderOptions.CreateOAuth2(
            host: settings.Host,
            port: settings.Port,
            username: settings.Username,
            accessToken: settings.AccessToken,
            accessTokenExpiresAtUtc: settings.AccessTokenExpiresAtUtc,
            refreshAccessTokenAsync: async token =>
            {
                var refreshed = await tokenProvider.RefreshAsync(settings.RefreshToken, token);
                return new EmailSenderOAuthRefreshResult
                {
                    AccessToken = refreshed.AccessToken,
                    RefreshToken = refreshed.RefreshToken,
                    AccessTokenExpiresAtUtc = refreshed.ExpiresAtUtc
                };
            },
            onOAuth2CredentialsRefreshed: async (refreshed, token) =>
            {
                await settingsStore.SaveTokensAsync(refreshed.AccessToken, refreshed.RefreshToken, refreshed.AccessTokenExpiresAtUtc, token);
            });
    }
}
```

Use `IOptionsMonitor<EmailSenderOptions>` when your application already maintains the current options in memory:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReconArt.Email;

services.AddEmailSenderService<DatabaseEmailSenderOptionsMonitor>();
```

The monitor should return a non-null `CurrentValue`. If credentials do not exist yet, return a placeholder `EmailSenderOptions` instance with incomplete transport settings. Sends will fail gracefully until the monitor publishes a valid `EmailSenderOptions.CreateBasic(...)` or `EmailSenderOptions.CreateOAuth2(...)` instance.

`CurrentValue` should be fast and should not synchronously query the database on every send. Load from the database, Redis, or secret store into an in-memory value, replace the whole `EmailSenderOptions` instance when structural SMTP settings change, and notify registered `OnChange` listeners.

For OAuth2, the sender updates `AccessToken`, `AccessTokenExpiresAtUtc`, and `RefreshToken` when a refreshed refresh token is returned. Use `OnOAuth2CredentialsRefreshed` when the application needs to persist or observe those refreshed credentials. Exceptions thrown by user-provided delegates are logged and do not fail the send operation.

```csharp
public sealed class DatabaseEmailSenderOptionsMonitor : IOptionsMonitor<EmailSenderOptions>
{
    private readonly object _lock = new();
    private readonly List<Action<EmailSenderOptions, string?>> _listeners = [];
    // Placeholder until real credentials are available; sends fail gracefully until then.
    private EmailSenderOptions _current = new();

    public EmailSenderOptions CurrentValue
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public EmailSenderOptions Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<EmailSenderOptions, string?> listener)
    {
        lock (_lock)
        {
            _listeners.Add(listener);
        }

        return new Subscription(() =>
        {
            lock (_lock)
            {
                _listeners.Remove(listener);
            }
        });
    }

    public void Publish(EmailSenderOptions options)
    {
        Action<EmailSenderOptions, string?>[] listeners;
        lock (_lock)
        {
            _current = options;
            listeners = [.. _listeners];
        }

        foreach (var listener in listeners)
        {
            listener(options, Options.DefaultName);
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
```

### Health Monitoring

The `EmailSenderLivenessService` is designed to monitor the health of the email sending process by periodically checking the connection to the SMTP server. It implements Microsoft's `BackgroundService`, allowing it to run in the background and perform health checks.

#### Standalone Usage

In a standalone application, you need to start the `EmailSenderLivenessService` and periodically check the healthiness report using `GetSnapshotAsync`. Here's how you can set it up:

```csharp
using Microsoft.Extensions.Logging;
using ReconArt.Email;
using System;
using System.Threading;
using System.Threading.Tasks;

// Configure email sender options
var emailSenderOptions = EmailSenderOptions.CreateBasic(
    host: "smtp.example.com",
    port: 587,
    requiresAuthentication: true,
    username: "your-username",
    password: "your-password",
    fromAddress: "no-reply@example.com");

// Create the email sender service
var emailSenderService = new EmailSenderService(emailSenderOptions, new EmailSenderStartupOptions(), configureLogger: builder =>
{
    builder.AddConsole();
});

// Configure email sender liveness options
var livenessOptions = new EmailSenderLivenessOptions
{
    LivenessReportResetsMessageCount = true
};

// Create the email sender liveness service
var emailSenderLivenessService = new EmailSenderLivenessService(emailSenderService, livenessOptions, configureLogger: builder =>
{
    builder.AddConsole();
});

// Start the liveness service
await emailSenderLivenessService.StartAsync(CancellationToken.None);

// Periodically check the healthiness report by receiving a snapshot of the last
// health monitoring check
while (true)
{
    var livenessSnapshot = await emailSenderLivenessService.GetSnapshotAsync();
    Console.WriteLine($"Service is alive: {livenessSnapshot.Success}");
    await Task.Delay(TimeSpan.FromMinutes(2)); // Check every 2 minutes
}
```

Internally, the `EmailSenderLivenessService` tests the connection of the provided `IEmailSenderService` by invoking its `TestConnectionAsync(CancellationToken cancellationToken)` method. When you call `GetSnapshotAsync()`, you receive a snapshot of the most recent health check operation.

To determine if a health check has never been performed, examine the properties of the `EmailSenderLivenessSnapshot`, particularly `Success` and `TimeInSecondsToNextLivenessCheck`. If the snapshot is outdated and due for a refresh, `TimeInSecondsToNextLivenessCheck` will report `0`. Once the background operation completes, a new snapshot with updated properties will be available.

If the connection to the SMTP server fails, the background service will retry the operation after 2 minutes. If successful, it will perform the next check in 10 minutes.

### Configuration

Below are the configuration options available for `EmailSenderService` and `EmailSenderLivenessService`.

For more detailed insights into what each option does, refer to their XML documentation.

#### EmailSenderService Configuration Options

| Option                          | Type                              | Description                                                                                                                                                                                                                      | Default Value |
|---------------------------------|-----------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|---------------|
| Host                            | string                            | Host of the mail server.                                                                                                                                                                                                         | (Required)    |
| Port                            | int                               | Port of the mail server.                                                                                                                                                                                                         | (Required)    |
| AuthenticationType             | EmailSenderAuthenticationType      | Selects the SMTP authentication flow. Use `Basic` for traditional SMTP and `OAuth2` for token-based auth.                                                                                                                       | Basic         |
| RequiresAuthentication         | bool                               | Applies only to `AuthenticationType = Basic`. When `true`, uses `Username` and `Password` for SMTP basic auth; when `false`, only connects.                                                                                     | true          |
| Username                       | string?                            | Username to authenticate as for the mail server. Required for basic-authenticated SMTP and OAuth2.                                                                                                                               | null          |
| FromAddress                    | string?                            | Email address to send emails from. If `null`, `Username` will be used when it is a valid email address.                                                                                                                         | null          |
| Password                       | string?                            | Password to authenticate as for the mail server. Used only for `AuthenticationType = Basic` when `RequiresAuthentication = true`.                                                                                                | null          |
| AccessToken                    | string?                            | Optional initial OAuth2 access token. When omitted, one is obtained via `RefreshAccessTokenAsync` before the first send.                                                                                                         | null          |
| RefreshToken                   | string?                            | OAuth2 refresh token used by upstream refresh callbacks, when applicable. Not used directly for SMTP authentication.                                                                                                             | null          |
| AccessTokenExpiresAtUtc        | DateTime                           | Optional UTC expiration of the OAuth2 access token. The default means the expiry is unknown - the token is used until the server rejects it; supplying it enables proactive refresh.                                             | 0001-01-01    |
| RefreshAccessTokenAsync        | Func<CancellationToken, ValueTask<EmailSenderOAuthRefreshResult>>? | Callback that returns refreshed OAuth2 token values. Required when `AuthenticationType = OAuth2`.                                                                                                | null          |
| OnOAuth2CredentialsRefreshed   | Func<EmailSenderOAuthRefreshResult, CancellationToken, ValueTask>? | Optional callback invoked after refreshed OAuth2 credentials are applied to the current options instance. Exceptions are logged and ignored.                                      | null          |
| RetryCount                      | int                               | Number of times to retry sending an email before giving up.                                                                                                                                                                      | 3             |
| RetryDelayInMilliseconds        | int                               | Approximate wait time before retrying to send an email. Uses a jitter formula for delay calculation.                                                                                                                             | 2000          |
| TreatEmptyRecipientsAsSuccess   | bool                              | Set to `true` to treat emails with no recipients as successfully sent.                                                                                                                                                           | false         |
| EnableTempMailRouting           | bool                              | Allows using `some_email+N@somedomain.com` for routing to `some_email@somedomain.com`. Useful for testing.                                                                                                                       | false         |
| Whitelist                       | string[]                          | Collection of email addresses allowed to receive emails. If empty, no filtering is applied.                                                                                                                                      | []            |
| AllowUnquotedCommasInAddresses  | bool                              | Set to `true` to allow unquoted commas in email addresses.                                                                                                                                                                       | true          |
| AllowAddressesWithoutDomain     | bool                              | Set to `true` to allow parsing addresses without a domain.                                                                                                                                                                       | true          |
| UseStrictAddressParser          | bool                              | Set to `true` to use a stricter RFC-822 address parser.                                                                                                                                                                          | false         |
| SignalFailureOnInvalidParameters| bool                              | Set to `true` to signal a failure when invalid parameters are detected.                                                                                                                                                          | false         |
| VerifyInlineAttachments         | bool                              | Set to `true` to verify inline attachments exist in the email body.                                                                                                                                                              | true          |
| OnEmailSendingFailure           | Func<IEmailMessage, EmailFailureReason, ValueTask>? | Called when there's a failure sending an email to the SMTP server.                                                                                                                                             | null          |

`OnEmailSendingFailure` will not be invoked if cancellation is requested. Additionally, unless `SignalFailureOnInvalidParameters` is set to `true`, it will not be called for failures during the construction of the MIME message. These failures can be inspected through the return values of `IEmailSenderService.TrySendAsync` and `IEmailSenderService.TryScheduleAsync`.

#### EmailSenderStartupOptions Configuration Options

These options are fixed at construction. In ASP.NET Core they bind from the `EmailSender:Startup` configuration section (or `<sectionName>:Startup` when a custom section name is used) and can be overridden with the `configureStartupOptions` delegate on `AddEmailSenderService`.

| Option                              | Type                                 | Description                                                                                                                                                                                                                       | Default Value |
|-------------------------------------|--------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|---------------|
| MaxConcurrentConnections            | int                                  | Maximum number of concurrent SMTP connections maintained in the pool, and the maximum number of messages processed in parallel. Higher values improve throughput under load but consume more resources and may be limited by the mail server. | 3             |
| MessageQueueSize                    | int                                  | Number of messages that can be queued before back-pressure applies. Set to -1 for unlimited. When capacity is reached, calls to `TryScheduleAsync` await asynchronously until capacity is available.                             | 10,000        |
| ServerCertificateValidationCallback | RemoteCertificateValidationCallback? | Callback to validate the server certificate. If no value is specified, the default validation will be used.                                                                                                                      | null          |

#### EmailSenderLivenessService Configuration Options

| Option                               | Type    | Description                                                                                                           | Default Value |
|--------------------------------------|---------|-----------------------------------------------------------------------------------------------------------------------|---------------|
| LivenessReportResetsMessageCount     | bool    | Set to `true` to reset the count of unsuccessfully sent email messages when a liveness check is performed.             | true          |

### ASP.NET Identity Support

There's a separate package `ReconArt.Email.Sender.Identity` which allows integrating the `IEmailSenderService` with ASP.NET Identity's infrastructure.

You can read more about it [here](https://github.com/ReconArt/email-sdk/tree/main/src/EmailSender/Email.Sender.Identity).


## Contributing

If you'd like to contribute to the project, please reach out to the [ReconArt/email-sdk](https://github.com/orgs/ReconArt/teams/email-sdk) team.

## Support

If you encounter any issues or require assistance, please file an issue in the [GitHub Issues](https://github.com/ReconArt/email-sdk/issues) section of the repository.

## Authors and Acknowledgments

Developed by [ReconArt, Inc.](https://reconart.com/). 

Special thanks to the contributors of the [MailKit](https://github.com/jstedfast/MailKit) and [MimeKit](https://github.com/jstedfast/MimeKit) libraries for providing the underlying implementations for communicating and interacting with an SMTP server.
