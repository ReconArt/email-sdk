using System.Threading.Tasks;

namespace ReconArt.Email
{
    /// <summary>
    /// Represents a service for reporting health status checks for an email sender service.
    /// </summary>
    public interface IEmailSenderLivenessService
    {
        /// <summary>
        /// Gets a liveness snapshot of the email sender service.
        /// </summary>
        /// <returns>A <see cref="ValueTask{TResult}"/> containing a snapshot of the liveness of the email sender service.</returns>
        ValueTask<EmailSenderLivenessSnapshot> GetSnapshotAsync();
    }
}
