using System.Security.Cryptography;
using System.Text;
using NodeSetEditor.Model;

namespace NodeSetEditor.Server.Services
{
    /// <summary>Outcome of verifying a submitted email login code.</summary>
    public enum VerifyResult
    {
        /// <summary>Code matched — the caller should issue a session cookie.</summary>
        Success,
        /// <summary>No pending code, wrong code, or the code has expired.</summary>
        InvalidOrExpired,
        /// <summary>Too many failed attempts for the current code — a new code must be requested.</summary>
        LockedOut,
    }

    /// <summary>
    /// Postgres-backed one-time email code service for passwordless sign-in. Generates a
    /// 6-digit code, stores only a salted SHA-256 hash (never plaintext), and enforces a short
    /// expiry, a resend throttle, and a failed-attempt lockout. Scoped, since it uses the
    /// per-request <see cref="NodeSetEditorDbContext"/>.
    ///
    /// Hardens the OPC Foundation working-group original (in-memory, plaintext, no lockout):
    /// the hash defends against a DB leak and the lockout + 10-minute lifetime make an online
    /// guess of the 1-in-a-million code infeasible.
    /// </summary>
    public sealed class EmailVerificationService
    {
        public const int MaxAttempts = 5;
        public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan ResendThrottle = TimeSpan.FromSeconds(30);

        private readonly ILoginCodeStore _store;
        private readonly IEmailSender _email;
        private readonly ILogger<EmailVerificationService> _logger;

        public EmailVerificationService(
            ILoginCodeStore store,
            IEmailSender email,
            ILogger<EmailVerificationService> logger)
        {
            _store = store;
            _email = email;
            _logger = logger;
        }

        /// <summary>
        /// Generates and emails a fresh code for the address, replacing any pending one. If the
        /// last code was sent within <see cref="ResendThrottle"/> the request is silently ignored
        /// (anti-bombing) — the caller returns a generic success either way so the endpoint never
        /// reveals whether an address is throttled or even valid.
        /// </summary>
        public async Task RequestCodeAsync(string email, CancellationToken ct = default)
        {
            var key = Normalize(email);
            var now = DateTime.UtcNow;

            var entry = await _store.FindAsync(key, ct);
            if (entry != null && now - entry.SentAt < ResendThrottle)
            {
                _logger.LogInformation("Login code resend throttled for {Email}", key);
                return;
            }

            var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            var salt = RandomNumberGenerator.GetBytes(16);

            entry ??= new LoginCode { Email = key };
            entry.CodeHash = Hash(code, salt);
            entry.Salt = Convert.ToBase64String(salt);
            entry.ExpiresAt = now.Add(CodeLifetime);
            entry.Attempts = 0;
            entry.SentAt = now;

            await _store.SaveAsync(entry, ct);
            await _email.SendLoginCodeAsync(key, code, ct);
        }

        /// <summary>
        /// Verifies a submitted code. On success the code is consumed (single-use). A wrong code
        /// increments the attempt counter and locks out after <see cref="MaxAttempts"/>; an expired
        /// code is discarded.
        /// </summary>
        public async Task<VerifyResult> VerifyCodeAsync(string email, string code, CancellationToken ct = default)
        {
            var key = Normalize(email);
            var now = DateTime.UtcNow;

            var entry = await _store.FindAsync(key, ct);
            if (entry == null)
                return VerifyResult.InvalidOrExpired;

            if (now > entry.ExpiresAt)
            {
                await _store.RemoveAsync(key, ct);
                return VerifyResult.InvalidOrExpired;
            }

            if (entry.Attempts >= MaxAttempts)
                return VerifyResult.LockedOut;

            var candidate = Hash(code?.Trim() ?? string.Empty, Convert.FromBase64String(entry.Salt));
            var matched = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(candidate),
                Encoding.UTF8.GetBytes(entry.CodeHash));

            if (!matched)
            {
                entry.Attempts++;
                await _store.SaveAsync(entry, ct);
                return entry.Attempts >= MaxAttempts ? VerifyResult.LockedOut : VerifyResult.InvalidOrExpired;
            }

            // Correct — codes are single-use.
            await _store.RemoveAsync(key, ct);
            return VerifyResult.Success;
        }

        /// <summary>Lowercased, trimmed email — the canonical identity key across both sign-in paths.</summary>
        public static string Normalize(string email) => (email ?? string.Empty).Trim().ToLowerInvariant();

        private static string Hash(string code, byte[] salt)
        {
            var codeBytes = Encoding.UTF8.GetBytes(code);
            var buffer = new byte[salt.Length + codeBytes.Length];
            Buffer.BlockCopy(salt, 0, buffer, 0, salt.Length);
            Buffer.BlockCopy(codeBytes, 0, buffer, salt.Length, codeBytes.Length);
            return Convert.ToBase64String(SHA256.HashData(buffer));
        }
    }
}
