# ReconArt.Email.Sender.Identity

This package extends ReconArt.Email.Sender with support for ASP.NET Identity.

## Usage

The registration methods mirror their non-identity counterparts from `ReconArt.Email.Sender` one-for-one, with the exception that they also register `IEmailSender` for ASP.NET Identity, as well as accepting a flag of whether or not the ASP.NET Identity implementation should schedule emails or await them.
```csharp
public void ConfigureServices(IServiceCollection services)
{
    // Register IEmailSender for ASP.NET Identity, as well as IEmailSenderService for more flexible usage.
    // The ASP.NET Identity implementation will schedule emails by default, as specified by `useBlockingIdentityService`.
    // Setting the flag to true will await the emails instead.
    services.AddIdentityEmailSenderService(useBlockingIdentityService: false);
}
```

The ASP.NET Identity implementation will throw an InvalidOperationException if sending/scheduling fails.

OAuth2 authentication and dynamic runtime configuration are inherited from `ReconArt.Email.Sender`; configure them through the same `EmailSenderOptions`, `EmailSenderStartupOptions`, and `configureStartupOptions` delegate exposed by these registration methods.

For dynamic runtime configuration, register an `IEmailSenderOptionsProvider` or an `IOptionsMonitor<EmailSenderOptions>` implementation exactly like `AddEmailSenderService<TOptionsSource>`, with startup options bound from the `EmailSender:Startup` configuration section:

```csharp
services.AddIdentityEmailSenderService<DatabaseEmailSenderOptionsProvider>(configuration);
```

See the `ReconArt.Email.Sender` README for the full dynamic runtime configuration guide, including the provider contract and OAuth2 token persistence rules.