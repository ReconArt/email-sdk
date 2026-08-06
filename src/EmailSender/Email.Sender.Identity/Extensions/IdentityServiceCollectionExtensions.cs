using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;

namespace ReconArt.Email
{
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
    public static class IdentityServiceCollectionExtensions
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
    {
        /// <summary>
        /// Registers an <see cref="IEmailSenderService"/> in the service collection, as well as an <see cref="IEmailSender"/> for ASP.NET Identity.
        /// </summary>
        /// <remarks>
        /// This method allows you to load default options, or options from an <see cref="IConfiguration"/> and optionally override them with a <paramref name="configureOptions"/> delegate.
        /// <br/><br/>
        /// <br/>If <paramref name="configuration"/> is <see langword="null"/>,
        /// the default values of <see cref="EmailSenderOptions"/> will be used and then overridden by <paramref name="configureOptions"/> (if any).
        /// <br/>If <paramref name="configuration"/> is not <see langword="null"/>,
        /// the options will be loaded from the configuration and then overridden by <paramref name="configureOptions"/> (if any).
        /// <br/><br/><see cref="EmailSenderStartupOptions"/> follow the same pattern, using the <c>Startup</c> child section
        /// of <paramref name="sectionName"/> and the <paramref name="configureStartupOptions"/> delegate.
        /// <br/><br/> There is also a simpler method overload,
        /// if you wish to only load options via a delegate - <see cref="AddIdentityEmailSenderService(IServiceCollection, Action{EmailSenderOptions}?, Action{EmailSenderStartupOptions}?, bool)"/>.
        /// </remarks>
        /// <param name="services">Service collection to use.</param>
        /// <param name="configuration">Configuration to read from, if any.</param>
        /// <param name="configureOptions">Optional delegate allowing you to override any settings loaded from the configuration.</param>
        /// <param name="configureStartupOptions">Optional delegate allowing you to override any startup settings loaded from the configuration.</param>
        /// <param name="sectionName">Section name to use for loading the options from.
        /// Defaults to <see cref="EmailSenderOptions.SectionName"/>.</param>
        /// <param name="useBlockingIdentityService">When set to <see langword="false"/>, the identity implementation being used will schedule emails instead of awaiting them.</param>
        /// <returns>A reference to this instance after the operation has completed.</returns>
        public static IServiceCollection AddIdentityEmailSenderService(
            this IServiceCollection services,
            IConfiguration? configuration,
            Action<EmailSenderOptions>? configureOptions = null,
            Action<EmailSenderStartupOptions>? configureStartupOptions = null,
            string? sectionName = null,
            bool useBlockingIdentityService = false)
        {
            RegisterService(services, useBlockingIdentityService);
            return ServiceCollectionExtensions.AddEmailSenderOptions(services, configuration, configureOptions, configureStartupOptions, sectionName);
        }

        /// <summary>
        /// Registers an <see cref="IEmailSenderService"/> in the service collection, as well as an <see cref="IEmailSender"/> for ASP.NET Identity.
        /// </summary>
        /// <param name="services">Service collection to use.</param>
        /// <param name="configureOptions">Delegate to configure options, if any.</param>
        /// <param name="configureStartupOptions">Delegate to configure startup options, if any.</param>
        /// <param name="useBlockingIdentityService">When set to <see langword="false"/>, the identity implementation being used will schedule emails instead of awaiting them.</param>
        /// <returns>A reference to this instance after the operation has completed.</returns>
        public static IServiceCollection AddIdentityEmailSenderService(
            this IServiceCollection services,
            Action<EmailSenderOptions>? configureOptions = null,
            Action<EmailSenderStartupOptions>? configureStartupOptions = null,
            bool useBlockingIdentityService = false)
        {
            RegisterService(services, useBlockingIdentityService);
            return ServiceCollectionExtensions.AddEmailSenderOptions(services, null, configureOptions, configureStartupOptions);
        }

        /// <summary>
        /// Registers an <see cref="IEmailSenderService"/> in the service collection by using a runtime options source,
        /// as well as an <see cref="IEmailSender"/> for ASP.NET Identity.
        /// </summary>
        /// <typeparam name="TOptionsSource">
        /// Options source type that supplies the current email sender options.
        /// Must implement <see cref="IOptionsMonitor{TOptions}"/> for <see cref="EmailSenderOptions"/> or <see cref="IEmailSenderOptionsProvider"/>.
        /// </typeparam>
        /// <param name="services">Service collection to use.</param>
        /// <param name="configureStartupOptions">Delegate to configure startup options, if any.</param>
        /// <param name="useBlockingIdentityService">When set to <see langword="false"/>, the identity implementation being used will schedule emails instead of awaiting them.</param>
        /// <returns>A reference to this instance after the operation has completed.</returns>
        public static IServiceCollection AddIdentityEmailSenderService<TOptionsSource>(
            this IServiceCollection services,
            Action<EmailSenderStartupOptions>? configureStartupOptions = null,
            bool useBlockingIdentityService = false)
            where TOptionsSource : class
        {
            return AddIdentityEmailSenderServiceWithSource<TOptionsSource>(services, null, configureStartupOptions, null, useBlockingIdentityService);
        }

        /// <summary>
        /// Registers an <see cref="IEmailSenderService"/> in the service collection by using a runtime options source,
        /// as well as an <see cref="IEmailSender"/> for ASP.NET Identity,
        /// loading <see cref="EmailSenderStartupOptions"/> from the supplied configuration.
        /// </summary>
        /// <remarks>
        /// Mail options are supplied at runtime by <typeparamref name="TOptionsSource"/> and are never loaded from
        /// <paramref name="configuration"/> - only <see cref="EmailSenderStartupOptions"/> are, using the <c>Startup</c>
        /// child section of <paramref name="sectionName"/> and then overridden by <paramref name="configureStartupOptions"/> (if any).
        /// </remarks>
        /// <typeparam name="TOptionsSource">
        /// Options source type that supplies the current email sender options.
        /// Must implement <see cref="IOptionsMonitor{TOptions}"/> for <see cref="EmailSenderOptions"/> or <see cref="IEmailSenderOptionsProvider"/>.
        /// </typeparam>
        /// <param name="services">Service collection to use.</param>
        /// <param name="configuration">Configuration to read startup options from.</param>
        /// <param name="configureStartupOptions">Optional delegate allowing you to override any startup settings loaded from the configuration.</param>
        /// <param name="sectionName">Section name the <see cref="IEmailSenderService"/> options would be loaded from; startup options use its <c>Startup</c> child section.
        /// Defaults to <see cref="EmailSenderOptions.SectionName"/>, i.e. startup options load from <see cref="EmailSenderStartupOptions.SectionName"/>.</param>
        /// <param name="useBlockingIdentityService">When set to <see langword="false"/>, the identity implementation being used will schedule emails instead of awaiting them.</param>
        /// <returns>A reference to this instance after the operation has completed.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
        public static IServiceCollection AddIdentityEmailSenderService<TOptionsSource>(
            this IServiceCollection services,
            IConfiguration configuration,
            Action<EmailSenderStartupOptions>? configureStartupOptions = null,
            string? sectionName = null,
            bool useBlockingIdentityService = false)
            where TOptionsSource : class
        {
            ArgumentNullException.ThrowIfNull(configuration);

            return AddIdentityEmailSenderServiceWithSource<TOptionsSource>(services, configuration, configureStartupOptions, sectionName, useBlockingIdentityService);
        }

        private static IServiceCollection AddIdentityEmailSenderServiceWithSource<TOptionsSource>(
            IServiceCollection services,
            IConfiguration? configuration,
            Action<EmailSenderStartupOptions>? configureStartupOptions,
            string? sectionName,
            bool useBlockingIdentityService)
            where TOptionsSource : class
        {
            services.AddEmailSenderOptionsSource<TOptionsSource>(configuration, configureStartupOptions, sectionName);

            if (typeof(IEmailSenderOptionsProvider).IsAssignableFrom(typeof(TOptionsSource)))
            {
                // Mirrors RegisterService's registration semantics for the provider mode, which is
                // not resolvable through constructor injection (the options infrastructure always
                // registers a default IOptionsMonitor, making constructor selection ambiguous).
                if (useBlockingIdentityService)
                {
                    services.TryAddSingleton<IEmailSenderService>(static provider => CreateBlockingProviderService(provider));
                    services.AddSingleton<IEmailSender>(static provider => CreateBlockingProviderService(provider));
                }
                else
                {
                    services.TryAddSingleton<IEmailSenderService>(static provider => CreateSchedulingProviderService(provider));
                    services.AddSingleton<IEmailSender>(static provider => CreateSchedulingProviderService(provider));
                }
            }
            else
            {
                RegisterService(services, useBlockingIdentityService);
            }

            return services;
        }

        private static IdentityEmailSenderService CreateBlockingProviderService(IServiceProvider provider) =>
            new(provider.GetRequiredService<IEmailSenderOptionsProvider>(),
                provider.GetRequiredService<IOptions<EmailSenderStartupOptions>>(),
                provider.GetRequiredService<ILogger<EmailSenderService>>());

        private static SchedulingIdentityEmailSenderService CreateSchedulingProviderService(IServiceProvider provider) =>
            new(provider.GetRequiredService<IEmailSenderOptionsProvider>(),
                provider.GetRequiredService<IOptions<EmailSenderStartupOptions>>(),
                provider.GetRequiredService<ILogger<EmailSenderService>>());

        private static void RegisterService(IServiceCollection services, bool useBlockingIdentityService)
        {
            // Not using TryAddSingleton here intentionally for ASP.NET Identity's implementation.
            // That way the consumer does not have to worry whether they call this before, or after ASP.NET Identity's registration.

            if (useBlockingIdentityService)
            {
                services.TryAddSingleton<IEmailSenderService, IdentityEmailSenderService>();
                services.AddSingleton<IEmailSender, IdentityEmailSenderService>();

            }
            else
            {
                services.TryAddSingleton<IEmailSenderService, SchedulingIdentityEmailSenderService>();
                services.AddSingleton<IEmailSender, SchedulingIdentityEmailSenderService>();
            }
        }
    }
}
