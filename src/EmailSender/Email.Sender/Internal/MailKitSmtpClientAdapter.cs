using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

namespace ReconArt.Email.Sender.Internal
{
    internal sealed class MailKitSmtpClientAdapter : IEmailSmtpClient
    {
        private readonly SmtpClient _client;

        public MailKitSmtpClientAdapter(RemoteCertificateValidationCallback? serverCertificateValidationCallback)
        {
            _client = new SmtpClient();
            if (serverCertificateValidationCallback is not null)
            {
                _client.ServerCertificateValidationCallback = serverCertificateValidationCallback;
            }
        }

        public bool IsConnected => _client.IsConnected;

        public bool IsAuthenticated => _client.IsAuthenticated;

        public void UpdateServerCertificateValidationCallback(RemoteCertificateValidationCallback? serverCertificateValidationCallback) =>
            _client.ServerCertificateValidationCallback = serverCertificateValidationCallback;

        public Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken) =>
            _client.ConnectAsync(host, port, options, cancellationToken);

        public Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken) =>
            _client.AuthenticateAsync(username, password, cancellationToken);

        public Task AuthenticateAsync(SaslMechanism mechanism, CancellationToken cancellationToken) =>
            _client.AuthenticateAsync(mechanism, cancellationToken);

        public Task NoOpAsync(CancellationToken cancellationToken) => _client.NoOpAsync(cancellationToken);

        public Task SendAsync(MimeMessage message, CancellationToken cancellationToken) =>
            _client.SendAsync(message, cancellationToken);

        public Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default) =>
            _client.DisconnectAsync(quit, cancellationToken);

        public void Dispose() => _client.Dispose();
    }
}
