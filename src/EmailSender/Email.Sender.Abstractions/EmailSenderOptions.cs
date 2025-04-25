using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Net.Security;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ReconArt.Email
{
    /// <summary>
    /// Options used to configure the behavior of the email sender.
    /// </summary>
    public partial class EmailSenderOptions : IValidatableObject
    {
        /// <summary>
        /// Name of the configuration section for the email sender options.
        /// </summary>
        public const string SectionName = "EmailSender";

        /// <summary>
        /// Host of mail server.
        /// </summary>
        [Required]
        public string Host { get; set; } = string.Empty;

        /// <summary>
        /// Port of the mail server.
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// Set to <see langword="true"/> when authentication is required when connecting to the server.
        /// <br/><br/>
        /// <i>Default value:</i> <see langword="true"/>
        /// </summary>
        /// <remarks>
        /// <seealso cref="Username"/> and <seealso cref="Password"/> will be used to perform the authentication.
        /// </remarks>
        public bool RequiresAuthentication { get; set; } = true;

        /// <summary>
        /// Username to authenticate as for the mail server.
        /// </summary>
        public string? Username { get; set; }

        /// <summary>
        /// Email address to send emails from.
        /// </summary>
        public string? FromAddress { get; set; }

        /// <summary>
        /// Password to authenticate as for the mail server.
        /// </summary>
        public string? Password { get; set; }

        /// <summary>
        /// How many times to retry sending an email before giving up.
        /// <br/><br/> <i>Default value:</i> 3
        /// </summary>
        public int RetryCount { get; set; } = 3;

        /// <summary>
        /// How long to approximately wait before retrying to send an email.
        /// <br/><br/> <i>Default value:</i> 2000
        /// </summary>
        /// <remarks>
        /// Under the hood we use a jitter formula to calculate the delay between retries.
        /// <br/>
        /// This will be used as the median delay to target before the first retry, call it f (= f * 2^0). 
        /// <br/>
        /// Choose this value both to approximate the first delay, and to scale the remainder of
        /// the series.
        /// <br/>
        /// Subsequent retries will (over a large sample size) have a median
        /// approximating retries at time f * 2^1, f * 2^2 ... f* 2^t etc for try t.
        /// <br/>
        /// The actual amount of delay-before-retry for try t may be distributed between 0 and
        /// f* (2^(t+1) - 2^(t-1)) for t >= 2; or between 0 and f * 2^(t+1), for t is 0
        /// or 1.
        /// </remarks>
        public int RetryDelayInMilliseconds { get; set; } = 2000;

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
        /// Callback to validate the server certificate.
        /// </summary>
        /// <remarks>
        /// If no value is speicified, the default validation will be used.
        /// </remarks>
        public RemoteCertificateValidationCallback? ServerCertificateValidationCallback { get; set; }

        /// <summary>
        /// Set to <see langword="true"/> to treat emails with no recipients as successfully sent.
        /// <br/><br/> <i>Default value:</i> <see langword="false"/>
        /// </summary>
        public bool TreatEmptyRecipientsAsSuccess { get; set; }

        /// <summary>
        /// Enabling this allows you to use some_email+N@somedomain.com, 
        /// where N is any number you like, which would then get routed down to some_email@somedomain.com.
        /// <br/><br/>
        /// Only useful for testing purposes. Avoid using in production.
        /// <br/><br/>
        /// <i>Default value:</i> <see langword="false"/>
        /// </summary>
        public bool EnableTempMailRouting { get; set; }

        /// <summary>
        /// Collection containing email addresses that are allowed to receive emails.
        /// </summary>
        /// <remarks>
        /// If no elements are specified, no filtering will be applied, and all emails will be sent.
        /// </remarks>
        [Required]
        public string[] Whitelist { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Set to <see langword="true"/> to allow unquoted commas in email addresses.
        /// <br/><br/> <i>Default value:</i> <see langword="true"/>
        /// </summary>
        /// <remarks>
        /// <para>In general, you'll probably want this value to be <see langword="true"/> (the default) as it allows
        /// maximum interoperability with existing (broken) mail clients and other mail software such as
        /// sloppily written perl scripts (aka spambots) that do not properly quote the name when it
        /// contains a comma.</para>
        /// </remarks>
        public bool AllowUnquotedCommasInAddresses { get; set; } = true;

        /// <summary>
        /// Set to <see langword="true"/> to allow parsing addresses without a domain.
        /// <br/><br/> <i>Default value:</i> <see langword="true"/>
        /// </summary>
        /// <remarks>
        /// <para>In general, you'll probably want this value to be <see langword="true"/> (the default) as it allows
        /// maximum interoperability with older email messages that may contain local UNIX addresses.</para>
        /// <para>This option exists in order to allow parsing of mailbox addresses that do not have an
        /// @domain component. These types of addresses are rare and were typically only used when sending
        /// mail to other users on the same UNIX system.</para>
        /// </remarks>
        public bool AllowAddressesWithoutDomain { get; set; } = true;

        /// <summary>
        /// Set to <see langword="true"/> to use a stricter RFC-822 address parser.
        /// <br/><br/> <i>Default value:</i> <see langword="false"/>
        /// </summary>
        /// <remarks>
        /// <para>In general, you'll probably want this value to be <see langword="false"/>
        /// (the default) as it allows maximum interoperability with existing (broken) mail clients
        /// and other mail software such as sloppily written perl scripts (aka spambots).</para>
        /// <note type="tip">Even when set to <see langword="true"/>, the address parser
        /// is fairly liberal in what it accepts. Setting it to <see langword="false"/>
        /// just makes it try harder to deal with garbage input.</note>
        /// </remarks>
        public bool UseStrictAddressParser { get; set; }

        /// <summary>
        /// Set to <see langword="true"/> to signal a failure when invalid parameters are detected
        /// by calling <see cref="OnEmailSendingFailure"/>, as well as counting it as such for <see cref="IEmailSenderService.GetFailedMessagesCount()"/>.
        /// <br/><br/> <i>Default value:</i> <see langword="false"/>
        /// </summary>
        /// <remarks>
        /// By default, failure is not signaled when invalid parameters are detected. Instead, you can inspect the results of 
        /// <see cref="IEmailSenderService.TrySendAsync(IEmailMessage, System.Threading.CancellationToken)"/> or 
        /// <see cref="IEmailSenderService.TrySendAsync(IEmailMessage, System.Threading.CancellationToken)"/>, both of which return <see langword="false"/> 
        /// if the provided <see cref="IEmailMessage"/> is invalid.
        /// </remarks>
        public bool SignalFailureOnInvalidParameters { get; set; }

        /// <summary>
        /// Set to <see langword="true"/> to verify inline attachments exists in the body of the email.
        /// <br/><br/> <i>Default value:</i> <see langword="true"/>
        /// </summary>
        /// <remarks>
        /// <para>Inline attachments are attachments that are embedded in the body of the email.
        /// <see cref="IEmailAttachment.Placeholder"/> is used to reference these attachments and where they should appear in the email body.
        /// If those placeholders are not found in the email body, the email will be considered invalid.</para>
        /// </remarks>
        public bool VerifyInlineAttachments { get; set; } = true;

        /// <summary>
        /// Called when there's a failure sending an email to the SMTP server.
        /// </summary>
        /// <remarks>
        /// <b>Note:</b> We will only call this after initial validation checks. 
        /// Cancellation via a <see cref="System.Threading.CancellationToken"/> is not considered a failure.
        /// <br/><br/>
        /// Sending an email to the SMTP server is the last step in the process. 
        /// Before that, we check if the email is valid, if it has recipients, if they are whitelisted, etc.
        /// </remarks>
        [JsonIgnore]
        public Func<IEmailMessage, EmailFailureReason, ValueTask>? OnEmailSendingFailure { get; set; }

        /// <summary>
        /// Gets a value indicating whether the <see cref="Username"/> is an email address.
        /// </summary>
        public bool IsUsernameEmailAddress => Username is not null && ValidEmailAddressRegex().IsMatch(Username);

        /// <inheritdoc/>
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (RequiresAuthentication)
            {
                if (string.IsNullOrWhiteSpace(Username))
                {
                    yield return new("Username is required when authentication is enabled.", [nameof(Username)]);
                }

                if (!IsUsernameEmailAddress && string.IsNullOrWhiteSpace(FromAddress))
                {
                    yield return new("From header is required when username is not an email address.", [nameof(FromAddress)]);
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(FromAddress))
                {
                    yield return new("From header is required when authentication is disabled.", [nameof(FromAddress)]);
                }
            }

            if (FromAddress is not null && !ValidEmailAddressRegex().IsMatch(FromAddress))
            {
                yield return new("From address is not a valid email address.", [nameof(FromAddress)]);
            }
        }

        // Uses the HTML5 living standard, does a willful violation of RFC-5322.
        // see https://html.spec.whatwg.org/multipage/input.html#valid-e-mail-address
        [GeneratedRegex(
        @"^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$",
        RegexOptions.Compiled)]
        public static partial Regex ValidEmailAddressRegex();
    }
}   
