using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ReconArt.Email.Sender.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddEmailSenderService_RuntimeMonitor_RegistersMonitorTypeAutomatically()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new TestRuntimeMonitorDependency());

        services.AddEmailSenderService<TestRuntimeEmailSenderOptionsMonitor>();

        await using ServiceProvider serviceProvider = services.BuildServiceProvider();

        IEmailSenderService emailSenderService = serviceProvider.GetRequiredService<IEmailSenderService>();
        TestRuntimeEmailSenderOptionsMonitor monitor = serviceProvider.GetRequiredService<TestRuntimeEmailSenderOptionsMonitor>();
        IOptionsMonitor<EmailSenderOptions> resolvedMonitor = serviceProvider.GetRequiredService<IOptionsMonitor<EmailSenderOptions>>();

        Assert.IsType<EmailSenderService>(emailSenderService);
        Assert.Same(monitor, resolvedMonitor);
        Assert.NotNull(monitor.Dependency);
    }

    [Fact]
    public async Task AddEmailSenderService_RuntimeProvider_RegistersProviderTypeAutomatically()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new TestRuntimeMonitorDependency());

        services.AddEmailSenderService<TestRuntimeEmailSenderOptionsProvider>();

        await using ServiceProvider serviceProvider = services.BuildServiceProvider();

        IEmailSenderService emailSenderService = serviceProvider.GetRequiredService<IEmailSenderService>();
        TestRuntimeEmailSenderOptionsProvider provider = serviceProvider.GetRequiredService<TestRuntimeEmailSenderOptionsProvider>();
        IEmailSenderOptionsProvider resolvedProvider = serviceProvider.GetRequiredService<IEmailSenderOptionsProvider>();

        Assert.IsType<EmailSenderService>(emailSenderService);
        Assert.Same(provider, resolvedProvider);
        Assert.NotNull(provider.Dependency);
    }
}

internal sealed class TestRuntimeMonitorDependency;

internal sealed class TestRuntimeEmailSenderOptionsMonitor(TestRuntimeMonitorDependency dependency) : IOptionsMonitor<EmailSenderOptions>
{
    public TestRuntimeMonitorDependency Dependency { get; } = dependency;

    public EmailSenderOptions CurrentValue { get; } = EmailSenderOptions.CreateBasic(
        host: "smtp.example.com",
        port: 25,
        requiresAuthentication: false,
        fromAddress: "from@example.com");

    public EmailSenderOptions Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<EmailSenderOptions, string?> listener) => null;
}

internal sealed class TestRuntimeEmailSenderOptionsProvider(TestRuntimeMonitorDependency dependency) : IEmailSenderOptionsProvider
{
    public TestRuntimeMonitorDependency Dependency { get; } = dependency;

    public ValueTask<EmailSenderOptions?> GetOptionsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<EmailSenderOptions?>(EmailSenderOptions.CreateBasic(
            host: "smtp.example.com",
            port: 25,
            requiresAuthentication: false,
            fromAddress: "from@example.com"));
}
