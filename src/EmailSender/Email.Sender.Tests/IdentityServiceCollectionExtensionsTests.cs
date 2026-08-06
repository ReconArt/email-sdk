using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ReconArt.Email.Sender.Tests;

public sealed class IdentityServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddIdentityEmailSenderService_RuntimeProvider_RegistersSchedulingServicesByDefault()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new TestRuntimeMonitorDependency());

        services.AddIdentityEmailSenderService<TestRuntimeEmailSenderOptionsProvider>();

        await using ServiceProvider serviceProvider = services.BuildServiceProvider();

        Assert.IsType<SchedulingIdentityEmailSenderService>(serviceProvider.GetRequiredService<IEmailSenderService>());
        Assert.IsType<SchedulingIdentityEmailSenderService>(serviceProvider.GetRequiredService<IEmailSender>());
        Assert.Same(
            serviceProvider.GetRequiredService<TestRuntimeEmailSenderOptionsProvider>(),
            serviceProvider.GetRequiredService<IEmailSenderOptionsProvider>());
    }

    [Fact]
    public async Task AddIdentityEmailSenderService_RuntimeProvider_Blocking_RegistersBlockingServices()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new TestRuntimeMonitorDependency());

        services.AddIdentityEmailSenderService<TestRuntimeEmailSenderOptionsProvider>(useBlockingIdentityService: true);

        await using ServiceProvider serviceProvider = services.BuildServiceProvider();

        Assert.IsType<IdentityEmailSenderService>(serviceProvider.GetRequiredService<IEmailSenderService>());
        Assert.IsType<IdentityEmailSenderService>(serviceProvider.GetRequiredService<IEmailSender>());
    }

    [Fact]
    public async Task AddIdentityEmailSenderService_RuntimeMonitor_RegistersMonitorTypeAutomatically()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new TestRuntimeMonitorDependency());

        services.AddIdentityEmailSenderService<TestRuntimeEmailSenderOptionsMonitor>();

        await using ServiceProvider serviceProvider = services.BuildServiceProvider();

        Assert.IsType<SchedulingIdentityEmailSenderService>(serviceProvider.GetRequiredService<IEmailSenderService>());
        Assert.IsType<SchedulingIdentityEmailSenderService>(serviceProvider.GetRequiredService<IEmailSender>());
        Assert.Same(
            serviceProvider.GetRequiredService<TestRuntimeEmailSenderOptionsMonitor>(),
            serviceProvider.GetRequiredService<IOptionsMonitor<EmailSenderOptions>>());
    }

    [Fact]
    public async Task AddIdentityEmailSenderService_RuntimeSourceWithConfiguration_BindsStartupOptions()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EmailSender:Startup:MaxConcurrentConnections"] = "9",
                ["EmailSender:Startup:MessageQueueSize"] = "77"
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new TestRuntimeMonitorDependency());

        services.AddIdentityEmailSenderService<TestRuntimeEmailSenderOptionsProvider>(configuration);

        await using ServiceProvider serviceProvider = services.BuildServiceProvider();

        Assert.IsType<SchedulingIdentityEmailSenderService>(serviceProvider.GetRequiredService<IEmailSenderService>());
        EmailSenderStartupOptions startupOptions = serviceProvider.GetRequiredService<IOptions<EmailSenderStartupOptions>>().Value;
        Assert.Equal(9, startupOptions.MaxConcurrentConnections);
        Assert.Equal(77, startupOptions.MessageQueueSize);
    }

    [Fact]
    public void AddIdentityEmailSenderService_RuntimeSourceWithNullConfiguration_ThrowsArgumentNullException()
    {
        ServiceCollection services = new();

        Assert.Throws<ArgumentNullException>(() =>
            services.AddIdentityEmailSenderService<TestRuntimeEmailSenderOptionsProvider>((IConfiguration)null!));
    }
}
