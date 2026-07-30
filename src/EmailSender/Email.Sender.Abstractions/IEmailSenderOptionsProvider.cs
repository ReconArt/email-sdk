using System.Threading;
using System.Threading.Tasks;

namespace ReconArt.Email
{
    /// <summary>
    /// Provides the current email sender options at runtime.
    /// </summary>
    public interface IEmailSenderOptionsProvider
    {
        /// <summary>
        /// Gets the current email sender options.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token used to cancel the operation.</param>
        /// <returns>
        /// The current email sender options, or <see langword="null"/> when the email sender should be treated as unavailable.
        /// </returns>
        ValueTask<EmailSenderOptions?> GetOptionsAsync(CancellationToken cancellationToken);
    }
}
