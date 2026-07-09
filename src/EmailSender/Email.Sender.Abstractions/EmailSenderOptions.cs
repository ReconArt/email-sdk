using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Net.Security;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
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
        /// Gets or sets the authentication flow used when connecting to the SMTP server.
        /// <br/><br/>
        /// <i>Default value:</i> <see cref="EmailSenderAuthenticationType.Basic"/>
        /// </summary>
        public EmailSenderAuthenticationType AuthenticationType { get; set; } = EmailSenderAuthenticationType.Basic;

        /// <summary>
        /// Set to <see langword="true"/> when basic authentication is required when connecting to the server.
        /// <br/><br/>
        /// <i>Default value:</i> <see langword="true"/>
        /// </summary>
        /// <remarks>
        /// This property only applies when <see cref="AuthenticationType"/> is <see cref="EmailSenderAuthenticationType.Basic"/>.
        /// <br/>
        /// When enabled, <seealso cref="Username"/> and <seealso cref="Password"/> will be used to perform the authentication.
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
        /// <remarks>
        /// This property is only used when <see cref="AuthenticationType"/> is <see cref="EmailSenderAuthenticationType.Basic"/>
        /// and <see cref="RequiresAuthentication"/> is enabled.
        /// </remarks>
        public string? Password { get; set; }

        /// <summary>
        /// OAuth2 access token to authenticate as for the mail server.
        /// </summary>
        /// <remarks>
        /// This property is only used when <see cref="AuthenticationType"/> is <see cref="EmailSenderAuthenticationType.OAuth2"/>.
        /// </remarks>
        public string? AccessToken { get; set; }

        /// <summary>
        /// UTC expiration timestamp of the OAuth2 access token.
        /// </summary>
        /// <remarks>
        /// This property is only used when <see cref="AuthenticationType"/> is <see cref="EmailSenderAuthenticationType.OAuth2"/>.
        /// </remarks>
        public DateTime AccessTokenExpiresAtUtc { get; set; }

        /// <summary>
        /// Called when the email sender needs a refreshed OAuth2 access token.
        /// </summary>
        /// <remarks>
        /// This property is only used when <see cref="AuthenticationType"/> is <see cref="EmailSenderAuthenticationType.OAuth2"/>.
        /// The returned token values will be applied to <see cref="AccessToken"/> and <see cref="AccessTokenExpiresAtUtc"/>
        /// on the current <see cref="EmailSenderOptions"/> instance.
        /// </remarks>
        [JsonIgnore]
        public Func<CancellationToken, ValueTask<EmailSenderOAuthRefreshResult>>? RefreshAccessTokenAsync { get; set; }

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

        /// <summary>
        /// Creates a new instance of <see cref="EmailSenderOptions"/> configured for the basic SMTP flow.
        /// </summary>
        /// <param name="host">Host of the mail server.</param>
        /// <param name="port">Port of the mail server.</param>
        /// <param name="requiresAuthentication">Whether basic SMTP authentication should be performed.</param>
        /// <param name="username">Username to authenticate as.</param>
        /// <param name="password">Password to authenticate as.</param>
        /// <param name="fromAddress">Email address to send emails from.</param>
        /// <returns>A validated <see cref="EmailSenderOptions"/> instance.</returns>
        /// <exception cref="ValidationException">Thrown when the supplied values are invalid.</exception>
        public static EmailSenderOptions CreateBasic(
            string host,
            int port,
            bool requiresAuthentication = true,
            string? username = null,
            string? password = null,
            string? fromAddress = null)
        {
            EmailSenderOptions options = new()
            {
                AuthenticationType = EmailSenderAuthenticationType.Basic,
                Host = host,
                Port = port,
                RequiresAuthentication = requiresAuthentication,
                Username = username,
                Password = password,
                FromAddress = fromAddress
            };

            ValidateOrThrow(options);
            return options;
        }

        /// <summary>
        /// Creates a new instance of <see cref="EmailSenderOptions"/> configured for the OAuth2 SMTP flow.
        /// </summary>
        /// <param name="host">Host of the mail server.</param>
        /// <param name="port">Port of the mail server.</param>
        /// <param name="username">Username to authenticate as.</param>
        /// <param name="accessToken">Initial OAuth2 access token.</param>
        /// <param name="accessTokenExpiresAtUtc">Initial OAuth2 access token expiration timestamp, in UTC.</param>
        /// <param name="refreshAccessTokenAsync">Callback used to refresh the OAuth2 access token.</param>
        /// <param name="fromAddress">Email address to send emails from.</param>
        /// <returns>A validated <see cref="EmailSenderOptions"/> instance.</returns>
        /// <exception cref="ValidationException">Thrown when the supplied values are invalid.</exception>
        public static EmailSenderOptions CreateOAuth2(
            string host,
            int port,
            string username,
            string accessToken,
            DateTime accessTokenExpiresAtUtc,
            Func<CancellationToken, ValueTask<EmailSenderOAuthRefreshResult>> refreshAccessTokenAsync,
            string? fromAddress = null)
        {
            EmailSenderOptions options = new()
            {
                AuthenticationType = EmailSenderAuthenticationType.OAuth2,
                Host = host,
                Port = port,
                RequiresAuthentication = true,
                Username = username,
                AccessToken = accessToken,
                AccessTokenExpiresAtUtc = accessTokenExpiresAtUtc,
                RefreshAccessTokenAsync = refreshAccessTokenAsync,
                FromAddress = fromAddress
            };

            ValidateOrThrow(options);
            return options;
        }

        /// <inheritdoc/>
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (!Enum.IsDefined(AuthenticationType))
            {
                yield return new("AuthenticationType is not valid.", [nameof(AuthenticationType)]);
            }

            if (AuthenticationType == EmailSenderAuthenticationType.Basic)
            {
                if (RequiresAuthentication)
                {
                    if (string.IsNullOrWhiteSpace(Username))
                    {
                        yield return new("Username is required when basic authentication is enabled.", [nameof(Username)]);
                    }

                    if (string.IsNullOrWhiteSpace(Password))
                    {
                        yield return new("Password is required when basic authentication is enabled.", [nameof(Password)]);
                    }

                    if (!IsUsernameEmailAddress && string.IsNullOrWhiteSpace(FromAddress))
                    {
                        yield return new("From header is required when username is not an email address.", [nameof(FromAddress)]);
                    }
                }
                else if (string.IsNullOrWhiteSpace(FromAddress))
                {
                    yield return new("From header is required when authentication is disabled.", [nameof(FromAddress)]);
                }
            }
            else if (AuthenticationType == EmailSenderAuthenticationType.OAuth2)
            {
                if (string.IsNullOrWhiteSpace(Username))
                {
                    yield return new("Username is required when OAuth2 authentication is enabled.", [nameof(Username)]);
                }

                if (string.IsNullOrWhiteSpace(AccessToken))
                {
                    yield return new("AccessToken is required when OAuth2 authentication is enabled.", [nameof(AccessToken)]);
                }

                if (AccessTokenExpiresAtUtc == default)
                {
                    yield return new("AccessTokenExpiresAtUtc is required when OAuth2 authentication is enabled.", [nameof(AccessTokenExpiresAtUtc)]);
                }

                if (RefreshAccessTokenAsync is null)
                {
                    yield return new("RefreshAccessTokenAsync is required when OAuth2 authentication is enabled.", [nameof(RefreshAccessTokenAsync)]);
                }

                if (!IsUsernameEmailAddress && string.IsNullOrWhiteSpace(FromAddress))
                {
                    yield return new("From header is required when username is not an email address.", [nameof(FromAddress)]);
                }
            }

            if (FromAddress is not null && !ValidEmailAddressRegex().IsMatch(FromAddress))
            {
                yield return new("From address is not a valid email address.", [nameof(FromAddress)]);
            }

            if (MessageQueueSize < 1 && MessageQueueSize != -1)
            {
                yield return new("MessageQueueSize must be greater than or equal to 1 or be set to unlimited capacity (-1).", [nameof(MessageQueueSize)]);
            }
        }

        // Uses the HTML5 living standard, does a willful violation of RFC-5322.
        // see https://html.spec.whatwg.org/multipage/input.html#valid-e-mail-address
        [GeneratedRegex(
        @"^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$",
        RegexOptions.Compiled)]
        public static partial Regex ValidEmailAddressRegex();

        private static void ValidateOrThrow(EmailSenderOptions options) =>
            Validator.ValidateObject(options, new ValidationContext(options), validateAllProperties: true);
    }
}   
