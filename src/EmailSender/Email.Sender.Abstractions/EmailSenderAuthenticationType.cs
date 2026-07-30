namespace ReconArt.Email
{
    /// <summary>
    /// Supported authentication flows for the email sender.
    /// </summary>
    public enum EmailSenderAuthenticationType
    {
        /// <summary>
        /// Uses the SMTP basic authentication flow and optionally sends unauthenticated when
        /// <see cref="EmailSenderOptions.RequiresAuthentication"/> is disabled.
        /// </summary>
        Basic = 0,

        /// <summary>
        /// Uses the SMTP OAuth2 authentication flow with an access token.
        /// </summary>
        OAuth2 = 1
    }
}
