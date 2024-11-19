# ReconArt.Email.Sender.Identity

This package extends ReconArt.Email.Sender with support for ASP.NET Identity

## Usage

There are 2 new methods to register an email sender service in your `Startup.cs` or `Program.cs`, which are exactly the same as their non-identity counterparts, with the exception that they also register `IEmailSender` for ASP.NET Identity, as well as accepting a flag of whether or not the ASP.NET Identity implementation should schedule emails or await them.
```csharp
public void ConfigureServices(IServiceCollection services)
{
    // Register IEmailSender for ASP.NET Identity, as well as IEmailSenderService for more flexible usage.
    // The ASP.NET Identity implementation will scchedule emails by default, as specified by `useBlockingIdentityService`.
    // Setting the flag to true will await the emails instead.
    services.AddIdentityEmailSenderService(useBlockingIdentityService: false);
}
```

The ASP.NET Identity implementation will thrown an InvalidOperationException if sending/scheduling fails.