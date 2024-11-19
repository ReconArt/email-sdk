using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReconArt.Email.Sender.Internal;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReconArt.Email
{
    /// <summary>
    /// Responsible for reporting health status checks for <see cref="IEmailSenderService"/>.
    /// </summary>
    public class EmailSenderLivenessService : BackgroundService, IEmailSenderLivenessService
    {
        private readonly IEmailSenderService _emailService;
        private readonly IOptionsMonitor<EmailSenderLivenessOptions> _mailOptions;
        private readonly ILogger<EmailSenderLivenessService> _logger;
        private readonly TimeSpan _connectionRetryWaitTimeOnSuccess = TimeSpan.FromMinutes(10);
        private readonly TimeSpan _connectionRetryWaitTimeOnFailure = TimeSpan.FromMinutes(2);

        private Exception? _connectionException;
        private DateTime _nextConnectionTestDate;

        /// <summary>
        /// Constructs a new instance of <see cref="EmailSenderLivenessService"/>.
        /// </summary>
        /// <param name="emailService">Email service to check for liveness.</param>
        /// <param name="optionsMonitor">Email sender liveness options.</param>
        /// <param name="logger">Logger used to log information.</param>
        /// 
        public EmailSenderLivenessService(IEmailSenderService emailService, 
            IOptionsMonitor<EmailSenderLivenessOptions> optionsMonitor,
            ILogger<EmailSenderLivenessService> logger)
        {
            _emailService = emailService;
            _mailOptions = optionsMonitor;
            _logger = logger;
        }

        /// <summary>
        /// Constructs a new instance of <see cref="EmailSenderLivenessService"/>.
        /// </summary>
        public EmailSenderLivenessService(IEmailSenderService emailService, EmailSenderLivenessOptions options, Action<ILoggingBuilder>? configureLogger = null) 
            : this(emailService, Helpers.CreateOptionsMonitor(options), Helpers.CreateLogger<EmailSenderLivenessService>(configureLogger))
        {
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Liveness check: Service starting.");
            try
            {
                do
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    EmailSenderLivenessOptions livenessOptions = _mailOptions.CurrentValue;
                    bool previouslyFailed = _connectionException is not null;

                    bool failed = await TestSmtpConnectionFailureAsync(stoppingToken).ConfigureAwait(false);

                    if (previouslyFailed && failed)
                    {
                        _logger.LogWarning("Liveness check: SMTP server is still not reachable. Will retry in {RetryWaitTime} seconds.",
                            _connectionRetryWaitTimeOnFailure.TotalSeconds);
                    }

                    if (livenessOptions.LivenessReportResetsMessageCount)
                    {
                        _emailService.ResetCount();
                    }

                    TimeSpan retryTime = failed ? _connectionRetryWaitTimeOnFailure : _connectionRetryWaitTimeOnSuccess;
                    _nextConnectionTestDate = DateTime.UtcNow.Add(retryTime);
                    await Task.Delay(retryTime, stoppingToken).ConfigureAwait(false);

                } while (true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _connectionException = ex;
                _logger.LogCritical(ex, "Liveness check: Service went down due to an unhandled exception.");
            }
        }

        /// <inheritdoc/>
        public ValueTask<EmailSenderLivenessSnapshot> GetSnapshotAsync()
        {
            int secondsToNextCheck = (int)(_nextConnectionTestDate - DateTime.UtcNow).TotalSeconds;
            if (secondsToNextCheck < 0)
            {
                secondsToNextCheck = 0;
            }

            return new ValueTask<EmailSenderLivenessSnapshot>(
                new EmailSenderLivenessSnapshot(_connectionException, _emailService.GetFailedMessagesCount(), secondsToNextCheck));
        }

        private async Task<bool> TestSmtpConnectionFailureAsync(CancellationToken cancellationToken)
        {
            Exception? exception = await _emailService.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (exception is null)
            {
                _connectionException = null;
                return false;
            }
            else
            {
                if (exception is OperationCanceledException)
                {
                    _logger.LogError(exception, "Liveness check: Operation was cancelled while connecting to the SMTP server.");
                }
                else
                {
                    _logger.LogError(exception, "Liveness check: Could not connect to the SMTP server.");
                }

                _connectionException = exception;
                return true;
            }
        }
    }
}
