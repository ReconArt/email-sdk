using System.IO;

namespace ReconArt.Email
{
    /// <summary>
    /// A default implementation for an email attachment.
    /// </summary>
    public class EmailAttachment : IEmailAttachment
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="EmailAttachment"/> class.
        /// </summary>
        /// <param name="fileName">Name of the file. Used for the Content-Disposition header.</param>
        /// <param name="content">Content of the file.</param>
        /// <param name="placeholder">Optional placeholder text for inline attachments.</param>
        /// <param name="leaveOpen">
        /// <see langword="true"/> to leave the stream open after the <see cref="EmailAttachment"/> object is disposed; otherwise, <see langword="false"/>.
        /// </param>
        public EmailAttachment(
            string fileName,
            Stream content,
            string? placeholder = null,
            bool leaveOpen = false)
        {
            FileName = fileName;
            Content = content;
            DisposeContent = !leaveOpen;
            Placeholder = placeholder;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EmailAttachment"/> class from a file path.
        /// </summary>
        /// <param name="filePath">Path of the file to be attached.</param>
        /// <param name="placeholder">Optional placeholder text for inline attachments.</param>
        /// <param name="leaveOpen">
        /// <see langword="true"/> to leave the stream open after the <see cref="EmailAttachment"/> object is disposed; otherwise, <see langword="false"/>.
        /// </param>
        public EmailAttachment(
            string filePath,
            string? placeholder = null,
            bool leaveOpen = false)
        {
            Content = File.OpenRead(filePath);
            DisposeContent = !leaveOpen;
            FileName = Path.GetFileName(filePath);
            Placeholder = placeholder;
        }

        /// <inheritdoc/>
        public string FileName { get; }

        /// <inheritdoc/>
        public Stream Content { get; }

        /// <inheritdoc/>
        public bool DisposeContent { get; }

        /// <inheritdoc/>
        public string? Placeholder { get; }
    }
}
