namespace ReconArt.Email
{
    /// <summary>
    /// Represents the reason an email failed to send.
    /// </summary>
    public enum EmailFailureReason
    {
        /// <summary>
        /// No failure occurred.
        /// </summary>
        None = 0,

        /// <summary>
        /// An unknown or general failure occurred.
        /// </summary>
        Unknown = 1,

        /// <summary>
        /// Email sender has been disposed.
        /// </summary>
        Disposed = 2,

        /// <summary>
        /// Not connected to the email server, possibly due to network issues, incorrect credentials, or an invalid host.
        /// </summary>
        NotConnected = 3,

        /// <summary>
        /// Email server rejected one of the addresses.
        /// </summary>
        /// <remarks>
        /// This may occur when too many addresses are used in fields like BCC, CC, or TO.
        /// <br/>
        /// Exceeding character limits can cause trimming, leading to invalid addresses.
        /// </remarks>
        InvalidAddress = 4,

        /// <summary>
        /// Email server rejected the email because the sender is not allowed to use the specified address.
        /// </summary>
        SendAsDenied = 5,

        /// <summary>
        /// Invalid parameters were supplied.
        /// </summary>
        /// <remarks>
        /// This may occur if the email message was improperly constructed, such as:
        /// <br/>
        /// - Using invalid email addresses.
        /// <br/>
        /// - Missing referenced resources, like images.
        /// <br/>
        /// - Other issues verified during MIME message creation.
        /// </remarks>
        InvalidParameters = 6,
    }
}
