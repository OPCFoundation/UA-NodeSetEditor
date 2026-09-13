namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Decides who may use features that are not open to everyone yet — currently the JSON,
    /// JSON-LD and archive download formats, which are shown to all users but only offered to
    /// beta testers.
    ///
    /// <para>The allow-list is the <c>BetaTesterDomains</c> setting: a comma-separated list read
    /// once at startup. An entry containing '@' names a single user; anything else names a domain
    /// and admits every address in it. An empty or absent setting admits nobody, so the features
    /// stay closed until someone is deliberately let in.</para>
    /// </summary>
    public sealed class BetaTesterPolicy
    {
        public const string ConfigurationKey = "BetaTesterDomains";

        private readonly HashSet<string> _addresses = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _domains = new(StringComparer.OrdinalIgnoreCase);
        private readonly TestModeOptions _testMode;

        public BetaTesterPolicy(IConfiguration configuration, TestModeOptions testMode)
        {
            _testMode = testMode;

            var configured = configuration[ConfigurationKey];

            if (string.IsNullOrWhiteSpace(configured))
            {
                return;
            }

            foreach (var raw in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var entry = raw.Trim();

                if (entry.Length == 0) continue;

                if (entry.Contains('@'))
                {
                    // "@example.com" is a domain written the way people usually write one.
                    if (entry.StartsWith('@')) _domains.Add(entry[1..]);
                    else _addresses.Add(entry);
                }
                else
                {
                    _domains.Add(entry);
                }
            }
        }

        /// <summary>True when the list names this address outright, or the domain it belongs to.</summary>
        public bool IsBetaTester(string? email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;

            var address = email.Trim();

            // Test mode exists to show the whole application, so the shared account gets the
            // beta formats regardless of the allow-list.
            if (_testMode.IsTestModeUser(address)) return true;

            if (_addresses.Contains(address)) return true;

            var at = address.LastIndexOf('@');

            return at >= 0 && at < address.Length - 1 && _domains.Contains(address[(at + 1)..]);
        }
    }
}
