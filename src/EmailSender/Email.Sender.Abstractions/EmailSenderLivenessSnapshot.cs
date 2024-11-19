using System;

namespace ReconArt.Email
{
    /// <summary>
    /// Represents a liveness snapshot of an email sender service.
    /// </summary>
    public readonly struct EmailSenderLivenessSnapshot
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="EmailSenderLivenessSnapshot"/> type.
        /// </summary>
        /// <param name="exception">Exception that occurred during the liveness check.</param>
        /// <param name="unsuccessfulMailCount">Count of unsuccessfully sent emails.</param>
        /// <param name="timeInSecondsToNextLivenessCheck">Time remaining to next liveness check.</param>
        /// 
        public EmailSenderLivenessSnapshot(Exception? exception, int unsuccessfulMailCount, int timeInSecondsToNextLivenessCheck)
        {
            Exception = exception;
            UnsuccessfulMailCount = unsuccessfulMailCount;
            TimeInSecondsToNextLivenessCheck = timeInSecondsToNextLivenessCheck;
        }

        /// <summary>
        /// <see langword="true"/> if the service is alive and well, <see langword="false"/> otherwise.
        /// </summary>
        public bool Success => Exception is null;

        /// <summary>
        /// Exception that occurred during the last liveness check.
        /// </summary>
        public Exception? Exception { get; }

        /// <summary>
        /// Count of unsuccessfully sent emails.
        /// </summary>
        public int UnsuccessfulMailCount { get; }

        /// <summary>
        /// How much time remains to the next liveness check.
        /// </summary>
        public int TimeInSecondsToNextLivenessCheck { get; }
    }
}
