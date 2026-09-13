using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// SMTP configuration for a self-hosted deployment, read from the <c>Smtp</c> configuration
    /// section (environment variables <c>Smtp__Host</c>, <c>Smtp__Port</c>, …).
    /// </summary>
    public sealed record SmtpOptions
    {
        public string Host { get; init; } = string.Empty;
        public int Port { get; init; } = 587;
        public string From { get; init; } = string.Empty;
        public string? FromName { get; init; }
        public string? User { get; init; }
        public string? Password { get; init; }

        /// <summary>
        /// <c>StartTls</c> (default, port 587), <c>SslOnConnect</c> (implicit TLS, port 465),
        /// <c>None</c> (plaintext — only sensible for a relay on a trusted network), or
        /// <c>Auto</c> to let MailKit choose from what the server advertises.
        /// </summary>
        public SecureSocketOptions Security { get; init; } = SecureSocketOptions.StartTls;

        /// <summary>Skips certificate validation. For a relay with a self-signed certificate.</summary>
        public bool AcceptInvalidCertificate { get; init; }

        /// <summary>Reads the section, returning null when no host is configured.</summary>
        public static SmtpOptions? FromConfiguration(IConfiguration configuration)
        {
            var section = configuration.GetSection("Smtp");
            var host = section["Host"];
            if (string.IsNullOrWhiteSpace(host)) return null;

            var security = section["Security"];

            return new SmtpOptions
            {
                Host = host.Trim(),
                Port = int.TryParse(section["Port"], out var port) ? port : 587,
                From = section["From"]?.Trim() ?? string.Empty,
                FromName = section["FromName"],
                User = section["User"],
                Password = section["Password"],
                Security = string.IsNullOrWhiteSpace(security)
                    ? SecureSocketOptions.StartTls
                    : Enum.TryParse<SecureSocketOptions>(security, ignoreCase: true, out var parsed)
                        ? parsed
                        : throw new InvalidOperationException(
                            $"Smtp:Security '{security}' is not valid. Use None, Auto, SslOnConnect or StartTls."),
                AcceptInvalidCertificate = bool.TryParse(section["AcceptInvalidCertificate"], out var accept) && accept,
            };
        }
    }

    /// <summary>
    /// Delivers login codes through an operator-supplied SMTP server — the path a self-hosted
    /// deployment uses. MailKit rather than <c>System.Net.Mail</c> because the latter cannot do
    /// implicit TLS (port 465), which several providers require.
    /// </summary>
    public sealed class SmtpEmailSender : IEmailSender
    {
        private readonly SmtpOptions _options;
        private readonly ILogger<SmtpEmailSender> _logger;

        public SmtpEmailSender(SmtpOptions options, ILogger<SmtpEmailSender> logger)
        {
            _options = options;
            _logger = logger;
        }

        public async Task SendLoginCodeAsync(string email, string code, CancellationToken ct = default)
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_options.FromName ?? "NodeSet Editor", _options.From));
            message.To.Add(MailboxAddress.Parse(email));
            message.Subject = LoginCodeMessage.Subject;
            message.Body = new BodyBuilder
            {
                TextBody = LoginCodeMessage.TextBody(code),
                HtmlBody = LoginCodeMessage.HtmlBody(code),
            }.ToMessageBody();

            using var client = new SmtpClient();
            if (_options.AcceptInvalidCertificate)
                client.ServerCertificateValidationCallback = (_, _, _, _) => true;

            try
            {
                await client.ConnectAsync(_options.Host, _options.Port, _options.Security, ct);

                if (!string.IsNullOrEmpty(_options.User))
                    await client.AuthenticateAsync(_options.User, _options.Password ?? string.Empty, ct);

                await client.SendAsync(message, ct);
                await client.DisconnectAsync(quit: true, ct);
            }
            catch (Exception ex)
            {
                // Logged with the provider's reason, but the caller returns the same generic
                // response either way so the endpoint cannot be used to probe deliverability.
                _logger.LogError(ex, "SMTP send failed for login code via {Host}:{Port}",
                    _options.Host, _options.Port);
                throw new InvalidOperationException("Failed to send login code email.", ex);
            }
        }
    }
}
