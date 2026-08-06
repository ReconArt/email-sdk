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
        /// Tests the connection to the email server with the given options.
        /// </summary>
        /// <remarks>
        /// The supplied options are validated and used for a one-off SMTP connect/authentication
        /// probe on a dedicated connection - the sender's pooled connections are not used, and its
        /// runtime configuration is not refreshed on the probe's behalf.
        /// For OAuth2, missing, expired, or rejected candidate access tokens are refreshed using the
        /// callbacks on the supplied options instance, and refreshed values are applied onto that
        /// instance - persist from it after a successful test, not from the original inputs.
        /// <br/><br/>
        /// Pass a dedicated candidate instance. When the sender is configured with static options
        /// or an options monitor, it reads the options source's current value to detect whether the
        /// supplied instance is the live configuration; if it is, the probe runs through the
        /// runtime configuration path instead, so the live instance is never mutated outside the
        /// sender's normal refresh machinery. A failure while reading the options source skips this
        /// detection and the candidate is probed on its own. Instances served by an
        /// <see cref="IEmailSenderOptionsProvider"/> cannot be detected this way - always pass a
        /// dedicated copy in that configuration.
        /// </remarks>
        /// <param name="options">Options to validate and test.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> containing the <see cref="Exception"/> thrown during the test, if any.
        /// Cancellation of <paramref name="cancellationToken"/> propagates as an exception rather than being returned.
        /// </returns>
        /// <exception cref="System.ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        ValueTask<Exception?> TestConnectionAsync(EmailSenderOptions options, CancellationToken cancellationToken = default);

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
