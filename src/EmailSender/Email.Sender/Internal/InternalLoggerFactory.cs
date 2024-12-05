using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;

namespace ReconArt.Email.Sender.Internal
{
    internal static class InternalLoggerFactory
    {
        internal static ILogger<T> CreateLogger<T>(Action<ILoggingBuilder>? configureLogger) =>
            LoggerFactory.Create(configureLogger ?? (static builder =>
            {
                // If the user did not provide a delegate, we won't add any providers, effectively disabling logging.
            })).CreateLogger<T>();
    }
}
