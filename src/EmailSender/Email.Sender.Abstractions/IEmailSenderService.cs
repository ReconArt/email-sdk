using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReconArt.Email
{
    /// <summary>
    /// Represents a service that sends emails.
    /// </summary>
    public interface IEmailSenderService
    {
        /// <summary>
        /// Attempts to send an email.
        /// </summary>
        /// <param name="email">Email to send.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns><see cref="ValueTask{TResult}"/> containing <see langword="true"/> if successfully sent, <see langword="false"/> otherwise.</returns>
        /// <exception cref="OperationCanceledException">
        /// Thrown when the <paramref name="cancellationToken"/> is cancelled.
        /// </exception>
        ValueTask<bool> TrySendAsync(IEmailMessage email, CancellationToken cancellationToken = default);

        /// <summary>
        /// Attempts to schedule an email for delivery.
        /// </summary>
        /// <param name="email">Email to schedule.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns><see cref="ValueTask{TResult}"/> containing <see langword="true"/> if successfully scheduled, <see langword="false"/> otherwise.</returns>
        /// <exception cref="OperationCanceledException">
        /// Thrown when the <paramref name="cancellationToken"/> is cancelled.
        /// </exception>
        ValueTask<bool> TryScheduleAsync(IEmailMessage email, CancellationToken cancellationToken = default);

        /// <summary>
        /// Tests the connection to the email server.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> containing the <see cref="Exception"/> thrown during the test, if any.
        /// </returns>
        ValueTask<Exception?> TestConnectionAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns the number of unsuccessfully sent email messages.
        /// </summary>
        /// <returns>
        /// The number of unsuccessfully sent email messages.
        /// </returns>
        int GetFailedMessagesCount();

        /// <summary>
        /// Resets the count of unsuccessfully sent email messages.
        /// </summary>
        void ResetCount();
    }
}
