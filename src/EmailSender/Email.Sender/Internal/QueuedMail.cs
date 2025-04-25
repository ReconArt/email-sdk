using MimeKit;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReconArt.Email.Sender.Internal
{
    internal sealed class QueuedMail : IDisposable
    {
        private bool _disposed;

        public QueuedMail(MimeMessage mimeMessage, IEmailMessage message, CancellationToken cancellationToken)
        {
            MimeMessage = mimeMessage;
            Message = message;
            CancellationToken = cancellationToken;
        }

        public QueuedMail(MimeMessage mimeMessage, IEmailMessage message, TaskCompletionSource<bool>? completionSource, CancellationToken cancellationToken)
            : this(mimeMessage, message, cancellationToken)
        {
            TaskCompletionSource = completionSource;
        }

        public MimeMessage MimeMessage { get; }

        public IEmailMessage Message { get; }

        public TaskCompletionSource<bool>? TaskCompletionSource { get; }

        public CancellationToken CancellationToken { get; }

        public void Delivered()
        {
            if (TaskCompletionSource is not null && !TaskCompletionSource.Task.IsCompleted)
            {
                TaskCompletionSource.TrySetResult(true);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                MimeMessage.Dispose();
                Message.Dispose();

                if (TaskCompletionSource is not null && !TaskCompletionSource.Task.IsCompleted)
                {
                    TaskCompletionSource.TrySetResult(false);
                }
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}
