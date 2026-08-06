using Microsoft.Extensions.Configuration;
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

    [Fact]
    public async Task AddEmailSenderService_RuntimeSourceWithConfiguration_BindsStartupOptions()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EmailSender:Startup:MaxConcurrentConnections"] = "7",
                ["EmailSender:Startup:MessageQueueSize"] = "42"
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new TestRuntimeMonitorDependency());

        services.AddEmailSenderService<TestRuntimeEmailSenderOptionsProvider>(configuration);

        await using ServiceProvider serviceProvider = services.BuildServiceProvider();

        Assert.IsType<EmailSenderService>(serviceProvider.GetRequiredService<IEmailSenderService>());
        EmailSenderStartupOptions startupOptions = serviceProvider.GetRequiredService<IOptions<EmailSenderStartupOptions>>().Value;
        Assert.Equal(7, startupOptions.MaxConcurrentConnections);
        Assert.Equal(42, startupOptions.MessageQueueSize);
    }

    [Fact]
    public async Task AddEmailSenderService_RuntimeSourceWithConfiguration_CustomSectionAndDelegateOverride()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Custom:Startup:MaxConcurrentConnections"] = "5",
                ["Custom:Startup:MessageQueueSize"] = "42"
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new TestRuntimeMonitorDependency());

        // The delegate runs after configuration binding and wins for the values it sets.
        services.AddEmailSenderService<TestRuntimeEmailSenderOptionsMonitor>(
            configuration,
            static startupOptions => startupOptions.MessageQueueSize = 123,
            sectionName: "Custom");

        await using ServiceProvider serviceProvider = services.BuildServiceProvider();

        EmailSenderStartupOptions startupOptions = serviceProvider.GetRequiredService<IOptions<EmailSenderStartupOptions>>().Value;
        Assert.Equal(5, startupOptions.MaxConcurrentConnections);
        Assert.Equal(123, startupOptions.MessageQueueSize);
    }

    [Fact]
    public void AddEmailSenderService_RuntimeSourceWithNullConfiguration_ThrowsArgumentNullException()
    {
        ServiceCollection services = new();

        Assert.Throws<ArgumentNullException>(() =>
            services.AddEmailSenderService<TestRuntimeEmailSenderOptionsProvider>((IConfiguration)null!));
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
