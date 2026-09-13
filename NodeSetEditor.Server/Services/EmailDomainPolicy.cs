namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Decides which email addresses may request a sign-in code, from the comma-separated
    /// <c>Auth:AllowedEmailDomains</c> setting. An entry containing '@' names a single address;
    /// anything else names a domain and admits every address in it.
    ///
    /// <para>An empty or absent setting admits everyone, which is the hosted deployment's
    /// behaviour — there the Azure AD tenant, not this list, is the perimeter. A self-hosted
    /// instance has no such perimeter: with email-code sign-in and no allow-list, anyone who can
    /// reach the port can create an account. Self-hosted deployments should set it.</para>
    /// </summary>
    public sealed class EmailDomainPolicy
    {
        public const string ConfigurationKey = "Auth:AllowedEmailDomains";

        private readonly HashSet<string> _addresses = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _domains = new(StringComparer.OrdinalIgnoreCase);

        public EmailDomainPolicy(IConfiguration configuration)
        {
            var configured = configuration[ConfigurationKey];
            if (string.IsNullOrWhiteSpace(configured)) return;

            foreach (var raw in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var entry = raw.Trim();
                if (entry.Length == 0) continue;

                if (entry.StartsWith('@')) _domains.Add(entry[1..]);
                else if (entry.Contains('@')) _addresses.Add(entry);
                else _domains.Add(entry);
            }
        }

        /// <summary>True when no allow-list is configured, so the check is a no-op by default.</summary>
        public bool IsUnrestricted => _addresses.Count == 0 && _domains.Count == 0;

        public bool IsAllowed(string? email)
        {
            if (IsUnrestricted) return true;
            if (string.IsNullOrWhiteSpace(email)) return false;

            var address = email.Trim();
            if (_addresses.Contains(address)) return true;

            var at = address.LastIndexOf('@');
            return at >= 0 && at < address.Length - 1 && _domains.Contains(address[(at + 1)..]);
        }
    }
}
