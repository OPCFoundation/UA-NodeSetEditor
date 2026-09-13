using PostmarkDotNet;

namespace NodeSetEditor.Server.Services
{
    /// <summary>Postmark configuration (API key + verified sender address).</summary>
    public sealed record PostmarkOptions(string ApiKey, string SenderEmail);

    /// <summary>
    /// Delivers login codes via Postmark — the same transactional email provider the
    /// OPC Foundation working-group tooling uses, so this reuses the existing
    /// PostmarkApiKey / PostmarkSenderEmail configuration and verified sender domain.
    /// </summary>
    public sealed class PostmarkEmailSender : IEmailSender
    {
        private readonly PostmarkOptions _options;
        private readonly ILogger<PostmarkEmailSender> _logger;

        public PostmarkEmailSender(PostmarkOptions options, ILogger<PostmarkEmailSender> logger)
        {
            _options = options;
            _logger = logger;
        }

        public async Task SendLoginCodeAsync(string email, string code, CancellationToken ct = default)
        {
            var client = new PostmarkClient(_options.ApiKey);
            var message = new PostmarkMessage
            {
                From = _options.SenderEmail,
                To = email,
                Subject = LoginCodeMessage.Subject,
                TextBody = LoginCodeMessage.TextBody(code),
                HtmlBody = LoginCodeMessage.HtmlBody(code),
                MessageStream = "outbound",
            };

            var response = await client.SendMessageAsync(message);
            if (response.Status != PostmarkStatus.Success)
            {
                // Log the provider-side reason but do NOT surface it to the caller —
                // the API returns a generic "code sent" so the endpoint can't be used
                // to probe which addresses are deliverable.
                _logger.LogError("Postmark send failed for login code: {ErrorCode} {Message}",
                    response.ErrorCode, response.Message);
                throw new InvalidOperationException("Failed to send login code email.");
            }
        }
    }
}
