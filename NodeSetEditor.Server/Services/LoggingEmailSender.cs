namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Fallback email sender used when no Postmark key is configured (local development).
    /// It writes the code to the log instead of sending mail so a developer can complete
    /// the email-code flow without a live provider. NEVER selected when PostmarkApiKey is
    /// set, and must not be used in a deployed environment.
    /// </summary>
    public sealed class LoggingEmailSender : IEmailSender
    {
        private readonly ILogger<LoggingEmailSender> _logger;

        public LoggingEmailSender(ILogger<LoggingEmailSender> logger) => _logger = logger;

        public Task SendLoginCodeAsync(string email, string code, CancellationToken ct = default)
        {
            _logger.LogWarning("[DEV EMAIL] Login code for {Email}: {Code} (no email provider configured)", email, code);
            return Task.CompletedTask;
        }
    }
}
