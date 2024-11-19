using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        /// <br/><br/> There is also a simpler method overload, 
        /// if you wish to only load options via a delegate - <see cref="AddIdentityEmailSenderService(IServiceCollection, Action{EmailSenderOptions}?, bool)"/>.
        /// </remarks>
        /// <param name="services">Service collection to use.</param>
        /// <param name="configuration">Configuration to read from, if any.</param>
        /// <param name="configureOptions">Optional delegate allowing you to override any settings loaded from the configuration.</param>
        /// <param name="sectionName">Section name to use for loading the options from. 
        /// Defaults to <see cref="EmailSenderOptions.SectionName"/>.</param>
        /// <param name="useBlockingIdentityService">When set to <see langword="false"/>, the identity implementation being used will schedule emails instead of awaiting them.</param>
        /// <returns>A reference to this instance after the operation has completed.</returns>
        public static IServiceCollection AddIdentityEmailSenderService(
            this IServiceCollection services,
            IConfiguration? configuration,
            Action<EmailSenderOptions>? configureOptions = null,
            string? sectionName = null,
            bool useBlockingIdentityService = false)
        {
            RegisterService(services, useBlockingIdentityService);
            return ServiceCollectionExtensions.AddEmailSenderOptions(services, configuration, configureOptions, sectionName);
        }

        /// <summary>
        /// Registers an <see cref="IEmailSenderService"/> in the service collection, as well as an <see cref="IEmailSender"/> for ASP.NET Identity.
        /// </summary>
        /// <param name="services">Service collection to use.</param>
        /// <param name="configureOptions">Delegate to configure options, if any.</param>
        /// <param name="useBlockingIdentityService">When set to <see langword="false"/>, the identity implementation being used will schedule emails instead of awaiting them.</param>
        /// <returns>A reference to this instance after the operation has completed.</returns>
        public static IServiceCollection AddIdentityEmailSenderService(
            this IServiceCollection services,
            Action<EmailSenderOptions>? configureOptions = null,
            bool useBlockingIdentityService = false)
        {
            RegisterService(services, useBlockingIdentityService);
            return ServiceCollectionExtensions.AddEmailSenderOptions(services, null, configureOptions);
        }

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
