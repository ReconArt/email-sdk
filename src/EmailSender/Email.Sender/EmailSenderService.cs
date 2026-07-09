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
    /// Sends emails by using a single SMTP connection.
    /// </summary>
    /// <remarks>
    /// Allows multi-threaded queuing of emails by utilizing an <see cref="ActionBlock{TInput}"/>
    /// with a bounded capacity. In the event capacity is reached, the service will start failing to send emails and log a warning of the event, until capacity is regained.
    /// </remarks>
    public partial class EmailSenderService : IEmailSenderService, IAsyncDisposable
    {
        private const string INVALID_ADDRESS = "5.1.3 Invalid address";
        private const string SENDER_DENIED = "5.2.252 SendAsDenied";

        private SmtpConnectionSlot[] _connectionSlots = Array.Empty<SmtpConnectionSlot>();
        private ActionBlock<QueuedMail>? _emailScheduleWork;
        private readonly SemaphoreSlim _configurationLock = new(1, 1);
        private readonly SemaphoreSlim _oauthRefreshLock = new(1, 1);
        private readonly Func<EmailSenderOptions, IEmailSmtpClient> _smtpClientFactory;

        private readonly ILogger<EmailSenderService> _logger;
        private readonly IOptionsMonitor<EmailSenderOptions>? _mailOptions;
        private readonly IEmailSenderOptionsProvider? _mailOptionsProvider;
        private IDisposable? _optionsUpdateListener;

        private ParserOptions _cachedAddressParserOptions;
        private EmailSenderOptions? _runtimeOptions;
        private string? _runtimeConfigurationRevision;
        private int _failedMessagesCount;
        private bool _disposed;

        /// <summary>
        /// Creates an instance of <see cref="EmailSenderService"/>.
        /// </summary>
        /// <param name="mailOptions">Email sender options.</param>
        /// <param name="logger">Email sender logger.</param>
        public EmailSenderService(IOptionsMonitor<EmailSenderOptions> mailOptions, ILogger<EmailSenderService> logger)
            : this(mailOptions, logger, static options => new MailKitSmtpClientAdapter(options.ServerCertificateValidationCallback))
        {
        }

        /// <summary>
        /// Creates an instance of <see cref="EmailSenderService"/>.
        /// </summary>
        /// <param name="mailOptionsProvider">Email sender runtime options provider.</param>
        /// <param name="logger">Email sender logger.</param>
        public EmailSenderService(IEmailSenderOptionsProvider mailOptionsProvider, ILogger<EmailSenderService> logger)
            : this(mailOptionsProvider, logger, static options => new MailKitSmtpClientAdapter(options.ServerCertificateValidationCallback))
        {
        }

        internal EmailSenderService(
            IOptionsMonitor<EmailSenderOptions> mailOptions,
            ILogger<EmailSenderService> logger,
            Func<EmailSenderOptions, IEmailSmtpClient> smtpClientFactory)
        {
            _mailOptions = mailOptions;
            _logger = logger;
            _smtpClientFactory = smtpClientFactory;

            EmailSenderOptions options = mailOptions.CurrentValue;
            _runtimeOptions = options;
            InitializeInfrastructure(options);

            try
            {
                _optionsUpdateListener = mailOptions.OnChange(UpdateParserOptions);
                _cachedAddressParserOptions = CreateParserOptions(mailOptions.CurrentValue);
            }
            catch
            {
                _cachedAddressParserOptions = CreateParserOptions(new());
            }
        }

        internal EmailSenderService(
            IEmailSenderOptionsProvider mailOptionsProvider,
            ILogger<EmailSenderService> logger,
            Func<EmailSenderOptions, IEmailSmtpClient> smtpClientFactory)
        {
            _mailOptionsProvider = mailOptionsProvider;
            _logger = logger;
            _smtpClientFactory = smtpClientFactory;
            _connectionSlots = Array.Empty<SmtpConnectionSlot>();
            _cachedAddressParserOptions = CreateParserOptions(new());
        }

        /// <summary>
        /// Creates an instance of <see cref="EmailSenderService"/>.
        /// </summary>
        /// <param name="mailOptions">Email sender options.</param>
        /// <param name="configureLogger">
        /// An optional action to configure the <see cref="ILoggerFactory"/> used by the <see cref="EmailSenderService"/>.
        /// Leave <see langword="null"/> to effectively disable logging.
        /// </param>
        public EmailSenderService(EmailSenderOptions mailOptions, Action<ILoggingBuilder>? configureLogger = null)
            : this(TransformEmailSenderOptions(mailOptions), InternalLoggerFactory.CreateLogger<EmailSenderService>(configureLogger))
        {
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
            try
            {
                EmailSenderOptions? options = await EnsureCurrentConfigurationAsync(cancellationToken).ConfigureAwait(false);
                if (options is null)
                {
                    return new InvalidOperationException("Email sender is not configured.");
                }

                IEmailSmtpClient client = _smtpClientFactory(options);

                try
                {
                    bool refreshedAfterFailure = false;
                    while (true)
                    {
                        string? previousAccessToken = options.AccessToken;
                        DateTime? previousAccessTokenExpiresAtUtc = options.AccessTokenExpiresAtUtc;
                        SmtpClientConnectionResult result =
                            await TryToConnectAndAuthenticateSmtpClientAsync(options, client, null, cancellationToken).ConfigureAwait(false);
                        if (result.Successful)
                        {
                            return null;
                        }

                        if (result.ShouldRefreshOAuthToken
                            && !refreshedAfterFailure
                            && await TryRefreshOAuthTokenAndReconnectAsync(
                                options,
                                null,
                                client,
                                cancellationToken,
                                previousAccessToken,
                                previousAccessTokenExpiresAtUtc).ConfigureAwait(false))
                        {
                            refreshedAfterFailure = true;
                            continue;
                        }

                        return result.Exception ?? new InvalidOperationException("Could not connect to the SMTP server.");
                    }
                }
                finally
                {
                    await DisconnectSmtpClientAsync(client).ConfigureAwait(false);
                    client.Dispose();
                }
            }
            catch (Exception ex)
            {
                return ex;
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
                _optionsUpdateListener?.Dispose();

                ActionBlock<QueuedMail>? emailScheduleWork = _emailScheduleWork;
                if (emailScheduleWork is not null)
                {
                    emailScheduleWork.Complete();
                    await emailScheduleWork.Completion.ConfigureAwait(false);
                }

                for (int i = 0; i < _connectionSlots.Length; i++)
                {
                    SmtpConnectionSlot slot = _connectionSlots[i];
                    await DisconnectConnectionSlotAsync(slot).ConfigureAwait(false);
                    slot.Client.Dispose();
                }

                _configurationLock.Dispose();
                _oauthRefreshLock.Dispose();
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

        private async ValueTask<bool> InternalTryScheduleAsync(IEmailMessage email,
            bool awaitCompletion,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                email.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            EmailSenderOptions? mailOptions = await GetOptionsAsync(cancellationToken).ConfigureAwait(false);
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
                ActionBlock<QueuedMail>? emailScheduleWork = _emailScheduleWork;
                if (emailScheduleWork is null)
                {
                    _logger.LogError("Email message could not be processed because the sender runtime was not initialized.");
                    await OnEmailSendingFailureAsync(email, mailOptions, EmailFailureReason.Unknown).ConfigureAwait(false);
                    queuedMail.Dispose();
                    return false;
                }

                queued = await emailScheduleWork.SendAsync(queuedMail, cancellationToken).ConfigureAwait(false);
            }
            catch when (ExceptionFilters.DisposeWithoutUnwindingStack(queuedMail))
            {
                throw;
            }

            if (!queued)
            {
                _logger.LogError("Email message could not be processed. Service has stopped accepting new email messages.");

                await OnEmailSendingFailureAsync(email, mailOptions, EmailFailureReason.Unknown).ConfigureAwait(false);
                queuedMail.Dispose();

                return false;
            }

            _logger.LogInformation("Email to {Recipients} has been scheduled for sending.",
                string.Join(", ", mimeMessage.To.Cast<MailboxAddress>().Select(static adr => adr.Address)));

            return !awaitCompletion || await queuedMail.TaskCompletionSource!.Task.ConfigureAwait(false);
        }

        private MimeMessage? CreateMimeMessage(IEmailMessage email,
            EmailSenderOptions mailOptions,
            out bool treatAsSuccess)
        {
            treatAsSuccess = false;
            try
            {
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

                    foreach (IEmailAttachment attachment in email.Attachments)
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

                if (UsesAuthenticatedIdentity(mailOptions) && mailOptions.IsUsernameEmailAddress)
                {
                    // Defensive check. Under normal conditions should never be hit.
                    if (string.IsNullOrWhiteSpace(mailOptions.Username))
                    {
                        _logger.LogCritical("Malformed configuration! Username is required when authentication is enabled, but was missing.");
                        return null;
                    }

                    if (mailOptions.FromAddress is not null && mailOptions.FromAddress != mailOptions.Username)
                    {
                        if (!TryParseEmailAddress(mailOptions.FromAddress, "From", out MailboxAddress parsedFromAddress))
                        {
                            return null;
                        }

                        if (!TryParseEmailAddress(mailOptions.Username, "Sender", out MailboxAddress parsedSenderAddress))
                        {
                            return null;
                        }

                        fromAddress = parsedFromAddress;
                        senderAddress = parsedSenderAddress;
                    }
                    else if (!TryParseEmailAddress(mailOptions.Username, "From", out MailboxAddress parsedFromAddress))
                    {
                        return null;
                    }
                    else
                    {
                        fromAddress = parsedFromAddress;
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

                    if (!TryParseEmailAddress(mailOptions.FromAddress, "From", out MailboxAddress parsedFromAddress))
                    {
                        return null;
                    }

                    fromAddress = parsedFromAddress;
                }

                mail.From.Add(fromAddress);
                if (senderAddress is not null)
                {
                    mail.Sender = senderAddress;
                }

                foreach (string recipient in filteredRecipients)
                {
                    if (MailboxAddress.TryParse(_cachedAddressParserOptions, recipient, out MailboxAddress? emailAddress) && emailAddress is not null)
                    {
                        mail.To.Add(emailAddress);
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

                if (mailOptions.TreatEmptyRecipientsAsSuccess)
                {
                    _logger.LogInformation("An email with invalid recipient addresses was treated as successfully processed.");
                    treatAsSuccess = true;
                    return null;
                }

                _logger.LogWarning("Email will not be processed because all remaining recipients had invalid addresses.");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "There was a problem creating a MimeMessage for {Recipients}", string.Join(',', email.Recipients));
                return null;
            }
        }

        private async ValueTask<SmtpClientConnectionResult> TryToConnectAndAuthenticateSmtpClientAsync(
            EmailSenderOptions options,
            IEmailSmtpClient smtpClient,
            SmtpConnectionSlot? connectionSlot,
            CancellationToken cancellationToken)
        {
            try
            {
                if (connectionSlot is not null)
                {
                    smtpClient = await EnsureConnectionSlotRuntimeMatchesOptionsAsync(connectionSlot, options).ConfigureAwait(false);
                }

                if (options.AuthenticationType == EmailSenderAuthenticationType.OAuth2)
                {
                    await EnsureValidOAuthAccessTokenAsync(options, cancellationToken).ConfigureAwait(false);

                    if (connectionSlot is not null && connectionSlot.RequiresReconnect && smtpClient.IsConnected)
                    {
                        await DisconnectConnectionSlotAsync(connectionSlot).ConfigureAwait(false);
                    }
                }

                if (!smtpClient.IsConnected)
                {
                    await smtpClient.ConnectAsync(options.Host, options.Port, SecureSocketOptions.Auto, cancellationToken).ConfigureAwait(false);
                }

                if (options.AuthenticationType == EmailSenderAuthenticationType.Basic)
                {
                    if (options.RequiresAuthentication && !smtpClient.IsAuthenticated)
                    {
                        await smtpClient.AuthenticateAsync(options.Username ?? string.Empty, options.Password ?? string.Empty, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    if (!smtpClient.IsAuthenticated)
                    {
                        string accessToken = options.AccessToken ?? string.Empty;
                        SaslMechanismOAuth2 oauthMechanism = new(options.Username ?? string.Empty, accessToken);
                        await smtpClient.AuthenticateAsync(oauthMechanism, cancellationToken).ConfigureAwait(false);
                        if (connectionSlot is not null)
                        {
                            connectionSlot.RequiresReconnect = false;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (LogOperationCancelledWithoutUnwinding(_logger))
            {
                throw;
            }
            catch (Exception ex) when (options.AuthenticationType == EmailSenderAuthenticationType.OAuth2 && IsRefreshableAuthenticationException(ex))
            {
                _logger.LogWarning(ex, "OAuth2 authentication failed. A token refresh will be attempted.");
                return new(false, true, EmailFailureReason.AuthenticationFailed, ex);
            }
            catch (Exception ex) when (IsAuthenticationFailure(ex))
            {
                _logger.LogCritical(ex, "Authentication with the SMTP server failed.");
                return new(false, false, EmailFailureReason.AuthenticationFailed, ex);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Critical error in connecting to SMTP server.");
                return new(false, false, EmailFailureReason.Unknown, ex);
            }

            return new(true, false, EmailFailureReason.None, null);
        }

        private async Task<bool> ProcessMessageAsync(QueuedMail queuedMail)
        {
            try
            {
                bool wasDisposed = false;
                _logger.LogInformation("Message for {Recipients} is being processed...", string.Join(", ",
                    queuedMail.MimeMessage.To.Cast<MailboxAddress>().Select(static adr => adr.Address)));

                EmailSenderOptions? mailOptions = await GetOptionsAsync(queuedMail.CancellationToken).ConfigureAwait(false);
                if (mailOptions is null)
                {
                    Interlocked.Increment(ref _failedMessagesCount);
                    return false;
                }

                SmtpConnectionSlot connectionSlot = GetConnection();
                try
                {
                    try
                    {
                        IEmailSmtpClient smtpClient = connectionSlot.Client;
                        if (connectionSlot.RequiresClientReinitialization)
                        {
                            smtpClient = await EnsureConnectionSlotRuntimeMatchesOptionsAsync(connectionSlot, mailOptions).ConfigureAwait(false);
                        }

                        if (smtpClient.IsConnected)
                        {
                            await smtpClient.NoOpAsync(queuedMail.CancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        _logger.LogError("Could not send message to SMTP server - object was disposed.");
                        wasDisposed = true;
                    }
                    catch (OperationCanceledException) when (LogOperationCancelledWithoutUnwinding(_logger))
                    {
                        throw;
                    }
                    catch
                    {
                        _logger.LogInformation("Failed to keep the underlying connection alive. Will re-connect and re-authenticate where appropriate.");
                    }

                    return wasDisposed
                        ? await OnEmailSendingFailureAsync(queuedMail.Message, mailOptions, EmailFailureReason.Disposed).ConfigureAwait(false)
                        : await SendMessageAsync(connectionSlot, queuedMail, mailOptions).ConfigureAwait(false);
                }
                finally
                {
                    ReleaseConnection(connectionSlot);
                }
            }
            finally
            {
                queuedMail.Dispose();
            }
        }

        private bool TryParseEmailAddress(string address, string headerType, out MailboxAddress parsedAddress)
        {
            if (!MailboxAddress.TryParse(_cachedAddressParserOptions, address, out MailboxAddress? candidateAddress)
                || candidateAddress is null)
            {
                parsedAddress = null!;
                _logger.LogCritical("Failed to parse {Address} as an email address for the \"{HeaderType}\" header.", address, headerType);
                return false;
            }

            parsedAddress = candidateAddress;
            return true;
        }

        private SmtpConnectionSlot GetConnection()
        {
            // Find the closest or "hottest" available connection.
            for (int i = 0; i < _connectionSlots.Length; i++)
            {
                SmtpConnectionSlot slot = _connectionSlots[i];
                // Try to acquire the connection by marking it as in-use atomically.
                if (Interlocked.CompareExchange(ref slot.InUse, 1, 0) == 0)
                {
                    return slot;
                }
            }

            // No connections available.
            Debug.Fail("This should never happen under ActionBlock constraints.");

            _logger.LogCritical("No SMTP connections available");
            throw new InvalidOperationException("No SMTP connections available.");
        }

        private void ReleaseConnection(SmtpConnectionSlot slot)
        {
            // Simply mark as available
            Interlocked.Exchange(ref slot.InUse, 0);
        }

        private async ValueTask<EmailSenderOptions?> GetOptionsAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await EnsureCurrentConfigurationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Could not fetch mail options.");
                return null;
            }
        }

        private ValueTask<EmailSenderOptions?> EnsureCurrentConfigurationAsync(CancellationToken cancellationToken)
        {
            if (_mailOptionsProvider is null)
            {
                return new ValueTask<EmailSenderOptions?>(_mailOptions!.CurrentValue);
            }

            return EnsureDynamicConfigurationAsync(cancellationToken);
        }

        private async ValueTask<EmailSenderOptions?> EnsureDynamicConfigurationAsync(CancellationToken cancellationToken)
        {
            await _configurationLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                EmailSenderOptionsSnapshot snapshot = await _mailOptionsProvider!.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
                EmailSenderOptions? snapshotOptions = snapshot.Options;
                if (snapshotOptions is null)
                {
                    await DeactivateDynamicRuntimeAsync(snapshot.ConfigurationRevision).ConfigureAwait(false);
                    return null;
                }

                if (string.IsNullOrWhiteSpace(snapshot.ConfigurationRevision))
                {
                    _logger.LogError("Dynamic email sender configuration is missing a structural configuration revision.");
                    await DeactivateDynamicRuntimeAsync(snapshot.ConfigurationRevision).ConfigureAwait(false);
                    return null;
                }

                if (_runtimeOptions is not null
                    && string.Equals(_runtimeConfigurationRevision, snapshot.ConfigurationRevision, StringComparison.Ordinal))
                {
                    return _runtimeOptions;
                }

                try
                {
                    ObjectValidator.ValidateObjectOrThrow(snapshotOptions);
                }
                catch (System.ComponentModel.DataAnnotations.ValidationException ex)
                {
                    _logger.LogError(ex, "Dynamic email sender configuration is invalid.");
                    await DeactivateDynamicRuntimeAsync(snapshot.ConfigurationRevision).ConfigureAwait(false);
                    return null;
                }

                if (_emailScheduleWork is null)
                {
                    InitializeInfrastructure(snapshotOptions);
                }

                ApplyDynamicRuntime(snapshotOptions, snapshot.ConfigurationRevision);
                return _runtimeOptions;
            }
            finally
            {
                _configurationLock.Release();
            }
        }

        private void InitializeInfrastructure(EmailSenderOptions options)
        {
            if (_emailScheduleWork is not null && _connectionSlots.Length != 0)
            {
                return;
            }

            int connectionCount = Math.Max(options.MaxConcurrentConnections, 1);
            _emailScheduleWork = new ActionBlock<QueuedMail>(ProcessMessageAsync, new ExecutionDataflowBlockOptions
            {
                EnsureOrdered = false,
                MaxDegreeOfParallelism = connectionCount,
                SingleProducerConstrained = false,
                TaskScheduler = TaskScheduler.Default,
                BoundedCapacity = options.MessageQueueSize
            });

            SmtpConnectionSlot[] connectionSlots = new SmtpConnectionSlot[connectionCount];
            for (int i = 0; i < connectionSlots.Length; i++)
            {
                connectionSlots[i] = new SmtpConnectionSlot
                {
                    Client = _smtpClientFactory(options)
                };
            }

            _connectionSlots = connectionSlots;
        }

        private void ApplyDynamicRuntime(EmailSenderOptions options, string configurationRevision)
        {
            bool requiresClientReinitialization = _runtimeOptions is not null;

            _runtimeOptions = options;
            _runtimeConfigurationRevision = configurationRevision;
            _cachedAddressParserOptions = CreateParserOptions(options);

            if (_connectionSlots.Length == 0)
            {
                return;
            }

            if (!requiresClientReinitialization)
            {
                return;
            }

            foreach (SmtpConnectionSlot connectionSlot in _connectionSlots)
            {
                connectionSlot.RequiresClientReinitialization = true;
                connectionSlot.RequiresReconnect = false;
            }
        }

        private async ValueTask DeactivateDynamicRuntimeAsync(string? configurationRevision)
        {
            _runtimeOptions = null;
            _runtimeConfigurationRevision = configurationRevision;
            _cachedAddressParserOptions = CreateParserOptions(new());

            foreach (SmtpConnectionSlot connectionSlot in _connectionSlots)
            {
                connectionSlot.RequiresClientReinitialization = true;
                connectionSlot.RequiresReconnect = false;

                if (Volatile.Read(ref connectionSlot.InUse) == 0)
                {
                    await DisconnectConnectionSlotAsync(connectionSlot).ConfigureAwait(false);
                }
            }
        }

        private async ValueTask<IEmailSmtpClient> EnsureConnectionSlotRuntimeMatchesOptionsAsync(
            SmtpConnectionSlot connectionSlot,
            EmailSenderOptions options)
        {
            if (!connectionSlot.RequiresClientReinitialization)
            {
                return connectionSlot.Client;
            }

            IEmailSmtpClient replacementClient = _smtpClientFactory(options);
            IEmailSmtpClient previousClient = connectionSlot.Client;

            await DisconnectSmtpClientAsync(previousClient).ConfigureAwait(false);
            connectionSlot.Client = replacementClient;
            connectionSlot.RequiresClientReinitialization = false;
            connectionSlot.RequiresReconnect = false;
            previousClient.Dispose();

            return replacementClient;
        }

        private async Task<bool> SendMessageAsync(SmtpConnectionSlot connectionSlot, QueuedMail queuedMail, EmailSenderOptions mailOptions)
        {
            EmailFailureReason failureReason = EmailFailureReason.Unknown;
            CancellationToken cancellationToken = queuedMail.CancellationToken;
            bool refreshedAfterFailure = false;

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
                    IEmailSmtpClient smtpClient = connectionSlot.Client;
                    SmtpClientConnectionResult connectionResult =
                        await TryToConnectAndAuthenticateSmtpClientAsync(mailOptions, smtpClient, connectionSlot, cancellationToken).ConfigureAwait(false);
                    if (connectionResult.Successful)
                    {
                        smtpClient = connectionSlot.Client;
                        SmtpSendResult sendResult =
                            await TrySendingSmtpClientMailMessageAsync(smtpClient, queuedMail.MimeMessage, mailOptions, cancellationToken).ConfigureAwait(false);

                        if (sendResult.Successful)
                        {
                            queuedMail.Delivered();
                            return true;
                        }

                        failureReason = sendResult.FailureReason;

                        if (sendResult.ShouldRefreshOAuthToken
                            && !refreshedAfterFailure
                            && await TryRefreshOAuthTokenAndReconnectAsync(mailOptions, connectionSlot, null, cancellationToken).ConfigureAwait(false))
                        {
                            refreshedAfterFailure = true;
                            continue;
                        }

                        if (sendResult.FailFast)
                        {
                            break;
                        }
                    }
                    else
                    {
                        failureReason = connectionResult.FailureReason;

                        if (connectionResult.ShouldRefreshOAuthToken
                            && !refreshedAfterFailure
                            && await TryRefreshOAuthTokenAndReconnectAsync(mailOptions, connectionSlot, null, cancellationToken).ConfigureAwait(false))
                        {
                            refreshedAfterFailure = true;
                            continue;
                        }
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
            IEmailSmtpClient smtpClient,
            MimeMessage mail,
            EmailSenderOptions options,
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
                _logger.LogError("Could not send message to SMTP server - object was disposed.");
                return new(false, true, false, EmailFailureReason.Disposed);
            }
            catch (OperationCanceledException) when (LogOperationCancelledWithoutUnwinding(_logger))
            {
                // Honor typical async patterns, by keeping cancellation as an exception.
                throw;
            }
            catch (SmtpCommandException ex) when (ex.Message == INVALID_ADDRESS)
            {
                _logger.LogError("Could not send message to SMTP server due to an invalid address - mail will be dropped.");
                return new(false, true, false, EmailFailureReason.InvalidAddress);
            }
            catch (SmtpCommandException ex) when (ex.Message.StartsWith(SENDER_DENIED, StringComparison.Ordinal))
            {
                _logger.LogCritical("Could not send message to SMTP server as {FromAddress}. " +
                    "Make sure your account has the necessary permissions and that you're using the correct address to send emails from.",
                    ((MailboxAddress)mail.From[0]).Address);
                return new(false, true, false, EmailFailureReason.SendAsDenied);
            }
            catch (SmtpCommandException ex) when (options.AuthenticationType == EmailSenderAuthenticationType.OAuth2 && ex.StatusCode == SmtpStatusCode.AuthenticationRequired)
            {
                _logger.LogWarning(ex, "SMTP server requested re-authentication. A token refresh will be attempted.");
                return new(false, false, true, EmailFailureReason.AuthenticationFailed);
            }
            catch (ServiceNotAuthenticatedException ex) when (options.AuthenticationType == EmailSenderAuthenticationType.OAuth2)
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
            catch (IOException ex) when (ex.InnerException is not null)
            {
                _logger.LogError(ex.InnerException, "Could not send message to SMTP server.");
                return new(false, false, false, EmailFailureReason.Unknown);
            }
            catch (ServiceNotAuthenticatedException)
            {
                // This catch block should never be hit. It's just here as a defensive-coding practice in the event
                // in the future we overlook the Debug.Assert statement.
                _logger.LogCritical("Attempted to send message to SMTP server when no connection was established with it.");
                return new(false, false, false, EmailFailureReason.NotConnected);
            }
            catch (ServiceNotConnectedException)
            {
                // This catch block should never be hit. It's just here as a defensive-coding practice in the event
                // in the future we overlook the Debug.Assert statement.
                _logger.LogCritical("Attempted to send message to SMTP server when no connection was established with it.");
                return new(false, false, false, EmailFailureReason.NotConnected);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not send message to SMTP server.");
                return new(false, false, false, EmailFailureReason.Unknown);
            }
        }

        private async ValueTask EnsureValidOAuthAccessTokenAsync(EmailSenderOptions options, CancellationToken cancellationToken)
        {
            if (options.AuthenticationType != EmailSenderAuthenticationType.OAuth2)
            {
                return;
            }

            if (HasUsableOAuthAccessToken(options))
            {
                return;
            }

            bool refreshed = await TryRefreshOAuthTokenAsync(options, forceRefresh: false, cancellationToken).ConfigureAwait(false);
            if (!refreshed || !HasUsableOAuthAccessToken(options))
            {
                throw new InvalidOperationException("OAuth2 access token is missing, expired, or could not be refreshed.");
            }
        }

        private async ValueTask<bool> TryRefreshOAuthTokenAndReconnectAsync(
            EmailSenderOptions options,
            SmtpConnectionSlot? connectionSlot,
            IEmailSmtpClient? temporaryClient,
            CancellationToken cancellationToken,
            string? previousAccessToken = null,
            DateTime? previousAccessTokenExpiresAtUtc = null)
        {
            if (options.AuthenticationType != EmailSenderAuthenticationType.OAuth2)
            {
                return false;
            }

            bool refreshed = await TryRefreshOAuthTokenAsync(
                options,
                forceRefresh: true,
                cancellationToken,
                connectionSlot,
                previousAccessToken,
                previousAccessTokenExpiresAtUtc).ConfigureAwait(false);
            if (!refreshed)
            {
                return false;
            }

            if (temporaryClient is not null)
            {
                await DisconnectSmtpClientAsync(temporaryClient).ConfigureAwait(false);
            }

            return true;
        }

        private async ValueTask<bool> TryRefreshOAuthTokenAsync(
            EmailSenderOptions options,
            bool forceRefresh,
            CancellationToken cancellationToken,
            SmtpConnectionSlot? connectionSlot = null,
            string? previousAccessToken = null,
            DateTime? previousAccessTokenExpiresAtUtc = null)
        {
            if (options.AuthenticationType != EmailSenderAuthenticationType.OAuth2)
            {
                return false;
            }

            Func<CancellationToken, ValueTask<EmailSenderOAuthRefreshResult>>? refreshAccessTokenAsync = options.RefreshAccessTokenAsync;
            if (refreshAccessTokenAsync is null)
            {
                return false;
            }

            bool lockTaken = false;

            try
            {
                await _oauthRefreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                lockTaken = true;

                if (!forceRefresh && HasUsableOAuthAccessToken(options))
                {
                    return true;
                }

                if (forceRefresh)
                {
                    if (connectionSlot is not null && connectionSlot.RequiresReconnect)
                    {
                        return true;
                    }

                    if (connectionSlot is null
                        && previousAccessTokenExpiresAtUtc.HasValue
                        && HaveOAuthOptionValuesChanged(options, previousAccessToken, previousAccessTokenExpiresAtUtc.Value))
                    {
                        return true;
                    }
                }

                EmailSenderOAuthRefreshResult refreshedToken = await refreshAccessTokenAsync(cancellationToken).ConfigureAwait(false);
                ValidateRefreshedOAuthRefreshResult(refreshedToken);

                options.AccessToken = refreshedToken.AccessToken;
                options.AccessTokenExpiresAtUtc = refreshedToken.AccessTokenExpiresAtUtc;
                MarkOAuthConnectionSlotsForReconnect();

                _logger.LogInformation("OAuth2 access token was refreshed.");
                return true;
            }
            catch (OperationCanceledException) when (LogOperationCancelledWithoutUnwinding(_logger))
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not refresh OAuth2 access token.");
                return false;
            }
            finally
            {
                if (lockTaken)
                {
                    _oauthRefreshLock.Release();
                }
            }
        }

        private async ValueTask DisconnectSmtpClientAsync(IEmailSmtpClient smtpClient)
        {
            try
            {
                if (smtpClient.IsConnected)
                {
                    // Do not pass the cancellation token. We want to disconnect gracefully.
#pragma warning disable CA2016 // Forward the 'CancellationToken' parameter to methods
                    await smtpClient.DisconnectAsync(true).ConfigureAwait(false);
#pragma warning restore CA2016 // Forward the 'CancellationToken' parameter to methods
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to disconnect SMTP client cleanly.");
            }
        }

        private async ValueTask DisconnectConnectionSlotAsync(SmtpConnectionSlot connectionSlot)
        {
            await DisconnectSmtpClientAsync(connectionSlot.Client).ConfigureAwait(false);
            if (!connectionSlot.Client.IsConnected)
            {
                connectionSlot.RequiresReconnect = false;
            }
        }

        private void MarkOAuthConnectionSlotsForReconnect()
        {
            foreach (SmtpConnectionSlot connectionSlot in _connectionSlots)
            {
                connectionSlot.RequiresReconnect = true;
            }
        }

        private static bool HasUsableOAuthAccessToken(EmailSenderOptions options) =>
            !string.IsNullOrWhiteSpace(options.AccessToken)
            && options.AccessTokenExpiresAtUtc > DateTime.UtcNow.AddMinutes(1);

        private static bool HaveOAuthOptionValuesChanged(
            EmailSenderOptions options,
            string? previousAccessToken,
            DateTime previousAccessTokenExpiresAtUtc) =>
            !string.Equals(options.AccessToken, previousAccessToken, StringComparison.Ordinal)
            || options.AccessTokenExpiresAtUtc != previousAccessTokenExpiresAtUtc;

        private static bool UsesAuthenticatedIdentity(EmailSenderOptions options) =>
            options.AuthenticationType == EmailSenderAuthenticationType.OAuth2
            || (options.AuthenticationType == EmailSenderAuthenticationType.Basic && options.RequiresAuthentication);

        private static bool IsRefreshableAuthenticationException(Exception ex) =>
            ex is MailKit.Security.AuthenticationException or SmtpCommandException;

        private static bool IsAuthenticationFailure(Exception ex) =>
            ex is MailKit.Security.AuthenticationException or SmtpCommandException;

        private static void ValidateRefreshedOAuthRefreshResult(EmailSenderOAuthRefreshResult refreshedToken)
        {
            ArgumentNullException.ThrowIfNull(refreshedToken);

            if (string.IsNullOrWhiteSpace(refreshedToken.AccessToken))
            {
                throw new InvalidOperationException("RefreshAccessTokenAsync returned an empty access token.");
            }

            if (refreshedToken.AccessTokenExpiresAtUtc == default)
            {
                throw new InvalidOperationException("RefreshAccessTokenAsync returned an invalid access token expiration time.");
            }

            if (refreshedToken.AccessTokenExpiresAtUtc <= DateTime.UtcNow)
            {
                throw new InvalidOperationException("RefreshAccessTokenAsync returned an already expired access token.");
            }
        }

        private void UpdateParserOptions(EmailSenderOptions options, string? optionsName)
        {
            _cachedAddressParserOptions = CreateParserOptions(options);
        }

        private static ParserOptions CreateParserOptions(EmailSenderOptions options) => new()
        {
            AddressParserComplianceMode = options.UseStrictAddressParser ? RfcComplianceMode.Strict : RfcComplianceMode.Loose,
            AllowAddressesWithoutDomain = options.AllowAddressesWithoutDomain,
            AllowUnquotedCommasInAddresses = options.AllowUnquotedCommasInAddresses
        };

        private static bool LogOperationCancelledWithoutUnwinding(ILogger<EmailSenderService> logger)
        {
            logger.LogInformation("Could not send message to SMTP server - operation was canceled.");
            return false;
        }

        private static IOptionsMonitor<EmailSenderOptions> TransformEmailSenderOptions(EmailSenderOptions options)
        {
            ObjectValidator.ValidateObjectOrThrow(options);
            return StaticOptionsMonitor.Create(options);
        }

        [GeneratedRegex(@"(\+|\.|\-)[0-9]+\@")]
        private static partial Regex MailRoutingRegex();

        private readonly record struct SmtpClientConnectionResult(
            bool Successful,
            bool ShouldRefreshOAuthToken,
            EmailFailureReason FailureReason,
            Exception? Exception);

        private readonly record struct SmtpSendResult(
            bool Successful,
            bool FailFast,
            bool ShouldRefreshOAuthToken,
            EmailFailureReason FailureReason);

        #endregion
    }
}
