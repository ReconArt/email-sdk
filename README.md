# Email SDK

Collection of packages that enrich e-mail functionality.

## Available Packages

| Package | Description |
|---------|-------------|
| [ReconArt.Email.Sender](https://github.com/ReconArt/email-sdk/tree/main/src/EmailSender/Email.Sender) | Core implementation for sending emails with SMTP connection pooling, retry logic, and comprehensive configuration options. |
| [ReconArt.Email.Sender.Abstractions](https://github.com/ReconArt/email-sdk/tree/main/src/EmailSender/Email.Sender.Abstractions) | Interface definitions and models for the email sender functionality, allowing for loose coupling in your applications. |
| [ReconArt.Email.Sender.Identity](https://github.com/ReconArt/email-sdk/tree/main/src/EmailSender/Email.Sender.Identity) | Extends support for ASP.NET Identity. |

## Getting Started

To use this SDK, install only the package you need for your scenario:

```bash
# For basic email sending functionality:
dotnet add package ReconArt.Email.Sender

# For identity integration:
dotnet add package ReconArt.Email.Sender.Identity
```

Each package has detailed documentation in its respective repository.

## Features

- Efficient SMTP connection pooling
- Comprehensive retry policies with jitter
- Support for email attachments and inline content
- Whitelist filtering
- Flexible address format handling options
- Thread-safe implementation for high-volume scenarios
