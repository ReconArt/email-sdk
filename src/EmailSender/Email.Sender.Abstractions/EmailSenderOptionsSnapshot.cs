namespace ReconArt.Email
{
    /// <summary>
    /// Represents the effective email sender configuration at a point in time.
    /// </summary>
    public sealed class EmailSenderOptionsSnapshot
    {
        /// <summary>
        /// Gets or sets the current effective options.
        /// </summary>
        /// <remarks>
        /// Set to <see langword="null"/> when the email sender is currently unavailable.
        /// </remarks>
        public EmailSenderOptions? Options { get; set; }

        /// <summary>
        /// Gets or sets a revision that identifies structural configuration changes.
        /// </summary>
        /// <remarks>
        /// This value is used to determine when the sender should rebuild its SMTP runtime state.
        /// Token-only OAuth2 changes do not need to change this revision.
        /// </remarks>
        public string? ConfigurationRevision { get; set; }
    }
}
