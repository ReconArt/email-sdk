using System;

namespace ReconArt.Email
{
    /// <summary>
    /// Represents refreshed OAuth2 token values for the email sender.
    /// </summary>
    public sealed class EmailSenderOAuthRefreshResult
    {
        /// <summary>
        /// Gets or sets the refreshed OAuth2 access token.
        /// </summary>
        public string AccessToken { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the UTC expiration timestamp of the refreshed OAuth2 access token.
        /// </summary>
        public DateTime AccessTokenExpiresAtUtc { get; set; }
    }
}
