# ReconArt.Email.Sender

## Overview

`ReconArt.Email.Sender` is a robust .NET library designed for sending emails using the SMTP protocol. It provides a comprehensive suite of features that make it suitable for a wide range of email sending scenarios, from simple email dispatch to basic queuing and health monitoring.

## Features

- **Targets .NET 8 and .NET 9**: Leverages the latest .NET frameworks for optimal performance and compatibility.
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
var emailSenderOptions = new EmailSenderOptions
{
    Host = "smtp.example.com",
    Port = 587,
    RequiresAuthentication = true,
    Username = "your-username",
    Password = "your-password",
    // FromAddress is only necessary in the event Username is not an actual email address,
    // or no authentication is involved.
    FromAddress = "no-reply@example.com" 
};

// Create the email sender service
var emailSenderService = new EmailSenderService(emailSenderOptions, configureLogger: builder =>
{
    builder.AddConsole();
});

// Use the email sender service to send an email
var emailMessage = new EmailMessage("recipient@example.com", "Subject", "Body");

await emailSenderService.TrySendAsync(emailMessage);
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
var emailSenderOptions = new EmailSenderOptions
{
    Host = "smtp.example.com",
    Port = 587,
    RequiresAuthentication = true,
    Username = "your-username",
    Password = "your-password",
    FromAddress = "no-reply@example.com"
};

// Create the email sender service
var emailSenderService = new EmailSenderService(emailSenderOptions, configureLogger: builder =>
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
| RequiresAuthentication          | bool                              | Set to `true` when authentication is required when connecting to the server. Uses `Username` and `Password` for authentication.                                                                                                   | true          |
| Username                        | string?                           | Username to authenticate as for the mail server.                                                                                                                                                                                 | null          |
| FromAddress                     | string?                           | Email address to send emails from. If `null`, `Username` will be used instead.                                                                                                                                                   | null          |
| Password                        | string?                           | Password to authenticate as for the mail server.                                                                                                                                                                                 | null          |
| RetryCount                      | uint                              | Number of times to retry sending an email before giving up.                                                                                                                                                                      | 3             |
| RetryDelayInMilliseconds        | uint                              | Approximate wait time before retrying to send an email. Uses a jitter formula for delay calculation.                                                                                                                             | 2000          |
| MaxConcurrentConnections        | int                               | Maximum number of concurrent SMTP connections to maintain in the pool. Determines the maximum amount of simultaneous connections to the mail server that will be maintained for processing outgoing messages. Higher values can improve throughput under heavy load but may consume more resources and may be limited by the mail server. | 3             |
| MessageQueueSize                | int                               | Number of messages that can be stored in the queue before applying back-pressure mechanisms. Set to -1 for storing an unlimited number of messages. When capacity is reached, calls to `TryScheduleAsync` will begin awaiting asynchronously until capacity is available. | 10,000        |
| ServerCertificateValidationCallback | RemoteCertificateValidationCallback? | Callback to validate the server certificate. If no value is specified, the default validation will be used.                                                                                                               | null          |
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
