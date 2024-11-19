using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReconArt.Email;
using System;
using System.Threading.Tasks;

namespace ReconArt.Email
{
    /// <summary>
    /// Wrapper for ASP.NET Identity of <see cref="EmailSenderService"/> that sends emails.
    /// </summary>
    public sealed class IdentityEmailSenderService : EmailSenderService, IEmailSender
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="IdentityEmailSenderService"/> class.
        /// </summary>
        /// <param name="mailOptions">Email sender options.</param>
        /// <param name="logger">Email sender logger.</param>
        public IdentityEmailSenderService(IOptionsMonitor<EmailSenderOptions> mailOptions, ILogger<EmailSenderService> logger) : base(mailOptions, logger)
        {
        }

        /// <summary>
        /// Creates an instance of <see cref="IdentityEmailSenderService"/>.
        /// </summary>
        /// <param name="mailOptions">Email sender options.</param>
        /// <param name="configureLogger">
        /// An optional action to configure the <see cref="ILoggerFactory"/> used by the <see cref="IdentityEmailSenderService"/>.
        /// Leave <see langword="null"/> to effectively disable logging.
        /// </param>
        public IdentityEmailSenderService(EmailSenderOptions mailOptions, Action<ILoggingBuilder>? configureLogger = null) : base(mailOptions, configureLogger)
        {
        }

        /// <summary>
        /// Sends an email asynchronously.
        /// </summary>
        /// <remarks>
        /// If sending fails, an <see cref="InvalidOperationException"/> will be thrown.
        /// </remarks>
        /// <param name="email">Recipient's email address.</param>
        /// <param name="subject">Subject of the email.</param>
        /// <param name="htmlMessage">Body of the email which may contain HTML tags. Do not double encode this.</param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException">Thrown when email could not be sent.</exception>
        public async Task SendEmailAsync(string email, string subject, string htmlMessage)
        {
            bool successfullySent = await TrySendAsync(new EmailMessage(email, subject, htmlMessage));
            if (!successfullySent)
            {
                throw new InvalidOperationException("Email could not be sent.");
            }
        }
    }
}
