namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Test mode: a single shared account with every feature enabled, so the application can be
    /// evaluated without a mail provider. Enabled with <c>TestMode__Enabled=true</c>.
    ///
    /// <para>Deliberately opt-in and never an automatic fallback. Falling back to it when SMTP is
    /// missing would turn a real deployment with a mistyped <c>Smtp__Host</c> into a shared open
    /// account without anyone noticing.</para>
    ///
    /// <para>Independent of <c>ASPNETCORE_ENVIRONMENT</c>, so a container evaluating the app still
    /// runs with production hardening. It is not the <see cref="DevAuthMiddleware"/> bypass, which
    /// remains hard-gated to the Development environment.</para>
    ///
    /// <para>Everyone who visits a test-mode instance shares one identity and therefore one set of
    /// workspaces. An instance reachable from the internet is effectively world-writable.</para>
    /// </summary>
    public sealed class TestModeOptions
    {
        /// <summary>Identity key for the shared account. Not shown in the UI.</summary>
        public const string Email = "test-mode@localhost";

        /// <summary>What the UI shows wherever the signed-in user appears.</summary>
        public const string DisplayName = "Test Mode";

        public bool Enabled { get; init; }

        public static TestModeOptions FromConfiguration(IConfiguration configuration) => new()
        {
            Enabled = configuration.GetValue<bool>("TestMode:Enabled"),
        };

        /// <summary>True when <paramref name="email"/> is the shared test-mode account.</summary>
        public bool IsTestModeUser(string? email) =>
            Enabled && string.Equals(email, Email, StringComparison.OrdinalIgnoreCase);
    }
}
