using Microsoft.Extensions.Logging;
using MimeKit;

namespace ReconArt.Email.Sender.Internal
{
    internal static class AttachmentCollectionExtensions
    {
        internal static MimeEntity AddSerialized(this AttachmentCollection collection, IEmailAttachment attachment)
        {
            lock (attachment.Content)
            {
                if (attachment.Content.CanSeek)
                {
                    attachment.Content.Position = 0;
                }

                return collection.Add(attachment.FileName, attachment.Content);
            }
        }
    }
}
