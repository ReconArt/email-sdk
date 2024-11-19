using System;
using System.Collections.Generic;

namespace ReconArt.Email
{
    /// <summary>
    /// A default implementation for an email message.
    /// </summary>
    public class EmailMessage : IEmailMessage
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="EmailMessage"/> class.
        /// </summary>
        /// <param name="recipient">
        /// Recipient of the email.
        /// </param>
        /// <param name="subject">
        /// Subject of the email.
        /// </param>
        /// <param name="body">
        /// Body of the email.
        /// </param>
        /// <param name="isImportant">
        /// Whether the email is important.
        /// </param>
        public EmailMessage(string? recipient = null, string? subject = null, string? body = null, bool isImportant = false)
            : this(CreateRecipientsCollection(recipient), subject, body, isImportant)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EmailMessage"/> class.
        /// </summary>
        /// <param name="recipients">
        /// Recipients of the email.
        /// </param>
        /// <param name="subject">
        /// Subject of the email.
        /// </param>
        /// <param name="body">
        /// Body of the email.
        /// </param>
        /// <param name="isImportant">
        /// Whether the email is important.
        /// </param>
        public EmailMessage(IEnumerable<string> recipients, string? subject = null, string? body = null, bool isImportant = false)
            : this(recipients, new List<IEmailAttachment>(), subject, body, isImportant)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EmailMessage"/> class.
        /// </summary>
        /// <param name="recipients">
        /// Recipients of the email.
        /// </param>
        /// <param name="attachments">
        /// Attachments that will be sent with the email.
        /// </param>
        /// <param name="subject">
        /// Subject of the email.
        /// </param>
        /// <param name="body">
        /// Body of the email.
        /// </param>
        /// <param name="isImportant">
        /// Whether the email is important.
        /// </param>
        public EmailMessage(
            IEnumerable<string> recipients,
            IEnumerable<IEmailAttachment> attachments,
            string? subject = null,
            string? body = null,
            bool isImportant = false)
        {
            ArgumentNullException.ThrowIfNull(recipients, nameof(recipients));
            ArgumentNullException.ThrowIfNull(attachments, nameof(attachments));

            Recipients = recipients;
            Attachments = attachments;
            Body = body;
            Subject = subject;
            IsImportant = isImportant;
        }

        /// <inheritdoc/>
        public string? Body { get; set; }

        /// <inheritdoc/>
        public IEnumerable<string> Recipients { get; set; }

        /// <inheritdoc/>
        public IEnumerable<IEmailAttachment> Attachments { get; set; }

        /// <inheritdoc/>
        public string? Subject { get; set; }

        /// <inheritdoc/>
        public bool IsImportant { get; set; }

        private static List<string> CreateRecipientsCollection(string? recipient)
        {
            List<string> recipients = new();
            if (recipient is not null)
            {
                recipients.Add(recipient);

            }

            return recipients;
        }
    }
}
