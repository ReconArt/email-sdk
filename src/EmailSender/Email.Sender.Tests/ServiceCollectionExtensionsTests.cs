using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ReconArt.Email.Sender.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddEmailSenderService_DynamicProvider_RegistersProviderTypeAutomatically()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new TestDynamicProviderDependency());

        services.AddEmailSenderService<TestDynamicEmailSenderOptionsProvider>();

        using ServiceProvider serviceProvider = services.BuildServiceProvider();

        IEmailSenderService emailSenderService = serviceProvider.GetRequiredService<IEmailSenderService>();
        TestDynamicEmailSenderOptionsProvider provider = serviceProvider.GetRequiredService<TestDynamicEmailSenderOptionsProvider>();

        Assert.IsType<EmailSenderService>(emailSenderService);
        Assert.NotNull(provider.Dependency);
    }
}

internal sealed class TestDynamicProviderDependency;

internal sealed class TestDynamicEmailSenderOptionsProvider(TestDynamicProviderDependency dependency) : IEmailSenderOptionsProvider
{
    public TestDynamicProviderDependency Dependency { get; } = dependency;

    public ValueTask<EmailSenderOptionsSnapshot> GetCurrentAsync(CancellationToken cancellationToken)
    {
        EmailSenderOptions options = EmailSenderOptions.CreateBasic(
            host: "smtp.example.com",
            port: 25,
            requiresAuthentication: false,
            fromAddress: "from@example.com");
        options.MaxConcurrentConnections = 1;

        return ValueTask.FromResult(new EmailSenderOptionsSnapshot
        {
            Options = options,
            ConfigurationRevision = "revision-1"
        });
    }
}
