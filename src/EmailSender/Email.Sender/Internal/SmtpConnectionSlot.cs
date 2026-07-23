namespace ReconArt.Email.Sender.Internal
{
    internal sealed class SmtpConnectionSlot
    {
        public int Index { get; set; }

        public required IEmailSmtpClient Client { get; set; }

        public int InUse;
    }
}
