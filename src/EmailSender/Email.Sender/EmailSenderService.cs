using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Utils;
using Polly.Contrib.WaitAndRetry;
using ReconArt.Email.Sender.Internal;
using ReconArt.Synchronization;
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

        private readonly SmtpClient _smtpClient = new();
        private readonly ILogger<EmailSenderService> _logger;
        private readonly IOptionsMonitor<EmailSenderOptions> _mailOptions;
        private readonly UnitDistributorSlim _workDistributor = new(1);
        private readonly ActionBlock<(MimeMessage, IEmailMessage)> _emailScheduleWork;
        private readonly IDisposable? _optionsUpdateListener; 

        private ParserOptions _cachedAddressParserOptions;
        private int _failedMessagesCount = 0;
        private bool _disposed;

        /// <summary>
        /// Creates an instance of <see cref="EmailSenderService"/>.
        /// </summary>
        /// <param name="mailOptions">Email sender options.</param>
        /// <param name="logger">Email sender logger.</param>
        public EmailSenderService(IOptionsMonitor<EmailSenderOptions> mailOptions, ILogger<EmailSenderService> logger)
        {
            _mailOptions = mailOptions;
            _logger = logger;
            _smtpClient.ServerCertificateValidationCallback = (s, c, h, e) => true;
            _emailScheduleWork = new ActionBlock<(MimeMessage, IEmailMessage)>(ProcessScheduledMessageAsync, new ExecutionDataflowBlockOptions
            {
                // DO NOT CHANGE THESE! Settings below are needed to achieve thread-safety and order.
                EnsureOrdered = true,
                MaxDegreeOfParallelism = 1,
                SingleProducerConstrained = false,
                TaskScheduler = TaskScheduler.Default,
                BoundedCapacity = 10_000
            });

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

        /// <summary>
        /// Creates an instance of <see cref="EmailSenderService"/>.
        /// </summary>
        /// <param name="mailOptions">Email sender options.</param>
        /// <param name="configureLogger">
        /// An optional action to configure the <see cref="ILoggerFactory"/> used by the <see cref="EmailSenderService"/>.
        /// Leave <see langword="null"/> to effectively disable logging.
        /// </param>
        public EmailSenderService(EmailSenderOptions mailOptions, Action<ILoggingBuilder>? configureLogger = null)
            : this(TransformEmailSenderOptions(mailOptions), Helpers.CreateLogger<EmailSenderService>(configureLogger))
        {
        }

        #region Public_Methods

        /// <inheritdoc/>
        public ValueTask<bool> TrySendAsync(IEmailMessage email, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                email.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            EmailSenderOptions? mailOptions = GetOptions();
            if (mailOptions is null)
            {
                Interlocked.Increment(ref _failedMessagesCount);
                email.Dispose();
                return new ValueTask<bool>(false);
            }

            MimeMessage? mail = CreateMimeMessage(email, mailOptions, out bool treatAsSuccess);
            return mail is null
                ? HandleMimeMessageResponseAsync(email, mailOptions, treatAsSuccess)
                : ProcessSendAsync(mail, email, cancellationToken);
        }

        /// <inheritdoc/>
        public ValueTask<bool> TryScheduleAsync(IEmailMessage email, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                email.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            EmailSenderOptions? mailOptions = GetOptions();
            if (mailOptions is null)
            {
                Interlocked.Increment(ref _failedMessagesCount);
                email.Dispose();
                return new ValueTask<bool>(false);
            }

            MimeMessage? mail = CreateMimeMessage(email, mailOptions, out bool treatAsSuccess);
            if (mail is null)
            {
                return HandleMimeMessageResponseAsync(email, mailOptions, treatAsSuccess);
            }

            Task<bool> sendTask = _emailScheduleWork.SendAsync((mail, email), cancellationToken);
            if (sendTask.IsCompleted)
            {
                if (sendTask.IsCanceled)
                {
                    email.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                else if (!sendTask.Result)
                {
                    _logger.LogError("Email message could not be processed. Either we have hit the limit of the queue, or it has stopped accepting new email messages.");
                    return OnEmailSendingFailureAsync(email, mailOptions, EmailFailureReason.Unknown);
                }
            }

            _logger.LogInformation("Email to {Recipients} has been scheduled for sending.", 
                string.Join(", ", string.Join(", ",
                    mail.To.Cast<MailboxAddress>().Select(static adr => adr.Address))));

            return new ValueTask<bool>(true);
        }

        /// <inheritdoc/>
        public async ValueTask<Exception?> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            SmtpClient client = new()
            {
                ServerCertificateValidationCallback = static (s, c, h, e) => true
            };
            try
            {
                EmailSenderOptions options = GetOptionsUnsafe();

                await client.ConnectAsync(options.Host, options.Port, SecureSocketOptions.StartTls, cancellationToken)
                    .ConfigureAwait(false);

                if (options.RequiresAuthentication)
                {
                    await client.AuthenticateAsync(options.Username ?? string.Empty, options.Password ?? string.Empty, cancellationToken)
                        .ConfigureAwait(false);
                }

                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
            finally
            {
                // Do not pass the cancellation token. We want to disconnect gracefully.
#pragma warning disable CA2016 // Forward the 'CancellationToken' parameter to methods
                await client.DisconnectAsync(true).ConfigureAwait(false);
#pragma warning restore CA2016 // Forward the 'CancellationToken' parameter to methods
                client.Dispose();
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

                _emailScheduleWork.Complete();
                await _emailScheduleWork.Completion.ConfigureAwait(false);

                await _smtpClient.DisconnectAsync(true).ConfigureAwait(false);
                _smtpClient.Dispose();
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

        private ValueTask<bool> HandleMimeMessageResponseAsync(IEmailMessage email, EmailSenderOptions mailOptions, bool treatAsSuccess)
        {
            if (!treatAsSuccess && mailOptions.SignalFailureOnInvalidParameters)
            {
                return OnEmailSendingFailureAsync(email, mailOptions, EmailFailureReason.InvalidParameters);
            }

            email.Dispose();
            return new ValueTask<bool>(treatAsSuccess);
        }

        private MimeMessage? CreateMimeMessage(IEmailMessage email, EmailSenderOptions mailOptions, out bool treatAsSuccess)
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
                if (mailOptions.RequiresAuthentication)
                {
                    if (mailOptions.IsUsernameEmailAddress)
                    {
                        if (mailOptions.FromAddress is not null && mailOptions.FromAddress != mailOptions.Username)
                        {
                            if (!MailboxAddress.TryParse(_cachedAddressParserOptions, mailOptions.FromAddress, out fromAddress))
                            {
                                _logger.LogCritical("Failed to parse {FromAddress} as an email address for the \"From\" header.", mailOptions.FromAddress);
                                return null;
                            }

                            if (!MailboxAddress.TryParse(_cachedAddressParserOptions, mailOptions.Username, out senderAddress))
                            {
                                _logger.LogCritical("Failed to parse {Username} as an email address for the \"Sender\" header.", mailOptions.Username);
                                return null;
                            }
                        }
                        else if (!MailboxAddress.TryParse(_cachedAddressParserOptions, mailOptions.Username, out fromAddress))
                        {
                            _logger.LogCritical("Failed to parse {Username} as an email address for the \"From\" header.", mailOptions.Username);
                            return null;
                        }
                    }
                    else if (!MailboxAddress.TryParse(_cachedAddressParserOptions, mailOptions.FromAddress, out fromAddress))
                    {
                        _logger.LogCritical("Failed to parse {FromAddress} as an email address for the \"From\" header.", mailOptions.FromAddress);
                        return null;
                    }
                }
                else if (!MailboxAddress.TryParse(_cachedAddressParserOptions, mailOptions.FromAddress, out fromAddress))
                {
                    _logger.LogCritical("Failed to parse {FromAddress} as an email address for the \"From\" header.", mailOptions.FromAddress);
                    return null;
                }

                mail.From.Add(fromAddress);
                if (senderAddress is not null)
                {
                    mail.Sender = senderAddress;
                }

                foreach (string recipient in filteredRecipients)
                {
                    if (MailboxAddress.TryParse(_cachedAddressParserOptions, recipient, out MailboxAddress emailAdress))
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

        private async ValueTask<bool> TryToConnectAndAuthenticateSmtpClientAsync(EmailSenderOptions options, CancellationToken cancellationToken)
        {
            try
            {
                if (!_smtpClient.IsConnected)
                {
                    await _smtpClient.ConnectAsync(options.Host, options.Port, SecureSocketOptions.StartTls, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }

                if (options.RequiresAuthentication && !_smtpClient.IsAuthenticated)
                {
                    await _smtpClient.AuthenticateAsync(options.Username ?? string.Empty, options.Password ?? string.Empty, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (LogOperationCancelledWithoutUnwinding(_logger))
            {
                // Honor typical async patterns, by keeping cancellation as an exception.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Critical error in connecting to SMTP server.");
                return false;
            }

            return true;
        }

        private async Task ProcessScheduledMessageAsync((MimeMessage MimeMessage, IEmailMessage MailMessage) context)
        {
            await _workDistributor.TryTakeAsync(1, 1).ConfigureAwait(false);
            try
            {
                await ProcessMessageAsync(context.MimeMessage, context.MailMessage, default)
                    .ConfigureAwait(false);
            }
            finally
            {
                _workDistributor.Return(1);
            }
        }

        private async ValueTask<bool> ProcessSendAsync(MimeMessage mimeMessage, IEmailMessage mailMessage, CancellationToken cancellationToken)
        {
            await _workDistributor.TryTakeAsync(1, 0, cancellationToken).ConfigureAwait(false);
            try
            {
                return await ProcessMessageAsync(mimeMessage, mailMessage, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _workDistributor.Return(1);
            }
        }

        private async Task<bool> ProcessMessageAsync(MimeMessage mimeMessage, IEmailMessage mailMessage, CancellationToken cancellationToken)
        {
            bool wasDisposed = false;
            EmailSenderOptions? mailOptions;
            _logger.LogInformation("Message for {Recipients} is being processed...", string.Join(", ",
                    mimeMessage.To.Cast<MailboxAddress>().Select(static adr => adr.Address)));
            mailOptions = GetOptions();
            if (mailOptions is null)
            {
                Interlocked.Increment(ref _failedMessagesCount);
                mailMessage.Dispose();
                return false;
            }

            try
            {
                if (_smtpClient.IsConnected)
                {
                    await _smtpClient.NoOpAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (ObjectDisposedException)
            {
                _logger.LogError("Could not send message to SMTP server - object was disposed.");
                wasDisposed = true;
            }
            catch (OperationCanceledException) when (ExceptionFilters.DisposeWithoutUnwindingStack(mailMessage) & LogOperationCancelledWithoutUnwinding(_logger))
            {
                // Honor typical async patterns, by keeping cancellation as an exception.
                throw;
            }
            catch
            {
                _logger.LogInformation("Failed to keep the underlying connection alive. Will re-connect and re-authenticate where appropriate.");
            }

            return wasDisposed
                ? await OnEmailSendingFailureAsync(mailMessage, mailOptions, EmailFailureReason.Disposed).ConfigureAwait(false)
                : await SendMessageAsync(mimeMessage, mailMessage, mailOptions, cancellationToken).ConfigureAwait(false);
        }

        private EmailSenderOptions? GetOptions()
        {
            try
            {
                return GetOptionsUnsafe();
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Could not fetch mail options.");
                return null;
            }
        }

        private EmailSenderOptions GetOptionsUnsafe() => _mailOptions.CurrentValue;

        private async Task<bool> SendMessageAsync(
            MimeMessage mimeMessage,
            IEmailMessage mailMessage,
            EmailSenderOptions mailOptions,
            CancellationToken cancellationToken)
        {
            EmailFailureReason failureReason = EmailFailureReason.Unknown;

            // To add some resiliency, we'll attempt to send the message a couple of times before giving up.
            int retryCount = (int)mailOptions.RetryCount;
            IEnumerator<TimeSpan> delaysEnumerator = mailOptions.RetryDelayInMilliseconds == 0 || retryCount == 0
                ? Enumerable.Empty<TimeSpan>().GetEnumerator() 
                : Backoff.DecorrelatedJitterBackoffV2(
                    medianFirstRetryDelay: TimeSpan.FromMilliseconds((int)mailOptions.RetryDelayInMilliseconds),
                    retryCount: retryCount).GetEnumerator();
            try
            {
                do
                {
                    if (await TryToConnectAndAuthenticateSmtpClientAsync(mailOptions, cancellationToken).ConfigureAwait(false))
                    {
                        (bool Successful, bool FailFast, EmailFailureReason FailureReason) result = 
                            await TrySendingSmtpClientMailMessageAsync(mimeMessage, cancellationToken).ConfigureAwait(false);

                        if (result.Successful)
                        {
                            mailMessage.Dispose();
                            return true;
                        }

                        failureReason = result.FailureReason;

                        if (result.FailFast)
                        {
                            break;
                        }
                    }

                    if (!delaysEnumerator.MoveNext())
                    {
                        _logger.LogError("Could not send message to SMTP server - all retries failed.");
                        break;
                    }

                    TimeSpan retryDelay = delaysEnumerator.Current;

                    _logger.LogInformation("Retrying in {retryMailDelay}ms to send a message.", retryDelay.Milliseconds);

                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                } while (true);
            }
            catch (OperationCanceledException) when (ExceptionFilters.DisposeWithoutUnwindingStack(mailMessage))
            {
                // We're only disposing as internal methods should have already logged the exception.
                throw;
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
            return await OnEmailSendingFailureAsync(mailMessage, mailOptions, failureReason).ConfigureAwait(false);
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

            mailMessage.Dispose();
            return false;
        }

        private async ValueTask<(bool Successful, bool FailFast, EmailFailureReason)> TrySendingSmtpClientMailMessageAsync(MimeMessage mail, CancellationToken cancellationToken)
        {
            Debug.Assert(_smtpClient.IsConnected,
                "Method should only be called after attempting to establish a connection with the SMTP server.");

            try
            {
                await _smtpClient.SendAsync(mail, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Successfully sent message to {Recipients}.", string.Join(", ",
                    mail.To.Cast<MailboxAddress>().Select(static adr => adr.Address)));
                return (true, false, EmailFailureReason.None);
            }
            catch (ObjectDisposedException)
            {
                _logger.LogError("Could not send message to SMTP server - object was disposed.");
                return (false, true, EmailFailureReason.Disposed);
            }
            catch (OperationCanceledException) when (LogOperationCancelledWithoutUnwinding(_logger))
            {
                // Honor typical async patterns, by keeping cancellation as an exception.
                throw;
            }
            catch (SmtpProtocolException ex)
            {
                if (!_smtpClient.IsConnected)
                {
                    _logger.LogWarning("Could not send message to the SMTP server - the connection to the server was broken.");
                }
                else
                {
                    _logger.LogError(ex, "Could not send message to SMTP server.");
                }

                return (false, false, EmailFailureReason.Unknown);
            }
            catch (IOException ex) when (ex.InnerException is not null)
            {
                _logger.LogError(ex.InnerException, "Could not send message to SMTP server.");
                return (false, false, EmailFailureReason.Unknown);
            }
            catch (SmtpCommandException ex) when (ex.Message == INVALID_ADDRESS)
            {
                _logger.LogError("Could not send message to SMTP server due to an invalid address - mail will be dropped.");
                return (false, true, EmailFailureReason.InvalidAddress);
            }
            catch (SmtpCommandException ex) when (ex.Message.StartsWith(SENDER_DENIED))
            {
                _logger.LogCritical("Could not send message to SMTP server as {FromAddress}. " +
                    "Make sure your account has the necessary permissions and that you're using the correct address to send emails from.",
                    ((MailboxAddress)mail.From[0]).Address);
                return (false, true, EmailFailureReason.SendAsDenied);
            }
            catch (ServiceNotAuthenticatedException)
            {
                // This catch block should never be hit. It's just here as a defensive-coding practice in the event
                // in the future we overlook the Debug.Assert statement.
                _logger.LogCritical("Attempted to send message to SMTP server when no connection was established with it.");
                return (false, false, EmailFailureReason.NotConnected);
            }
            catch (ServiceNotConnectedException)
            {
                // This catch block should never be hit. It's just here as a defensive-coding practice in the event
                // in the future we overlook the Debug.Assert statement.
                _logger.LogCritical("Attempted to send message to SMTP server when no connection was established with it.");
                return (false, false, EmailFailureReason.NotConnected);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not send message to SMTP server.");
                return (false, false, EmailFailureReason.Unknown);
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
            return Helpers.CreateOptionsMonitor(options);
        }

        [GeneratedRegex(@"(\+|\.|\-)[0-9]+\@")]
        private static partial Regex MailRoutingRegex();

        #endregion
    }
}
