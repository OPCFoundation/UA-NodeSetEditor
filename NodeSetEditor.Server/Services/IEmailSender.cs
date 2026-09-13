namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Sends transactional email for the passwordless "email code" sign-in path.
    /// Implemented by <see cref="PostmarkEmailSender"/> in deployed environments and by
    /// <see cref="LoggingEmailSender"/> when no provider is configured (local dev).
    /// </summary>
    public interface IEmailSender
    {
        /// <summary>Emails a one-time login code to the given address.</summary>
        Task SendLoginCodeAsync(string email, string code, CancellationToken ct = default);
    }
}
