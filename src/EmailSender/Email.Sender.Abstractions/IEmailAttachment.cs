using System;
using System.IO;

namespace ReconArt.Email
{
    /// <summary>
    /// Represents an email attachment.
    /// </summary>
    public interface IEmailAttachment : IDisposable
    {
        /// <summary>
        /// Name of the attachment file.
        /// </summary>
        string FileName { get; }

        /// <summary>
        /// Content stream of the attachment.
        /// </summary>
        Stream Content { get; }

        /// <summary>
        /// Set to <c>true</c> if the attachment should be disposed after use.
        /// </summary>
        bool DisposeContent { get; }

        /// <summary>
        /// Placeholder text that this attachment corresponds to.
        /// </summary>
        /// <remarks>
        /// Specifying this will make the attachment inline, allowing it to be displayed directly within the email body.
        /// If <c>null</c>, the attachment will be treated as a traditional, non-inline attachment.
        /// </remarks>
        string? Placeholder { get; }

        /// <inheritdoc />
        void IDisposable.Dispose()
        {
            GC.SuppressFinalize(this);

            if (DisposeContent)
            {
                Content.Dispose();
            }
        }
    }
}
