using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Threading.Tasks;

namespace ReconArt.Email
{
    /// <summary>
    /// Wrapper for ASP.NET Identity of <see cref="EmailSenderService"/> that schedules emails.
    /// </summary>
    public sealed class SchedulingIdentityEmailSenderService : EmailSenderService, IEmailSender
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="IdentityEmailSenderService"/> class.
        /// </summary>
        /// <param name="mailOptions">Email sender options.</param>
        /// <param name="logger">Email sender logger.</param>
        public SchedulingIdentityEmailSenderService(IOptionsMonitor<EmailSenderOptions> mailOptions, ILogger<EmailSenderService> logger) : base(mailOptions, logger)
        {
        }

        /// <summary>
        /// Creates an instance of <see cref="SchedulingIdentityEmailSenderService"/>.
        /// </summary>
        /// <param name="mailOptions">Email sender options.</param>
        /// <param name="configureLogger">
        /// An optional action to configure the <see cref="ILoggerFactory"/> used by the <see cref="SchedulingIdentityEmailSenderService"/>.
        /// Leave <see langword="null"/> to effectively disable logging.
        /// </param>
        public SchedulingIdentityEmailSenderService(EmailSenderOptions mailOptions, Action<ILoggingBuilder>? configureLogger = null) : base(mailOptions, configureLogger)
        {
        }

        /// <summary>
        /// Schedules an email asynchronously.
        /// </summary>
        /// <remarks>
        /// If scheduling fails, an <see cref="InvalidOperationException"/> will be thrown.
        /// </remarks>
        /// <param name="email">Recipient's email address.</param>
        /// <param name="subject">Subject of the email.</param>
        /// <param name="htmlMessage">Body of the email which may contain HTML tags. Do not double encode this.</param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException">Thrown when email could not be scheduled.</exception>
        public async Task SendEmailAsync(string email, string subject, string htmlMessage)
        {
            bool successfullyScheduled = await TryScheduleAsync(new EmailMessage(email, subject, htmlMessage));
            if (!successfullyScheduled)
            {
                throw new InvalidOperationException("Email could not be scheduled.");
            }
        }
    }
}
