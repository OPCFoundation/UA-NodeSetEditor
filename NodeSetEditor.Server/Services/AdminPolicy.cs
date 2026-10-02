namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Decides who may curate models on behalf of everyone else. An admin can open the model
    /// dialog on ANY model in a workspace they can reach — shared and published ones included,
    /// and models in the reserved <c>http://opcfoundation.org/</c> namespace — so that standard
    /// models (the UA Core nodeset above all) can be given a profile group centrally instead of
    /// each user setting one on a private copy.
    ///
    /// <para>Model rows are shared across workspaces, so an admin's edit is visible to every user
    /// linked to that model. That is the point of the role, and the reason it is an allow-list
    /// rather than something a user can grant themselves.</para>
    ///
    /// <para>The allow-list is the <c>AdminEmails</c> setting, supplied as a secret in the app
    /// environment and read once at startup. An entry containing '@' names a single user;
    /// anything else names a domain and admits every address in it. An empty or absent setting
    /// admits nobody, so no deployment has admins by accident. Deliberately NOT tied to test
    /// mode — unlike the beta-tester list, this grants write access to shared data.</para>
    /// </summary>
    public sealed class AdminPolicy
    {
        public const string ConfigurationKey = "AdminEmails";

        private readonly HashSet<string> _addresses = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _domains = new(StringComparer.OrdinalIgnoreCase);

        public AdminPolicy(IConfiguration configuration)
        {
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
        public bool IsAdmin(string? email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;

            var address = email.Trim();

            if (_addresses.Contains(address)) return true;

            var at = address.LastIndexOf('@');

            return at >= 0 && at < address.Length - 1 && _domains.Contains(address[(at + 1)..]);
        }
    }
}
