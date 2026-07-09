using MailKit.Security;
using MimeKit;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReconArt.Email.Sender.Internal
{
    internal interface IEmailSmtpClient : IDisposable
    {
        bool IsConnected { get; }

        bool IsAuthenticated { get; }

        Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken);

        Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken);

        Task AuthenticateAsync(SaslMechanism mechanism, CancellationToken cancellationToken);

        Task NoOpAsync(CancellationToken cancellationToken);

        Task SendAsync(MimeMessage message, CancellationToken cancellationToken);

        Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default);
    }
}
