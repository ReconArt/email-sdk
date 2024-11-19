# ReconArt.Email.Abstractions

This package contains the core abstractions for the ReconArt.Email.Sender package.

## Contents
- `IEmailMessage` for creating email messages.
- `IEmailAttachment` to attach embedded or regular files to emails.
- `IEmailSenderService` for the contract of the email sending service.
- `IEmailSenderLivenessService` to check the liveness of the email sending service.
- `EmailFailureReason` to provide a reason for email sending failure.
- `EmailSenderOptions` to configure the email sending service.
- `EmailSenderLivenessOptions` to configure the email sending liveness service.
- `EmailSenderLivenessSnapshot` to represent a liveness snapshot of the email sending service.

## Integration
These abstractions are designed to be implemented by email sending services. Use in conjunction with `ReconArt.Email.Sender` for a complete solution.

Extend these interfaces to customize email handling for your specific needs.

## Contributing

If you'd like to contribute to the project, please reach out to the [ReconArt/email-sdk](https://github.com/orgs/ReconArt/teams/email-sdk) team.

## Support

If you encounter any issues or require assistance, please file an issue in the [GitHub Issues](https://github.com/ReconArt/email-sdk/issues) section of the repository.

## Authors and Acknowledgments

Developed by [ReconArt, Inc.](https://reconart.com/). 