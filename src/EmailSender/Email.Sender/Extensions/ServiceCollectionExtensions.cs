using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;

namespace ReconArt.Email
{
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
    public static partial class ServiceCollectionExtensions
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
    {
        /// <summary>
        /// Registers an <see cref="IEmailSenderService"/> in the service collection.
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
        /// if you wish to only load options via a delegate - <see cref="AddEmailSenderService(IServiceCollection, Action{EmailSenderOptions}?, Action{EmailSenderStartupOptions}?)"/>.
        /// </remarks>
        /// <param name="services">Service collection to use.</param>
        /// <param name="configuration">Configuration to read from, if any.</param>
        /// <param name="configureOptions">Optional delegate allowing you to override any settings loaded from the configuration.</param>
        /// <param name="configureStartupOptions">Optional delegate allowing you to override any startup settings loaded from the configuration.</param>
        /// <param name="sectionName">Section name to use for loading the <see cref="IEmailSenderService"/> options from.
        /// Defaults to <see cref="EmailSenderOptions.SectionName"/>.</param>
        ///
        /// <returns>A reference to this instance after the operation has completed.</returns>
        public static IServiceCollection AddEmailSenderService(
            this IServiceCollection services,
            IConfiguration? configuration,
            Action<EmailSenderOptions>? configureOptions = null,
            Action<EmailSenderStartupOptions>? configureStartupOptions = null,
            string? sectionName = null)
        {
            services.TryAddSingleton<IEmailSenderService, EmailSenderService>();
            return AddEmailSenderOptions(services, configuration, configureOptions, configureStartupOptions, sectionName);
        }

        /// <summary>
        /// Registers an <see cref="IEmailSenderService"/> in the service collection.
        /// </summary>
        /// <param name="services">Service collection to use.</param>
        /// <param name="configureOptions">Delegate to configure options, if any.</param>
        /// <param name="configureStartupOptions">Delegate to configure startup options, if any.</param>
        /// <returns>A reference to this instance after the operation has completed.</returns>
        public static IServiceCollection AddEmailSenderService(
            this IServiceCollection services,
            Action<EmailSenderOptions>? configureOptions = null,
            Action<EmailSenderStartupOptions>? configureStartupOptions = null)
        {
            services.TryAddSingleton<IEmailSenderService, EmailSenderService>();
            return AddEmailSenderOptions(services, null, configureOptions, configureStartupOptions);
        }

        /// <summary>
        /// Registers an <see cref="IEmailSenderService"/> in the service collection by using a runtime options source.
        /// </summary>
        /// <typeparam name="TOptionsSource">
        /// Options source type that supplies the current email sender options.
        /// Must implement <see cref="IOptionsMonitor{TOptions}"/> for <see cref="EmailSenderOptions"/> or <see cref="IEmailSenderOptionsProvider"/>.
        /// </typeparam>
        /// <param name="services">Service collection to use.</param>
        /// <param name="configureStartupOptions">Delegate to configure startup options, if any.</param>
        /// <returns>A reference to this instance after the operation has completed.</returns>
        public static IServiceCollection AddEmailSenderService<TOptionsSource>(
            this IServiceCollection services,
            Action<EmailSenderStartupOptions>? configureStartupOptions = null)
            where TOptionsSource : class
        {
            Type optionsSourceType = typeof(TOptionsSource);
            bool isOptionsMonitor = typeof(IOptionsMonitor<EmailSenderOptions>).IsAssignableFrom(optionsSourceType);
            bool isOptionsProvider = typeof(IEmailSenderOptionsProvider).IsAssignableFrom(optionsSourceType);

            if (!isOptionsMonitor && !isOptionsProvider)
            {
                throw new ArgumentException(
                    $"{optionsSourceType.FullName} must implement {nameof(IOptionsMonitor<TOptionsSource>)} or {nameof(IEmailSenderOptionsProvider)}.",
                    nameof(TOptionsSource));
            }

            services.TryAddSingleton<TOptionsSource>();
            AddEmailSenderStartupOptions(services, null, configureStartupOptions);

            if (isOptionsProvider)
            {
                services.AddSingleton<IEmailSenderOptionsProvider>(static provider =>
                    (IEmailSenderOptionsProvider)provider.GetRequiredService<TOptionsSource>());
                services.TryAddSingleton<IEmailSenderService>(static provider =>
                    new EmailSenderService(
                        provider.GetRequiredService<IEmailSenderOptionsProvider>(),
                        provider.GetRequiredService<IOptions<EmailSenderStartupOptions>>(),
                        provider.GetRequiredService<ILogger<EmailSenderService>>()));
            }
            else
            {
                services.AddSingleton<IOptionsMonitor<EmailSenderOptions>>(static provider =>
                    (IOptionsMonitor<EmailSenderOptions>)provider.GetRequiredService<TOptionsSource>());
                services.TryAddSingleton<IEmailSenderService, EmailSenderService>();
            }

            return services;
        }

        /// <summary>
        /// Adds <see cref="EmailSenderOptions"/> and <see cref="EmailSenderStartupOptions"/> as options in ASP.NET Core.
        /// </summary>
        /// <remarks>
        /// This exists in the event you want to re-use the options classes defined by this library in your own implementation, without registering the service.
        /// </remarks>
        /// <param name="services">Service collection to use.</param>
        /// <param name="configuration">Configuration to read from, if any.</param>
        /// <param name="configureOptions">Optional delegate allowing you to override any settings loaded from the configuration.</param>
        /// <param name="configureStartupOptions">Optional delegate allowing you to override any startup settings loaded from the configuration.</param>
        /// <param name="sectionName">Section name to use for loading the <see cref="IEmailSenderService"/> options from.
        /// Defaults to <see cref="EmailSenderOptions.SectionName"/>. Startup options are loaded from its <c>Startup</c> child section.</param>
        /// <returns>A reference to this instance after the operation has completed.</returns>
        public static IServiceCollection AddEmailSenderOptions(
            this IServiceCollection services,
            IConfiguration? configuration,
            Action<EmailSenderOptions>? configureOptions = null,
            Action<EmailSenderStartupOptions>? configureStartupOptions = null,
            string? sectionName = null)
        {
            var optionsBuilder = services.AddOptions<EmailSenderOptions>().ValidateDataAnnotations().ValidateOnStart();

            if (configuration is not null)
            {
                optionsBuilder.Bind(configuration.GetSection(sectionName ?? EmailSenderOptions.SectionName));
            }

            if (configureOptions is not null)
            {
                optionsBuilder.Configure(configureOptions);
            }

            return AddEmailSenderStartupOptions(services, configuration, configureStartupOptions, sectionName);
        }

        /// <summary>
        /// Adds <see cref="EmailSenderStartupOptions"/> as an option in ASP.NET Core.
        /// </summary>
        /// <remarks>
        /// This exists in the event you want to re-use the options class defined by this library in your own implementation, without registering the service.
        /// </remarks>
        /// <param name="services">Service collection to use.</param>
        /// <param name="configuration">Configuration to read from, if any.</param>
        /// <param name="configureStartupOptions">Optional delegate allowing you to override any startup settings loaded from the configuration.</param>
        /// <param name="sectionName">Section name the <see cref="IEmailSenderService"/> options are loaded from; startup options use its <c>Startup</c> child section.
        /// Defaults to <see cref="EmailSenderOptions.SectionName"/>, i.e. startup options load from <see cref="EmailSenderStartupOptions.SectionName"/>.</param>
        /// <returns>A reference to this instance after the operation has completed.</returns>
        public static IServiceCollection AddEmailSenderStartupOptions(
            this IServiceCollection services,
            IConfiguration? configuration,
            Action<EmailSenderStartupOptions>? configureStartupOptions = null,
            string? sectionName = null)
        {
            var startupOptionsBuilder = services.AddOptions<EmailSenderStartupOptions>().ValidateDataAnnotations().ValidateOnStart();

            if (configuration is not null)
            {
                string startupSectionName = sectionName is null
                    ? EmailSenderStartupOptions.SectionName
                    : sectionName + ":Startup";

                startupOptionsBuilder.Bind(configuration.GetSection(startupSectionName));
            }

            if (configureStartupOptions is not null)
            {
                startupOptionsBuilder.Configure(configureStartupOptions);
            }

            return services;
        }

        /// <summary>
        /// Registers an <see cref="IEmailSenderLivenessService"/> hosted service in the service collection.
        /// </summary>
        /// <remarks>
        /// This method allows you to load default options, or options from an <see cref="IConfiguration"/> and optionally override them with a <paramref name="configureOptions"/> delegate.
        /// <br/><br/>
        /// <br/>If <paramref name="configuration"/> is <see langword="null"/>,
        /// the default values of <see cref="EmailSenderLivenessOptions"/> will be used and then overridden by <paramref name="configureOptions"/> (if any).
        /// <br/>If <paramref name="configuration"/> is not <see langword="null"/>, 
        /// the options will be loaded from the configuration and then overridden by <paramref name="configureOptions"/> (if any).
        /// <br/><br/> There is also a simpler method overload, 
        /// if you wish to only load options via a delegate - <see cref="AddEmailSenderLivenessService(IServiceCollection, Action{EmailSenderLivenessOptions}?)"/>.
        /// </remarks>
        /// <param name="services">Service collection to use.</param>
        /// <param name="configuration">Configuration to read from, if any.</param>
        /// <param name="configureOptions">Optional delegate allowing you to override any settings loaded from the configuration.</param>
        /// <param name="sectionName">Section name to use for loading the <see cref="IEmailSenderService"/> options from. 
        /// Defaults to <see cref="EmailSenderLivenessOptions.SectionName"/>.</param>
        /// 
        /// <returns>A reference to this instance after the operation has completed.</returns>
        public static IServiceCollection AddEmailSenderLivenessService(
            this IServiceCollection services,
            IConfiguration? configuration,
            Action<EmailSenderLivenessOptions>? configureOptions = null,
            string? sectionName = null)
        {
            services.TryAddSingleton<IEmailSenderLivenessService, EmailSenderLivenessService>();
            services.AddHostedService(static provider => (EmailSenderLivenessService)provider.GetRequiredService<IEmailSenderLivenessService>());
            return AddEmailSenderLivenessOptions(services, configuration, configureOptions, sectionName);
        }

        /// <summary>
        /// Registers an <see cref="IEmailSenderLivenessService"/> hosted service in the service collection.
        /// </summary>
        /// <param name="services">Service collection to use.</param>
        /// <param name="configureOptions">Delegate to configure options, if any.</param>
        /// <returns>A reference to this instance after the operation has completed.</returns>
        public static IServiceCollection AddEmailSenderLivenessService(
            this IServiceCollection services,
            Action<EmailSenderLivenessOptions>? configureOptions = null)
        {
            services.TryAddSingleton<IEmailSenderLivenessService, EmailSenderLivenessService>();
            services.AddHostedService(static provider => (EmailSenderLivenessService)provider.GetRequiredService<IEmailSenderLivenessService>());
            return AddEmailSenderLivenessOptions(services, null, configureOptions);
        }

        /// <summary>
        /// Adds <see cref="EmailSenderLivenessOptions"/> as an option in ASP.NET Core.
        /// </summary>
        /// <remarks>
        /// This exists in the event you want to re-use the options class defined by this library in your own implementation, without registering the service.
        /// </remarks>
        /// <param name="services">Service collection to use.</param>
        /// <param name="configuration">Configuration to read from, if any.</param>
        /// <param name="configureOptions">Delegate allowing you to override any settings loaded from the configuration.</param>
        /// <param name="sectionName">Section name to use for loading the <see cref="IEmailSenderLivenessService"/> options from. 
        /// Defaults to <see cref="EmailSenderLivenessOptions.SectionName"/>.</param>
        /// <returns>A reference to this instance after the operation has completed.</returns>
        public static IServiceCollection AddEmailSenderLivenessOptions(
            this IServiceCollection services,
            IConfiguration? configuration,
            Action<EmailSenderLivenessOptions>? configureOptions = null,
            string? sectionName = null)
        {
            var optionsBuilder = services.AddOptions<EmailSenderLivenessOptions>().ValidateDataAnnotations().ValidateOnStart();

            if (configuration is not null)
            {
                optionsBuilder.Bind(configuration.GetSection(sectionName ?? EmailSenderLivenessOptions.SectionName));
            }

            if (configureOptions is not null)
            {
                optionsBuilder.Configure(configureOptions);
            }

            return services;
        }
    }
}
