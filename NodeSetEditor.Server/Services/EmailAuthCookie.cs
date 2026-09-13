using System.Security.Cryptography;
using System.Text;

namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Issues and validates the HMAC-signed session cookie for the email-code sign-in path.
    /// The cookie payload is <c>base64("email|unixExpiry|base64(HMACSHA256(email|unixExpiry))")</c> —
    /// stateless (no server-side session store) and tamper-evident: any change to the email or
    /// expiry invalidates the signature. Modelled on the OPC Foundation working-group tooling.
    ///
    /// Registered as a singleton because it holds only the signing secret and does no I/O; it is
    /// shared by <see cref="EmailCookieAuthenticationHandler"/> (validate) and the auth controller
    /// (issue). Rotating <c>EmailAuth:CookieSecret</c> invalidates every outstanding cookie.
    /// </summary>
    public sealed class EmailAuthCookie
    {
        public const string CookieName = "opc-email-auth";
        public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

        private readonly byte[] _secret;

        public EmailAuthCookie(string cookieSecret)
        {
            if (string.IsNullOrEmpty(cookieSecret) || cookieSecret.Length < 16)
                throw new ArgumentException("EmailAuth:CookieSecret must be at least 16 characters.");
            _secret = Encoding.UTF8.GetBytes(cookieSecret);
        }

        /// <summary>Creates a signed cookie value for the given (already-verified) email.</summary>
        public string Create(string email)
        {
            var expiry = DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeSeconds();
            var payload = $"{email}|{expiry}";
            var hmac = ComputeHmac(payload);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{payload}|{hmac}"));
        }

        /// <summary>
        /// Validates a cookie value, returning the email if the signature and expiry check out,
        /// otherwise null. Constant-time signature comparison; never throws on malformed input.
        /// </summary>
        public string? Validate(string? cookieValue)
        {
            if (string.IsNullOrEmpty(cookieValue))
                return null;

            byte[] decoded;
            try { decoded = Convert.FromBase64String(cookieValue); }
            catch { return null; }

            var parts = Encoding.UTF8.GetString(decoded).Split('|');
            if (parts.Length != 3) return null;

            var email = parts[0];
            if (!long.TryParse(parts[1], out var expiry)) return null;

            var expectedHmac = ComputeHmac($"{email}|{expiry}");
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(parts[2]),
                    Encoding.UTF8.GetBytes(expectedHmac)))
                return null;

            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiry)
                return null;

            return email;
        }

        private string ComputeHmac(string payload)
        {
            using var hmac = new HMACSHA256(_secret);
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            return Convert.ToBase64String(hash);
        }
    }
}
