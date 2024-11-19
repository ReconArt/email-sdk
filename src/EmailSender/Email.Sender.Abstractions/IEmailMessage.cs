using System;
using System.Collections.Generic;

namespace ReconArt.Email
{
    /// <summary>
    /// Represents an email message.
    /// </summary>
    public interface IEmailMessage : IDisposable
    {
        /// <summary>
        /// Body of the email.
        /// </summary>
        string? Body { get; }

        /// <summary>
        /// Recipients of the email.
        /// </summary>
        IEnumerable<string> Recipients { get; }

        /// <summary>
        /// Attachments that will be sent with the email.
        /// </summary>
        IEnumerable<IEmailAttachment> Attachments { get; }

        /// <summary>
        /// Subject of the email.
        /// </summary>
        string? Subject { get; }

        /// <summary>
        /// Whether the email is important.
        /// </summary>
        bool IsImportant { get; }

        void IDisposable.Dispose()
        {
            GC.SuppressFinalize(this);
            foreach (var attachment in Attachments)
            {
                attachment.Dispose();
            }
        }
    }
}
