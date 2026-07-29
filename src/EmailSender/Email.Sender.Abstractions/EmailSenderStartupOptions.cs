using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Net.Security;

namespace ReconArt.Email
{
    /// <summary>
    /// Startup options used to configure the behavior of the email sender.
    /// </summary>
    public class EmailSenderStartupOptions : IValidatableObject
    {

        /// <summary>
        /// Name of the configuration section for the email sender startup options.
        /// </summary>
        public const string SectionName = "EmailSender:Startup";

        /// <summary>
        /// Maximum number of concurrent SMTP connections to maintain in the pool.
        /// <br/><br/> <i>Default value:</i> 3
        /// </summary>
        /// <remarks>
        /// Determines the maximum amount of simultaneous connections to the mail server that will be maintained 
        /// for processing outgoing messages. This effectively sets the maximum number of threads that will be
        /// used to send messages concurrently, as well as the connection pool's size.
        /// <br/>
        /// Higher values can improve throughput under heavy load
        /// but may consume more resources and may be limited by the mail server leading to errors.
        /// </remarks>
        public int MaxConcurrentConnections { get; set; } = 3;


        /// <summary>
        /// Number of messages that can be stored in the queue before applying back-pressure mechanisms.
        /// Set to -1 for storing an unlimited number of messages.
        /// <br/><br/> <i>Default value:</i> 10,000
        /// </summary>
        /// <remarks>
        /// In the event capacity is reached, calls to <see cref="IEmailSenderService.TryScheduleAsync(IEmailMessage, System.Threading.CancellationToken)"/>
        /// will begin awaiting asynchronously until such capacity is available and only then return.
        /// </remarks>
        public int MessageQueueSize { get; set; } = 10_000;

        /// <summary>
        /// Callback to validate the server certificate.
        /// </summary>
        /// <remarks>
        /// If no value is speicified, the default validation will be used.
        /// </remarks>
        public RemoteCertificateValidationCallback? ServerCertificateValidationCallback { get; set; }

        /// <inheritdoc/>
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (MessageQueueSize < 1 && MessageQueueSize != -1)
            {
                yield return new("Value must be greater than or equal to 1 or be set to unlimited capacity (-1).", [nameof(MessageQueueSize)]);
            }

            if (MaxConcurrentConnections < 1)
            {
                yield return new("Value must be greater than or equal to 1.", [nameof(MaxConcurrentConnections)]);
            }
        }
    }
}
