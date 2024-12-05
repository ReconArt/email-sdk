using Microsoft.Extensions.Options;
using System;

namespace ReconArt.Email.Sender.Internal
{

    internal static class StaticOptionsMonitor
    {
        internal static IOptionsMonitor<TOptions> Create<TOptions>(TOptions options)
            where TOptions : class
        {
            return new StaticOptionsMonitor<TOptions>(options);
        }
    }

    internal class StaticOptionsMonitor<TOptions> : IOptionsMonitor<TOptions> 
        where TOptions: class
    {
        private readonly IOptions<TOptions> _options;

        public StaticOptionsMonitor(TOptions options)
            : this(Options.Create(options))
        {
        }

        public StaticOptionsMonitor(IOptions<TOptions> options)
        {
            _options = options;
        }

        public TOptions CurrentValue => _options.Value;

        public TOptions Get(string? name) => _options.Value;

        public IDisposable? OnChange(Action<TOptions, string> listener)
        {
            // Since we don't expect the options to change, we don't need to implement this
            return null;
        }
    }
}
