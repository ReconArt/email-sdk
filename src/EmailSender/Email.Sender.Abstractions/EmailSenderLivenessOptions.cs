namespace ReconArt.Email
{
    /// <summary>
    /// Options used to configure the behavior of the email sender liveness service.
    /// </summary>
    public class EmailSenderLivenessOptions
    {
        /// <summary>
        /// Name of the configuration section for the email sender liveness options.
        /// </summary>
        /// <remarks>
        /// Currently the default value for this is to use the section name of the <see cref="EmailSenderOptions"/>, for a unified experience.
        /// <br/>
        /// If in the future, the options here expand, it might be a good idea to separate them into their own section.
        /// </remarks>
        public const string SectionName = EmailSenderOptions.SectionName;

        /// <summary>
        /// Set to <see langword="true"/> to reset the count of unsuccessfully sent email messages
        /// when a liveness check is performed.
        /// <br/><br/> <i>Default value:</i> <see langword="true"/>
        /// </summary>
        public bool LivenessReportResetsMessageCount { get; set; } = true;
    }
}
