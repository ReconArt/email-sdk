using System.Threading;
using System.Threading.Tasks;

namespace ReconArt.Email
{
    /// <summary>
    /// Provides the current email sender options snapshot at runtime.
    /// </summary>
    public interface IEmailSenderOptionsProvider
    {
        /// <summary>
        /// Gets the current email sender options snapshot.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token used to cancel the operation.</param>
        /// <returns>
        /// A snapshot describing the current runtime configuration.
        /// Return a snapshot with <see cref="EmailSenderOptionsSnapshot.Options"/> set to <see langword="null"/>
        /// when the email sender should be treated as unavailable.
        /// </returns>
        ValueTask<EmailSenderOptionsSnapshot> GetCurrentAsync(CancellationToken cancellationToken);
    }
}
