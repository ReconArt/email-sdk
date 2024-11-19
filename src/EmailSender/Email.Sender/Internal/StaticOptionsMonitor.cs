using Microsoft.Extensions.Options;
using System;

namespace ReconArt.Email.Sender.Internal
{
    internal class StaticOptionsMonitor<TOptions> : IOptionsMonitor<TOptions> 
        where TOptions: class
    {
        private readonly IOptions<TOptions> _options;

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
