using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Utils;
using Polly.Contrib.WaitAndRetry;
using ReconArt.Email.Sender.Internal;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace ReconArt.Email
{
    /// <summary>
    /// Sends emails using a pool of SMTP connections.
    /// </summary>
    /// <remarks>
    /// The pool holds up to <see cref="EmailSenderStartupOptions.MaxConcurrentConnections"/> connections, each
    /// owned exclusively by the thread processing a message. Queuing is multi-threaded via a bounded
    /// <see cref="ActionBlock{TInput}"/>; when the queue is full, scheduling calls await asynchronously until
    /// capacity is available.
    /// </remarks>
    public partial class EmailSenderService : IEmailSenderService, IAsyncDisposable
    {
        // Access tokens within this window of expiry are refreshed proactively, so a token
        // cannot expire between the fetch-time check and its use on the wire.
        private static readonly TimeSpan OAuthTokenExpirySkew = TimeSpan.FromMinutes(1);

        // After a failed refresh, further attempts are skipped for this long so a dead
        // refresh token cannot hammer the token endpoint under queue load.
        private static readonly TimeSpan OAuthRefreshFailureCooldown = TimeSpan.FromSeconds(30);

        private readonly SmtpClient[] _connections;
        private readonly int[] _connectionStatus; // 0=available, 1=in-use
        private readonly ConnectionCredentials?[] _connectionCredentials;

        private readonly ILogger<EmailSenderService> _logger;
        private readonly IOptionsMonitor<EmailSenderOptions>? _mailOptions;
        private readonly IEmailSenderOptionsProvider? _optionsProvider;
        private readonly ActionBlock<QueuedMail> _emailScheduleWork;
        private readonly EmailSenderStartupOptions _startupOptions;
        private ConnectionCredentials? _currentCredentials;

        // The service's own OAuth2 token knowledge, layered over whatever the options source
        // supplies. Swapped atomically, never mutated. Null while the source token is authoritative.
        private OAuthTokenState? _oauthTokenState;


        // Single-flight gates (null when idle): at most one of each runs at a time; concurrent
        // callers coalesce onto the in-flight task instead of repeating the work. That keeps N
        // OAuth failures from stampeding the token endpoint, and keeps options fetches from
        // overlapping - so their credential publishes stay strictly ordered.
        private Task<bool>? _oauthRefreshTask;
        private Task<EmailSenderOptions?>? _optionsFetchTask;

        // The last failed refresh attempt, keyed to the source token it was attempted for -
        // rotated source credentials deserve a fresh attempt regardless of the cooldown.
        // Swapped atomically; null after a successful refresh.
        private OAuthRefreshFailure? _oauthRefreshFailure;

        private int _failedMessagesCount;
        private bool _disposed;

        /// <summary>
        /// Creates an instance of <see cref="EmailSenderService"/>.
        /// </summary>
        /// <param name="options">Email sender options.</param>
        /// <param name="startupOptions">Email sender startup options.</param>
        /// <param name="logger">Email sender logger.</param>
        public EmailSenderService(IOptionsMonitor<EmailSenderOptions> options, EmailSenderStartupOptions startupOptions, ILogger<EmailSenderService> logger)
            : this(options, null, TransformEmailSenderStartupOptions(startupOptions), logger)
        {
        }

        /// <summary>
        /// Creates an instance of <see cref="EmailSenderService"/>.
        /// </summary>
        /// <param name="options">Email sender options.</param>
        /// <param name="startupOptions">Email sender startup options</param>
        /// <param name="logger">Email sender logger.</param>
        public EmailSenderService(IOptionsMonitor<EmailSenderOptions> options, IOptions<EmailSenderStartupOptions> startupOptions, ILogger<EmailSenderService> logger)
            : this(options, null, startupOptions, logger)
        {
        }

        /// <summary>
        /// Creates an instance of <see cref="EmailSenderService"/>.
        /// </summary>
        /// <param name="options">Email sender options.</param>
        /// <param name="startupOptions">Email sender startup options.</param>
        /// <param name="configureLogger">
        /// An optional action to configure the <see cref="ILoggerFactory"/> used by the <see cref="EmailSenderService"/>.
        /// Leave <see langword="null"/> to effectively disable logging.
        /// </param>
        public EmailSenderService(EmailSenderOptions options, EmailSenderStartupOptions startupOptions, Action<ILoggingBuilder>? configureLogger = null)
            : this(TransformEmailSenderOptions(options), startupOptions, InternalLoggerFactory.CreateLogger<EmailSenderService>(configureLogger))
        {
        }

        /// <summary>
        /// Creates an instance of <see cref="EmailSenderService"/>.
        /// </summary>
        /// <param name="optionsProvider">Email sender runtime options provider.</param>
        /// <param name="startupOptions">Email sender startup options.</param>
        /// <param name="configureLogger">
        /// An optional action to configure the <see cref="ILoggerFactory"/> used by the <see cref="EmailSenderService"/>.
        /// Leave <see langword="null"/> to effectively disable logging.
        /// </param>
        public EmailSenderService(IEmailSenderOptionsProvider optionsProvider, EmailSenderStartupOptions startupOptions, Action<ILoggingBuilder>? configureLogger = null)
            : this(null, optionsProvider, TransformEmailSenderStartupOptions(startupOptions), InternalLoggerFactory.CreateLogger<EmailSenderService>(configureLogger))
        {
        }

        /// <summary>
        /// Creates an instance of <see cref="EmailSenderService"/>.
        /// </summary>
        /// <param name="optionsProvider">Email sender runtime options provider.</param>
        /// <param name="startupOptions">Email sender startup options.</param>
        /// <param name="logger">Email sender logger.</param>
        public EmailSenderService(IEmailSenderOptionsProvider optionsProvider, IOptions<EmailSenderStartupOptions> startupOptions, ILogger<EmailSenderService> logger)
            : this(null, optionsProvider, startupOptions, logger)
        {
        }


        internal EmailSenderService(
            IOptionsMonitor<EmailSenderOptions>? mailOptions,
            IEmailSenderOptionsProvider? mailOptionsProvider,
            IOptions<EmailSenderStartupOptions> startupOptions,
            ILogger<EmailSenderService> logger)
        {
            if (mailOptionsProvider is not null && mailOptions is not null)
            {
                throw new InvalidOperationException($"Cannot create an instance of type {typeof(EmailSenderService).FullName} with a mixed-configuration truth." +
                    $" If you see this, contact the developer.");
            }
            if (mailOptions is null)
            {
                if (mailOptionsProvider is null)
                {
                    throw new InvalidOperationException($"Either {nameof(mailOptions)} or {nameof(mailOptionsProvider)} must have a non-null value.");
                }
            }

            EmailSenderStartupOptions startupConfigurationOptions = startupOptions.Value;
            ObjectValidator.ValidateObjectOrThrow(startupConfigurationOptions);

            _startupOptions = startupConfigurationOptions;
            _mailOptions = mailOptions;
            _optionsProvider = mailOptionsProvider;
            _logger = logger;
            _emailScheduleWork = new ActionBlock<QueuedMail>(ProcessMessageAsync, new ExecutionDataflowBlockOptions
            {
                EnsureOrdered = false,
                MaxDegreeOfParallelism = Math.Max(startupConfigurationOptions.MaxConcurrentConnections, 1),
                SingleProducerConstrained = false,
                TaskScheduler = TaskScheduler.Default,
                BoundedCapacity = startupConfigurationOptions.MessageQueueSize
            });

            SmtpClient[] connections = new SmtpClient[startupConfigurationOptions.MaxConcurrentConnections];
            for (int i = 0; i < connections.Length; i++)
            {
                SmtpClient client = new();
                if (startupConfigurationOptions.ServerCertificateValidationCallback is not null)
                {
                    client.ServerCertificateValidationCallback = startupConfigurationOptions.ServerCertificateValidationCallback;
                }

                connections[i] = client;
            }

            _connections = connections;
            _connectionStatus = new int[connections.Length];
            _connectionCredentials = new ConnectionCredentials?[connections.Length];
        }

        #region Public_Methods

        /// <inheritdoc/>
        public ValueTask<bool> TrySendAsync(IEmailMessage email, CancellationToken cancellationToken = default) =>
            InternalTryScheduleAsync(email, true, cancellationToken);

        /// <inheritdoc/>
        public ValueTask<bool> TryScheduleAsync(IEmailMessage email, CancellationToken cancellationToken = default) =>
            InternalTryScheduleAsync(email, false, cancellationToken);

        /// <inheritdoc/>
        public async ValueTask<Exception?> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            SmtpClient? client = null;
            try
            {
                EmailSenderOptions? options = await GetOptionsAsync(cancellationToken).ConfigureAwait(false);
                if (options is null)
                {
                    return new InvalidOperationException("Email sender is not configured.");
                }

                // Probe with the same effective credentials the pool would use - the published
                // snapshot has the current OAuth2 token overlaid, which `options` may not.
                ConnectionCredentials credentials = Volatile.Read(ref _currentCredentials)!;
                Debug.Assert(credentials is not null,
                    "A successful options fetch always publishes a credentials snapshot.");

                client = new SmtpClient();
                if (_startupOptions.ServerCertificateValidationCallback is not null)
                {
                    client.ServerCertificateValidationCallback = _startupOptions.ServerCertificateValidationCallback;
                }

                bool refreshedAfterFailure = false;
                while (true)
                {
                    Exception? attemptError;
                    bool shouldRefreshToken;
                    try
                    {
                        await ConnectAndAuthenticateAsync(client, credentials, cancellationToken).ConfigureAwait(false);
                        return null;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Classify only. All recovery runs below, on a fully unwound stack - never
                        // inside this catch.
                        attemptError = ex;
                        shouldRefreshToken = credentials.AuthenticationType == EmailSenderAuthenticationType.OAuth2
                            && IsAuthenticationFailure(ex)
                            && !refreshedAfterFailure;
                    }

                    if (!shouldRefreshToken)
                    {
                        return attemptError;
                    }

                    // The OAuth2 token was rejected: refresh it once, then retry against a fresh session.
                    refreshedAfterFailure = true;
                    if (!await RefreshOAuthTokenAsync(options, credentials.AccessToken, cancellationToken).ConfigureAwait(false))
                    {
                        return attemptError;
                    }

                    RefreshConnectionInfo(options);
                    credentials = Volatile.Read(ref _currentCredentials) ?? credentials;
                    await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Every genuine failure is returned to the caller as the result value. The filter
                // deliberately excludes OperationCanceledException - cancellation is not caught
                // here or in the loop, so it propagates out and throws, like the rest of the API.
                return ex;
            }
            finally
            {
                if (client is not null)
                {
                    // Do not pass the cancellation token. We want to disconnect gracefully.
#pragma warning disable CA2016 // Forward the 'CancellationToken' parameter to methods
                    await client.DisconnectAsync(true).ConfigureAwait(false);
#pragma warning restore CA2016 // Forward the 'CancellationToken' parameter to methods
                    client.Dispose();
                }
            }
        }

        /// <inheritdoc/>
        public int GetFailedMessagesCount() => Volatile.Read(ref _failedMessagesCount);

        /// <inheritdoc/>
        public void ResetCount() => Volatile.Write(ref _failedMessagesCount, 0);

        /// <summary>
        /// Disposes the <see cref="EmailSenderService"/>.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await DisposeAsyncCore().ConfigureAwait(false);

            GC.SuppressFinalize(this);
        }

        #endregion

        #region Protected_Methods

        /// <summary>
        /// Performs cleanup of managed/unmanaged resources associated with <see cref="EmailSenderService"/>.
        /// </summary>
        /// <remarks>
        /// Derived classes should modify this method to release resources as needed.
        /// </remarks>
        /// <returns></returns>
        protected virtual async ValueTask DisposeAsyncCore()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _emailScheduleWork.Complete();
                try
                {
                    await _emailScheduleWork.Completion.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // A faulted block must not prevent the pool below from being disposed.
                    _logger.LogError(ex, "Email processing stopped with an error.");
                }

                for (int i = 0; i < _connections.Length; i++)
                {
                    SmtpClient smtpClient = _connections[i];

                    try
                    {
                        await smtpClient.DisconnectAsync(true).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // One broken session must not abandon disposal of the rest of the pool.
                        _logger.LogError(ex, "Could not gracefully disconnect an SMTP connection during dispose.");
                    }
                    finally
                    {
                        smtpClient.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not properly dispose service.");
            }
            finally
            {
                _disposed = true;
            }
        }

        #endregion

        #region Private_Methods

        private async ValueTask<bool> InternalTryScheduleAsync(IEmailMessage email, bool awaitCompletion, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                email.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            EmailSenderOptions? mailOptions;
            try
            {
                mailOptions = await GetOptionsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch when (ExceptionFilters.DisposeWithoutUnwindingStack(email))
            {
                throw;
            }

            if (mailOptions is null)
            {
                Interlocked.Increment(ref _failedMessagesCount);
                email.Dispose();
                return false;
            }

            MimeMessage? mimeMessage = CreateMimeMessage(email, mailOptions, out bool treatAsSuccess);
            if (mimeMessage is null)
            {
                if (!treatAsSuccess && mailOptions.SignalFailureOnInvalidParameters)
                {
                    await OnEmailSendingFailureAsync(email, mailOptions, EmailFailureReason.InvalidParameters).ConfigureAwait(false);
                }

                email.Dispose();
                return treatAsSuccess;
            }

            // If we do not need to await this, **DO NOT** pass cancellation token to the queued mail.
            // Instead, use that cancellation token in the scheduling of the task only.
            QueuedMail queuedMail = awaitCompletion
                ? new(mimeMessage, email, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously), cancellationToken)
                : new(mimeMessage, email, CancellationToken.None);

            bool queued;
            try
            {
                queued = await _emailScheduleWork.SendAsync(queuedMail, cancellationToken).ConfigureAwait(false);
            }
            catch when (ExceptionFilters.DisposeWithoutUnwindingStack(queuedMail))
            {
                throw;
            }

            if (!queued)
            {
                _logger.LogError("Email message could not be processed. " +
                    "Service has stopped accepting new email messages.");

                await OnEmailSendingFailureAsync(email, mailOptions, EmailFailureReason.Unknown).ConfigureAwait(false);
                queuedMail.Dispose();

                return false;
            }

            _logger.LogInformation("Email to {Recipients} has been scheduled for sending.",
                string.Join(", ",
                    mimeMessage.To.Cast<MailboxAddress>().Select(static adr => adr.Address)));

            return !awaitCompletion || await queuedMail.TaskCompletionSource!.Task.ConfigureAwait(false);
        }

        private MimeMessage? CreateMimeMessage(IEmailMessage email, EmailSenderOptions mailOptions, out bool treatAsSuccess)
        {
            treatAsSuccess = false;
            try
            {
                ParserOptions parserOptions = CreateParserOptions(mailOptions);
                IEnumerable<string> recipients = email.Recipients;
                HashSet<string> uniqueRecipients = [];

                if (mailOptions.EnableTempMailRouting)
                {
                    foreach (string recipient in recipients)
                    {
                        string actualRecipient = MailRoutingRegex().Replace(recipient, "@");
                        if (!string.Equals(recipient, actualRecipient, StringComparison.Ordinal))
                        {
                            _logger.LogInformation("Recipient '{Recipient}' was routed to '{ActualRecipient}'.", recipient, actualRecipient);
                        }

                        if (!uniqueRecipients.Add(actualRecipient))
                        {
                            _logger.LogInformation("Skipped adding '{Recipient}' as a recipient, because it already exists.", actualRecipient);
                        }
                    }
                }
                else
                {
                    foreach (string recipient in recipients)
                    {
                        if (!uniqueRecipients.Add(recipient))
                        {
                            _logger.LogInformation("Skipped adding '{Recipient}' as a recipient, because it already exists.", recipient);
                        }
                    }
                }

                recipients = uniqueRecipients;

                IEnumerable<string> filteredRecipients = mailOptions.Whitelist.Length == 0 ? recipients : recipients.Intersect(mailOptions.Whitelist);
                if (!filteredRecipients.Any())
                {
                    if (recipients.Any())
                    {
                        _logger.LogInformation("The following recipients were skipped because they were not present in the whitelist: {Recipients}.",
                            string.Join(",", recipients));

                        if (mailOptions.TreatEmptyRecipientsAsSuccess)
                        {
                            _logger.LogInformation("An email with no recipients after applying the whitelist was treated as successfully processed.");
                            treatAsSuccess = true;
                        }
                        else
                        {
                            _logger.LogWarning("Email will not be processed because it has no recipients left, after applying the whitelist.");
                        }
                    }
                    else
                    {
                        if (mailOptions.TreatEmptyRecipientsAsSuccess)
                        {
                            _logger.LogInformation("An email with no recipients was treated as successfully processed.");
                            treatAsSuccess = true;
                        }
                        else
                        {
                            _logger.LogWarning("Email will not be processed because it has no recipients.");
                        }
                    }

                    return null;
                }
                else if (recipients.Except(filteredRecipients) is IEnumerable<string> removedEntries && removedEntries.Any())
                {
                    _logger.LogInformation("The following recipients were skipped because they were not present in the whitelist: {Recipients}.",
                        string.Join(",", removedEntries));
                }

                BodyBuilder mimeBuilder = new();
                if (email.Attachments.Any())
                {
                    ReadOnlySpan<char> emailBody = (email.Body?.Trim() ?? string.Empty).AsSpan();
                    StringBuilder bodyBuilder = new();
                    bodyBuilder.Append(emailBody);

                    foreach (var attachment in email.Attachments)
                    {
                        if (attachment.Placeholder is not null)
                        {
                            MimeEntity inlineAttachment = mimeBuilder.LinkedResources.AddSerialized(attachment);

                            string contentId = MimeUtils.GenerateMessageId();
                            inlineAttachment.ContentId = contentId;
                            if (!emailBody.Contains(attachment.Placeholder, StringComparison.Ordinal))
                            {
                                if (mailOptions.VerifyInlineAttachments)
                                {
                                    _logger.LogWarning(
                                        "Attachment with placeholder '{Placeholder}' was not found in the email body. " +
                                        "{ParamName} is enabled, mail will not be processed.", attachment.Placeholder, nameof(mailOptions.VerifyInlineAttachments));
                                    return null;
                                }

                                _logger.LogWarning(
                                    "Attachment with placeholder '{Placeholder}' was not found in the email body, but will still be processed. " +
                                    "Enable {ParamName} to drop emails with missing attachment placeholders in their body.",
                                    attachment.Placeholder, nameof(mailOptions.VerifyInlineAttachments));
                            }
                            else
                            {
                                bodyBuilder.Replace(attachment.Placeholder, string.Format(CultureInfo.InvariantCulture, "cid:{0}", contentId));
                            }
                        }
                        else
                        {
                            mimeBuilder.Attachments.AddSerialized(attachment);
                        }
                    }

                    mimeBuilder.HtmlBody = bodyBuilder.ToString();
                }
                else
                {
                    mimeBuilder.HtmlBody = email.Body?.Trim() ?? string.Empty;
                }

                MimeMessage mail = new()
                {
                    Subject = email.Subject ?? string.Empty,
                    Body = mimeBuilder.ToMessageBody(),
                    Importance = email.IsImportant ? MessageImportance.High : MessageImportance.Normal,
                };

                MailboxAddress? fromAddress = null;
                MailboxAddress? senderAddress = null;

                // Whether the send authenticates under an identity we can derive the From header
                // from. OAuth2 always authenticates; Basic only when RequiresAuthentication is set
                // (that flag applies to Basic alone). Keying off AuthenticationType keeps this in
                // lockstep with what Validate() enforces, so the two can never disagree.
                bool usesAuthenticatedIdentity =
                    mailOptions.AuthenticationType == EmailSenderAuthenticationType.OAuth2
                    || (mailOptions.AuthenticationType == EmailSenderAuthenticationType.Basic && mailOptions.RequiresAuthentication);

                if (usesAuthenticatedIdentity && mailOptions.IsUsernameEmailAddress)
                {
                    // Defensive check. Under normal conditions should never be hit.
                    if (string.IsNullOrWhiteSpace(mailOptions.Username))
                    {
                        _logger.LogCritical("Malformed configuration! Username is required when authentication is enabled, but was missing.");
                        return null;
                    }

                    if (mailOptions.FromAddress is not null && mailOptions.FromAddress != mailOptions.Username)
                    {
                        if (!TryParseEmailAddress(parserOptions, mailOptions.FromAddress, "From", out fromAddress))
                        {
                            return null;
                        }
                        if (!TryParseEmailAddress(parserOptions, mailOptions.Username, "Sender", out senderAddress))
                        {
                            return null;
                        }
                    }
                    else if (!TryParseEmailAddress(parserOptions, mailOptions.Username, "From", out fromAddress))
                    {
                        return null;
                    }
                }
                else
                {
                    // Defensive check. Under normal conditions should never be hit.
                    if (string.IsNullOrWhiteSpace(mailOptions.FromAddress))
                    {
                        _logger.LogCritical("Malformed configuration! " +
                            "From address is required when no authentication is necessary, or when the username used is not an email address.");
                        return null;
                    }

                    if (!TryParseEmailAddress(parserOptions, mailOptions.FromAddress, "From", out fromAddress))
                    {
                        return null;
                    }
                }

                mail.From.Add(fromAddress);
                if (senderAddress is not null)
                {
                    mail.Sender = senderAddress;
                }

                foreach (string recipient in filteredRecipients)
                {
                    if (MailboxAddress.TryParse(parserOptions, recipient, out MailboxAddress? emailAdress))
                    {
                        mail.To.Add(emailAdress);
                    }
                    else
                    {
                        _logger.LogWarning("Skipped adding {Recipient} as recipient because it is not a valid email address.", recipient);
                    }
                }

                if (mail.To.Count != 0)
                {
                    return mail;
                }
                else
                {
                    if (mailOptions.TreatEmptyRecipientsAsSuccess)
                    {
                        _logger.LogInformation("An email with invalid recipient addresses was treated as successfully processed.");
                        treatAsSuccess = true;
                        return null;
                    }

                    _logger.LogWarning("Email will not be processed because all remaining recipients had invalid addresses.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "There was a problem creating a MimeMessage for {Recipients}", string.Join(',', email.Recipients));
                return null;
            }
        }

        /// <summary>
        /// Connects (if needed) and authenticates <paramref name="smtpClient"/> for the given
        /// credential generation. Shared by the pooled send path and <see cref="TestConnectionAsync"/>.
        /// Throws on failure; callers classify the exception.
        /// </summary>
        private static async Task ConnectAndAuthenticateAsync(SmtpClient smtpClient, ConnectionCredentials credentials, CancellationToken cancellationToken)
        {
            if (!smtpClient.IsConnected)
            {
                await smtpClient.ConnectAsync(credentials.Host, credentials.Port, SecureSocketOptions.Auto, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            if (credentials.AuthenticationType == EmailSenderAuthenticationType.OAuth2)
            {
                if (!smtpClient.IsAuthenticated)
                {
                    SaslMechanismOAuth2 oauthMechanism = new(credentials.Username ?? string.Empty, credentials.AccessToken ?? string.Empty);
                    await smtpClient.AuthenticateAsync(oauthMechanism, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (credentials.RequiresAuthentication && !smtpClient.IsAuthenticated)
            {
                await smtpClient.AuthenticateAsync(credentials.Username ?? string.Empty, credentials.Password ?? string.Empty, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private async ValueTask<SmtpConnectionResult> TryToConnectAndAuthenticateSmtpClientAsync(
            ConnectionCredentials credentials,
            int connectionIndex,
            SmtpClient smtpClient,
            CancellationToken cancellationToken)
        {
            try
            {
                // Staleness is derived, not recorded: the session is stale exactly when it last
                // authenticated against a different credentials generation than the one this
                // message was acquired with. We own the slot, so a plain read is safe.
                if (!ReferenceEquals(_connectionCredentials[connectionIndex], credentials) && smtpClient.IsConnected)
                {
                    // A stale session cannot be re-authenticated in place - MailKit throws on an
                    // already-authenticated session, and a host/port change needs a fresh socket
                    // regardless. Tear down and rebuild.
                    await smtpClient.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
                }

                await ConnectAndAuthenticateAsync(smtpClient, credentials, cancellationToken).ConfigureAwait(false);

                // Record the generation only on full success: cancel or fail halfway and the slot
                // keeps its old (or null) reference, so the next owner sees a mismatch and
                // rebuilds - half-connected sessions self-heal. A plain write is safe - we own
                // the slot; the Interlocked handoff in ReleaseConnection publishes it onward.
                _connectionCredentials[connectionIndex] = credentials;
            }
            catch (OperationCanceledException) when (LogOperationCancelledWithoutUnwinding(_logger))
            {
                // Honor typical async patterns, by keeping cancellation as an exception.
                throw;
            }
            catch (Exception ex) when (credentials.AuthenticationType == EmailSenderAuthenticationType.OAuth2 && IsAuthenticationFailure(ex))
            {
                _logger.LogWarning(ex, "OAuth2 authentication with the SMTP server failed. A token refresh will be attempted.");
                return new(false, ShouldRefreshOAuthToken: true, FailFast: false, EmailFailureReason.AuthenticationFailed);
            }
            catch (Exception ex) when (IsAuthenticationFailure(ex))
            {
                _logger.LogCritical(ex, "Authentication with the SMTP server failed.");
                return new(false, ShouldRefreshOAuthToken: false, FailFast: true, EmailFailureReason.AuthenticationFailed);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Critical error in connecting to SMTP server.");
                return new(false, ShouldRefreshOAuthToken: false, FailFast: false, EmailFailureReason.Unknown);
            }

            return new(true, ShouldRefreshOAuthToken: false, FailFast: false, EmailFailureReason.None);
        }

        private async Task<bool> ProcessMessageAsync(QueuedMail queuedMail)
        {
            try
            {
                bool wasDisposed = false;
                EmailSenderOptions? mailOptions;
                var recipientsFormatted = string.Join(", ",
                    queuedMail.MimeMessage.To.Cast<MailboxAddress>().Select(static adr => adr.Address));

                _logger.LogInformation("Message for {Recipients} is being processed...", recipientsFormatted);
                mailOptions = await GetOptionsAsync(queuedMail.CancellationToken).ConfigureAwait(false);
                if (mailOptions is null)
                {
                    _logger.LogWarning(
                        "Message for {Recipients} could not be processed - no SMTP configuration is available.",
                        recipientsFormatted);

                    Interlocked.Increment(ref _failedMessagesCount);
                    return false;
                }

                (SmtpClient smtpClient, int connectionIndex, ConnectionCredentials credentials, bool requiresReauthentication) = GetConnection();
                try
                {
                    try
                    {
                        // No point probing a session that is about to be torn down and rebuilt.
                        if (!requiresReauthentication && smtpClient.IsConnected)
                        {
                            await smtpClient.NoOpAsync(queuedMail.CancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        _logger.LogError("Could not send message to SMTP server - the client was disposed.");
                        wasDisposed = true;
                    }
                    catch (OperationCanceledException) when (LogOperationCancelledWithoutUnwinding(_logger))
                    {
                        // Honor typical async patterns, by keeping cancellation as an exception.
                        throw;
                    }
                    catch
                    {
                        _logger.LogInformation("Failed to keep the underlying connection alive. Will re-connect and re-authenticate where appropriate.");
                    }

                    return wasDisposed
                        ? await OnEmailSendingFailureAsync(queuedMail.Message, mailOptions, EmailFailureReason.Disposed).ConfigureAwait(false)
                        : await SendMessageAsync(smtpClient, connectionIndex, credentials, queuedMail, mailOptions).ConfigureAwait(false);
                }
                finally
                {
                    ReleaseConnection(connectionIndex);
                }
            }
            finally
            {
                queuedMail.Dispose();
            }
        }

        private bool TryParseEmailAddress(ParserOptions parserOptions, string address, string headerType, [NotNullWhen(true)] out MailboxAddress? parsedAddress)
        {
            if (!MailboxAddress.TryParse(parserOptions, address, out parsedAddress))
            {
                _logger.LogCritical("Failed to parse {Address} as an email address for the \"{HeaderType}\" header.", address, headerType);
                return false;
            }
            return true;
        }

        private (SmtpClient Client, int Index, ConnectionCredentials Credentials, bool RequiresReauthentication) GetConnection()
        {
            // Find the closest or "hottest" available connection.
            for (int i = 0; i < _connections.Length; i++)
            {
                // Try to acquire the connection by marking it as in-use atomically.
                if (Interlocked.CompareExchange(ref _connectionStatus[i], 1, 0) == 0)
                {
                    // Acquire paired with the release in RefreshConnectionInfo: observing the new
                    // reference guarantees observing the record's fully-initialized fields.
                    ConnectionCredentials? current = Volatile.Read(ref _currentCredentials);

                    Debug.Assert(current is not null,
                        "Connections are only acquired after a successful options fetch, which publishes a credentials snapshot.");

                    return (_connections[i], i, current!, !ReferenceEquals(_connectionCredentials[i], current));
                }
            }

            // No connections available.
            Debug.Fail("This should never happen under ActionBlock constraints.");

            _logger.LogCritical("No SMTP connections available");
            throw new InvalidOperationException("No SMTP connections available.");
        }

        private void ReleaseConnection(int index)
        {
            // Releases the slot. This interlocked exchange is also the release fence that
            // publishes this owner's plain write to _connectionCredentials[index] to the next
            // acquirer - do not weaken it to a plain write, or the slot-handoff protocol breaks.
            Interlocked.Exchange(ref _connectionStatus[index], 0);
        }

        private async Task<bool> SendMessageAsync(
            SmtpClient smtpClient,
            int connectionIndex,
            ConnectionCredentials credentials,
            QueuedMail queuedMail,
            EmailSenderOptions mailOptions)
        {
            EmailFailureReason failureReason = EmailFailureReason.Unknown;
            CancellationToken cancellationToken = queuedMail.CancellationToken;
            bool refreshedAfterFailure = false;

            // To add some resiliency, we'll attempt to send the message a couple of times before giving up.
            int retryCount = mailOptions.RetryCount;
            IEnumerator<TimeSpan> delaysEnumerator = mailOptions.RetryDelayInMilliseconds <= 0 || retryCount <= 0
                ? Enumerable.Empty<TimeSpan>().GetEnumerator()
                : Backoff.DecorrelatedJitterBackoffV2(
                    medianFirstRetryDelay: TimeSpan.FromMilliseconds(mailOptions.RetryDelayInMilliseconds),
                    retryCount: retryCount).GetEnumerator();
            try
            {
                do
                {
                    SmtpConnectionResult connectionResult =
                        await TryToConnectAndAuthenticateSmtpClientAsync(credentials, connectionIndex, smtpClient, cancellationToken)
                            .ConfigureAwait(false);

                    bool shouldRefreshOAuthToken;
                    bool failFast = false;
                    if (connectionResult.Successful)
                    {
                        SmtpSendResult sendResult =
                            await TrySendingSmtpClientMailMessageAsync(smtpClient, queuedMail.MimeMessage, credentials, cancellationToken)
                                .ConfigureAwait(false);

                        if (sendResult.Successful)
                        {
                            queuedMail.Delivered();
                            return true;
                        }

                        failureReason = sendResult.FailureReason;
                        shouldRefreshOAuthToken = sendResult.ShouldRefreshOAuthToken;
                        failFast = sendResult.FailFast;
                    }
                    else
                    {
                        failureReason = connectionResult.FailureReason;
                        shouldRefreshOAuthToken = connectionResult.ShouldRefreshOAuthToken;
                        failFast = connectionResult.FailFast;
                    }

                    if (shouldRefreshOAuthToken)
                    {
                        if (refreshedAfterFailure
                            || !await RefreshOAuthTokenAsync(mailOptions, credentials.AccessToken, cancellationToken).ConfigureAwait(false))
                        {
                            // The token was already refreshed once for this message, or cannot be
                            // refreshed: further retries would reuse the same rejected token.
                            _logger.LogError("Could not send message to SMTP server - OAuth2 authentication failed and could not be recovered by a token refresh.");
                            break;
                        }

                        refreshedAfterFailure = true;

                        // Everywhere else a message retries with the same credentials snapshot it
                        // acquired at GetConnection, even if a newer one was published meanwhile.
                        // Honoring that rule here would re-authenticate with the very token the
                        // server just rejected, so this is the one place a message swaps to the
                        // newly published snapshot mid-flight.
                        RefreshConnectionInfo(mailOptions);
                        credentials = Volatile.Read(ref _currentCredentials) ?? credentials;

                        // Immediate retry - the refresh itself already consumed real time.
                        continue;
                    }

                    if (failFast)
                    {
                        break;
                    }

                    if (!delaysEnumerator.MoveNext())
                    {
                        _logger.LogError("Could not send message to SMTP server - all retries failed.");
                        break;
                    }

                    TimeSpan retryDelay = delaysEnumerator.Current;

                    _logger.LogInformation("Retrying in {RetryMailDelay}ms to send a message.", retryDelay.TotalMilliseconds);

                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                } while (true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogCritical(ex, "Caught unhandled exception while trying to send message to SMTP server.");
            }
            finally
            {
                delaysEnumerator.Dispose();
            }

            // We've failed here.
            return await OnEmailSendingFailureAsync(queuedMail.Message, mailOptions, failureReason).ConfigureAwait(false);
        }


        private async ValueTask<bool> OnEmailSendingFailureAsync(
            IEmailMessage mailMessage,
            EmailSenderOptions mailOptions,
            EmailFailureReason failureReason)
        {
            Interlocked.Increment(ref _failedMessagesCount);

            if (mailOptions.OnEmailSendingFailure is not null)
            {
                try
                {
                    await mailOptions.OnEmailSendingFailure(mailMessage, failureReason).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Caught exception in user-provided delegate.");
                }
            }

            return false;
        }

        private async ValueTask<SmtpSendResult> TrySendingSmtpClientMailMessageAsync(
            SmtpClient smtpClient,
            MimeMessage mail,
            ConnectionCredentials credentials,
            CancellationToken cancellationToken)
        {
            Debug.Assert(smtpClient.IsConnected,
                "Method should only be called after attempting to establish a connection with the SMTP server.");

            try
            {
                await smtpClient.SendAsync(mail, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Successfully sent message to {Recipients}.", string.Join(", ",
                    mail.To.Cast<MailboxAddress>().Select(static adr => adr.Address)));
                return new(true, false, false, EmailFailureReason.None);
            }
            catch (ObjectDisposedException)
            {
                _logger.LogError("Could not send message to SMTP server - the client was disposed.");
                return new(false, true, false, EmailFailureReason.Disposed);
            }
            catch (OperationCanceledException) when (LogOperationCancelledWithoutUnwinding(_logger))
            {
                // Honor typical async patterns, by keeping cancellation as an exception.
                throw;
            }
            catch (SmtpCommandException ex) when (ex.ErrorCode == SmtpErrorCode.RecipientNotAccepted && IsPermanentSmtpFailure(ex.StatusCode))
            {
                _logger.LogError("Could not send message to SMTP server - recipient {Recipient}" +
                    " was permanently rejected ({StatusCode} {Response}). Mail will be dropped.",
                    ex.Mailbox?.Address, (int)ex.StatusCode, ex.Message);
                return new(false, true, false, EmailFailureReason.InvalidAddress);
            }
            catch (SmtpCommandException ex) when (ex.ErrorCode == SmtpErrorCode.SenderNotAccepted && IsPermanentSmtpFailure(ex.StatusCode))
            {
                _logger.LogCritical("Could not send message to SMTP server as {FromAddress}" +
                    " - the sender was permanently rejected ({StatusCode} {Response}). " +
                    "Make sure your account has permission to send from this address.",
                    ((MailboxAddress)mail.From[0]).Address, (int)ex.StatusCode, ex.Message);
                return new(false, true, false, EmailFailureReason.SendAsDenied);
            }
            catch (SmtpCommandException ex) when (credentials.AuthenticationType == EmailSenderAuthenticationType.OAuth2
                && ex.StatusCode == SmtpStatusCode.AuthenticationRequired)
            {
                _logger.LogWarning(ex, "SMTP server requested re-authentication. A token refresh will be attempted.");
                return new(false, false, true, EmailFailureReason.AuthenticationFailed);
            }
            catch (ServiceNotAuthenticatedException ex) when (credentials.AuthenticationType == EmailSenderAuthenticationType.OAuth2)
            {
                _logger.LogWarning(ex, "SMTP client is no longer authenticated. A token refresh will be attempted.");
                return new(false, false, true, EmailFailureReason.AuthenticationFailed);
            }
            catch (SmtpProtocolException ex)
            {
                if (!smtpClient.IsConnected)
                {
                    _logger.LogWarning("Could not send message to the SMTP server - the connection to the server was broken.");
                }
                else
                {
                    _logger.LogError(ex, "Could not send message to SMTP server.");
                }

                return new(false, false, false, EmailFailureReason.Unknown);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Could not send message to SMTP server.");
                return new(false, false, false, EmailFailureReason.Unknown);
            }
            catch (ServiceNotAuthenticatedException ex)
            {
                _logger.LogCritical(ex, "Authentication with the SMTP server was rejected while sending.");
                return new(false, true, false, EmailFailureReason.AuthenticationFailed);
            }
            catch (ServiceNotConnectedException)
            {
                _logger.LogWarning("Could not send message to the SMTP server - the connection to the server was broken.");
                return new(false, false, false, EmailFailureReason.NotConnected);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not send message to SMTP server.");
                return new(false, false, false, EmailFailureReason.Unknown);
            }
        }

        private Task<EmailSenderOptions?> GetOptionsAsync(CancellationToken cancellation = default)
        {
            while (true)
            {
                Task<EmailSenderOptions?>? inFlight = Volatile.Read(ref _optionsFetchTask);
                if (inFlight is not null)
                {
                    // Coalesce onto the in-flight fetch. Each caller awaits with its OWN token, so
                    // one caller cancelling only abandons its own wait - never the shared fetch.
                    return inFlight.WaitAsync(cancellation);
                }

                TaskCompletionSource<EmailSenderOptions?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (Interlocked.CompareExchange(ref _optionsFetchTask, completion.Task, null) is null)
                {
                    // Won the election: drive the one shared fetch, then wait on it like everyone else.
                    _ = ExecuteOptionsFetchAsync(completion);
                    return completion.Task.WaitAsync(cancellation);
                }

                // Lost the election - loop and join the winner.
            }
        }

        private async Task ExecuteOptionsFetchAsync(TaskCompletionSource<EmailSenderOptions?> completion)
        {
            try
            {
                EmailSenderOptions? options;
                IEmailSenderOptionsProvider? optionsProvider = _optionsProvider;
                if (optionsProvider is null)
                {
                    Debug.Assert(_mailOptions is not null, "Either IEmailSenderOptionsProvider is null or MailOptions is, never both.");
                    options = _mailOptions.CurrentValue;
                }
                else
                {
                    // Uncancellable on purpose: this is service-wide work whose result every
                    // coalesced caller consumes, so no single caller's token may cancel it.
                    // Callers govern their own responsiveness via WaitAsync in GetOptionsAsync.
                    options = await optionsProvider.GetOptionsAsync(CancellationToken.None).ConfigureAwait(false);
                }

                if (options is not null)
                {
                    ObjectValidator.ValidateObjectOrThrow(options);

                    if (options.AuthenticationType == EmailSenderAuthenticationType.OAuth2)
                    {
                        // Proactive: refresh before the token is ever used, so the connect
                        // path can stay dumb and authenticate with whatever is published.
                        await EnsureUsableOAuthTokenAsync(options, CancellationToken.None).ConfigureAwait(false);
                    }

                    // Pull-based refresh: one mechanism covers monitor reloads, fresh rows from
                    // the provider, and in-place OAuth token mutations - those sources don't
                    // share a change-notification channel, so pushing can't cover them all.
                    // Single-flight makes these publishes non-overlapping, so a stale generation
                    // can never overwrite a fresher one on the pooled connections.
                    RefreshConnectionInfo(options);
                }

                completion.TrySetResult(options);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Could not fetch mail options.");
                completion.TrySetResult(null);
            }
            finally
            {
                // Reopen the gate only after the publish above - keeps successive fetches ordered.
                Volatile.Write(ref _optionsFetchTask, null);
            }
        }

        private void RefreshConnectionInfo(EmailSenderOptions options)
        {
            // Snapshot immediately. Never compare against or cache the live options instance -
            // the OAuth refresh callback mutates it in place, which would make the comparison
            // vacuously true whenever the cached and incoming references are the same object.
            ConnectionCredentials incoming = OverlayRefreshedOAuthToken(ConnectionCredentials.From(options));

            ConnectionCredentials? current = Volatile.Read(ref _currentCredentials);
            while (true)
            {
                // Value-equality gate. This is not just an optimization: publishing a new
                // *reference* is precisely what tells every connection to re-authenticate,
                // so identical values must not produce a new reference. The CAS retry re-checks
                // against the freshly witnessed value, so two racing refreshes with equal
                // snapshots can never install two distinct-but-equal references.
                // Two racing refreshes with *different* values may still publish in either
                // order - inherent without the options source supplying a version; connections
                // converge on the next fetch.
                if (incoming == current)
                {
                    return;
                }

                ConnectionCredentials? witnessed = Interlocked.CompareExchange(ref _currentCredentials, incoming, current);
                if (ReferenceEquals(witnessed, current))
                {
                    return;
                }

                current = witnessed;
            }
        }

        /// <summary>
        /// Decides whose OAuth2 token goes into the published snapshot: the options source's,
        /// or the one the service refreshed itself (<see cref="_oauthTokenState"/>).
        /// </summary>
        /// <remarks>
        /// An options source (e.g. a database-backed provider) may keep materializing fresh
        /// instances that still carry the token our refresh already superseded - without this
        /// overlay, every message would trigger a refresh. Conversely, a token we have never
        /// seen means the source changed outside of us, so it becomes authoritative again and
        /// the service's token memory is dropped.
        /// </remarks>
        private ConnectionCredentials OverlayRefreshedOAuthToken(ConnectionCredentials incoming)
        {
            if (incoming.AuthenticationType != EmailSenderAuthenticationType.OAuth2)
            {
                return incoming;
            }

            OAuthTokenState? state = Volatile.Read(ref _oauthTokenState);
            if (state is null)
            {
                return incoming;
            }

            if (string.Equals(incoming.AccessToken, state.ReplacedSourceToken, StringComparison.Ordinal))
            {
                // The source still serves the token we already refreshed past - ours supersedes
                // it. This is what keeps a provider that materializes stale persisted rows from
                // triggering a refresh per message.
                return incoming with { AccessToken = state.AccessToken };
            }

            if (!string.Equals(incoming.AccessToken, state.AccessToken, StringComparison.Ordinal))
            {
                // The source presented a token we don't recognize (config reload, credential
                // rotation, or a provider that caught up via persistence): the source is
                // authoritative again.
                Interlocked.CompareExchange(ref _oauthTokenState, null, state);
            }

            return incoming;
        }

        private (string? AccessToken, DateTime ExpiresAtUtc) GetEffectiveOAuthToken(EmailSenderOptions options)
        {
            OAuthTokenState? state = Volatile.Read(ref _oauthTokenState);
            if (state is not null
                && (string.Equals(options.AccessToken, state.ReplacedSourceToken, StringComparison.Ordinal)
                    || string.Equals(options.AccessToken, state.AccessToken, StringComparison.Ordinal)))
            {
                return (state.AccessToken, state.ExpiresAtUtc);
            }

            return (options.AccessToken, options.AccessTokenExpiresAtUtc);
        }

        private async ValueTask EnsureUsableOAuthTokenAsync(EmailSenderOptions options, CancellationToken cancellationToken)
        {
            // A default expiry means "unknown", not "expired".
            (string? accessToken, DateTime expiresAtUtc) = GetEffectiveOAuthToken(options);

            if (!string.IsNullOrWhiteSpace(accessToken)
                && (expiresAtUtc == default || expiresAtUtc > DateTime.UtcNow + OAuthTokenExpirySkew))
            {
                return;
            }

            // Failure is tolerated here: the message proceeds with whatever token is published
            // and fails through the authentication path, which reports AuthenticationFailed.
            await RefreshOAuthTokenAsync(options, accessToken, cancellationToken).ConfigureAwait(false);
        }

        private Task<bool> RefreshOAuthTokenAsync(EmailSenderOptions options, string? staleAccessToken, CancellationToken cancellationToken)
        {
            while (true)
            {
                Task<bool>? inFlight = Volatile.Read(ref _oauthRefreshTask);
                if (inFlight is not null)
                {
                    // Join the ongoing refresh instead of stampeding the token endpoint.
                    return inFlight.WaitAsync(cancellationToken);
                }

                // A refresh may have already superseded the token we're complaining about,
                // while we were busy failing with it. If so, the fresh token is already
                // published - the caller only needs to re-pull.
                (string? effectiveToken, _) = GetEffectiveOAuthToken(options);
                if (!string.Equals(effectiveToken, staleAccessToken, StringComparison.Ordinal))
                {
                    return Task.FromResult(true);
                }

                OAuthRefreshFailure? recentFailure = Volatile.Read(ref _oauthRefreshFailure);
                if (recentFailure is not null
                    && DateTime.UtcNow.Ticks - recentFailure.TimestampTicks < OAuthRefreshFailureCooldown.Ticks
                    && string.Equals(options.AccessToken, recentFailure.SourceAccessToken, StringComparison.Ordinal))
                {
                    // A recent refresh attempt against this same source credential failed -
                    // fail fast during the cooldown instead of hammering the token endpoint
                    // once per queued message. A different source token bypasses the cooldown:
                    // rotated credentials carry a refresh token that has never been tried.
                    return Task.FromResult(false);
                }

                TaskCompletionSource<bool> refreshCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (Interlocked.CompareExchange(ref _oauthRefreshTask, refreshCompletion.Task, null) is null)
                {
                    return ExecuteOAuthRefreshAsync(options, refreshCompletion, cancellationToken);
                }

                // Lost the election - loop around and join the winner.
            }
        }

        private async Task<bool> ExecuteOAuthRefreshAsync(
            EmailSenderOptions options,
            TaskCompletionSource<bool> refreshCompletion,
            CancellationToken cancellationToken)
        {
            try
            {
                Func<CancellationToken, ValueTask<EmailSenderOAuthRefreshResult>>? refreshAccessTokenAsync = options.RefreshAccessTokenAsync;
                if (refreshAccessTokenAsync is null)
                {
                    // Unreachable through validated options; defensive for manually-mutated instances.
                    _logger.LogCritical("An OAuth2 token refresh is required, but no {CallbackName} callback is configured.",
                        nameof(EmailSenderOptions.RefreshAccessTokenAsync));
                    Volatile.Write(ref _oauthRefreshFailure, new OAuthRefreshFailure(DateTime.UtcNow.Ticks, options.AccessToken));
                    refreshCompletion.TrySetResult(false);
                    return false;
                }

                EmailSenderOAuthRefreshResult refreshResult = await refreshAccessTokenAsync(cancellationToken).ConfigureAwait(false);
                ValidateOAuthRefreshResult(refreshResult);

                // The winner's fetched options represent the source view this refresh
                // supersedes - the overlay keeps applying while the source serves that token.
                OAuthTokenState refreshedState = new(
                    refreshResult.AccessToken,
                    refreshResult.AccessTokenExpiresAtUtc,
                    refreshResult.RefreshToken,
                    ReplacedSourceToken: options.AccessToken);

                Volatile.Write(ref _oauthTokenState, refreshedState);
                Volatile.Write(ref _oauthRefreshFailure, null);

                // Documented contract: refreshed values are applied onto the current options instance.
                options.AccessToken = refreshResult.AccessToken;
                options.AccessTokenExpiresAtUtc = refreshResult.AccessTokenExpiresAtUtc;
                if (refreshResult.RefreshToken is not null)
                {
                    options.RefreshToken = refreshResult.RefreshToken;
                }

                _logger.LogInformation("OAuth2 access token was refreshed.");

                // Unblock joiners before the persistence callback: they only need the token,
                // and the callback may do slow I/O.
                refreshCompletion.TrySetResult(true);

                await InvokeOAuthCredentialsRefreshedAsync(options, refreshResult, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                // Cancellation is not a token refresh failure.
                // Let joiners handle it themselves.
                _logger.LogInformation("OAuth2 token refresh was canceled.");
                refreshCompletion.TrySetResult(false);
                throw;
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _oauthRefreshFailure, new OAuthRefreshFailure(DateTime.UtcNow.Ticks, options.AccessToken));
                _logger.LogError(ex, "Could not refresh OAuth2 access token.");
                refreshCompletion.TrySetResult(false);
                return false;
            }
            finally
            {
                Volatile.Write(ref _oauthRefreshTask, null);
            }
        }

        private async ValueTask InvokeOAuthCredentialsRefreshedAsync(
            EmailSenderOptions options,
            EmailSenderOAuthRefreshResult refreshResult,
            CancellationToken cancellationToken)
        {
            Func<EmailSenderOAuthRefreshResult, CancellationToken, ValueTask>? onRefreshed = options.OnOAuth2CredentialsRefreshed;
            if (onRefreshed is null)
            {
                return;
            }

            try
            {
                await onRefreshed(refreshResult, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Caught exception in user-provided delegate.");
            }
        }

        private static void ValidateOAuthRefreshResult(EmailSenderOAuthRefreshResult refreshResult)
        {
            ArgumentNullException.ThrowIfNull(refreshResult);

            if (string.IsNullOrWhiteSpace(refreshResult.AccessToken))
            {
                throw new InvalidOperationException("RefreshAccessTokenAsync returned an empty access token.");
            }

            // A default expiry is allowed - RFC 6749 only RECOMMENDS expires_in in the token
            // response, so a delegate may legitimately not know it. The token is then used
            // until the server rejects it. A known-past expiry, however, is a caller bug.
            if (refreshResult.AccessTokenExpiresAtUtc != default && refreshResult.AccessTokenExpiresAtUtc <= DateTime.UtcNow)
            {
                throw new InvalidOperationException("RefreshAccessTokenAsync returned an already expired access token.");
            }
        }

        private static bool IsPermanentSmtpFailure(SmtpStatusCode statusCode) =>
            // 5xx is a permanent SMTP failure (4xx is transient and should be retried).
            (int)statusCode is >= 500 and < 600
            // Auth-related 5xx codes belong to the authentication paths, not to permanent
            // address rejection - keep them out so they still reach the OAuth refresh / auth handling.
            && statusCode is not (SmtpStatusCode.AuthenticationRequired
                or SmtpStatusCode.AuthenticationMechanismTooWeak
                or SmtpStatusCode.AuthenticationInvalidCredentials);

        private static bool IsAuthenticationFailure(Exception ex) =>
            ex is AuthenticationException
                or SmtpCommandException
                {
                    StatusCode: SmtpStatusCode.AuthenticationRequired
                        or SmtpStatusCode.AuthenticationMechanismTooWeak
                        or SmtpStatusCode.AuthenticationInvalidCredentials
                };

        private static ParserOptions CreateParserOptions(EmailSenderOptions options) => new()
        {
            AddressParserComplianceMode = options.UseStrictAddressParser ? RfcComplianceMode.Strict : RfcComplianceMode.Loose,
            AllowAddressesWithoutDomain = options.AllowAddressesWithoutDomain,
            AllowUnquotedCommasInAddresses = options.AllowUnquotedCommasInAddresses
        };


        private static bool LogOperationCancelledWithoutUnwinding(ILogger<EmailSenderService> logger)
        {
            logger.LogInformation("SMTP operation was canceled.");
            return false;
        }

        private static IOptionsMonitor<EmailSenderOptions> TransformEmailSenderOptions(EmailSenderOptions options)
        {
            ObjectValidator.ValidateObjectOrThrow(options);
            return StaticOptionsMonitor.Create(options);
        }

        private static IOptions<EmailSenderStartupOptions> TransformEmailSenderStartupOptions(EmailSenderStartupOptions options)
        {
            ObjectValidator.ValidateObjectOrThrow(options);
            return Options.Create(options);
        }

        [GeneratedRegex(@"(\+|\.|\-)[0-9]+\@")]
        private static partial Regex MailRoutingRegex();

        #endregion

        /// <summary>
        /// Immutable snapshot of every option that determines whether an established,
        /// authenticated SMTP session is still valid.
        /// </summary>
        private sealed record ConnectionCredentials(
            EmailSenderAuthenticationType AuthenticationType,
            bool RequiresAuthentication,
            string Host,
            int Port,
            string? Username,
            string? Password,
            string? AccessToken)
        {
            // RefreshToken is deliberately absent: it never touches the wire, so it must not
            // participate in value-equality - otherwise a source rotating only the refresh token
            // would publish a new generation and needlessly tear down every pooled session.
            public static ConnectionCredentials From(EmailSenderOptions options) => new(
                options.AuthenticationType,
                options.RequiresAuthentication,
                options.Host,
                options.Port,
                options.Username,
                options.Password,
                options.AccessToken);
        }

        /// <summary>
        /// The OAuth2 token generation the service refreshed itself, superseding the token
        /// supplied by the options source until the source presents a different one.
        /// </summary>
        private sealed record OAuthTokenState(
            string AccessToken,
            DateTime ExpiresAtUtc,
            string? RefreshToken,
            string? ReplacedSourceToken);

        /// <summary>
        /// A failed refresh attempt, keyed to the source access token it was attempted for.
        /// </summary>
        private sealed record OAuthRefreshFailure(long TimestampTicks, string? SourceAccessToken);

        private readonly record struct SmtpConnectionResult(
            bool Successful,
            bool ShouldRefreshOAuthToken,
            bool FailFast,
            EmailFailureReason FailureReason);

        private readonly record struct SmtpSendResult(
            bool Successful,
            bool FailFast,
            bool ShouldRefreshOAuthToken,
            EmailFailureReason FailureReason);
    }
}