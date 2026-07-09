namespace ReconArt.Email.Sender.Internal
{
    internal sealed class SmtpConnectionSlot
    {
        public required IEmailSmtpClient Client { get; set; }

        public int InUse;

        public bool RequiresReconnect;

        public bool RequiresClientReinitialization;

        public string? ConfigurationRevision;
    }
}
