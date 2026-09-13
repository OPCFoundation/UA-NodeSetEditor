using Microsoft.Extensions.Logging.Abstractions;
using NodeSetEditor.Model;
using NodeSetEditor.Server.Services;

namespace NodeSetEditor.Server.Tests;

/// <summary>
/// Unit tests for the email one-time-code logic (generate / verify / lockout / expiry / throttle /
/// single-use), exercised against an in-memory <see cref="ILoginCodeStore"/> so no database is
/// required. The EF-backed store is a thin adapter covered by the integration suite.
/// </summary>
public class EmailVerificationServiceTests
{
    /// <summary>In-memory store that clones on the way in and out, mimicking DB isolation.</summary>
    private sealed class InMemoryLoginCodeStore : ILoginCodeStore
    {
        private readonly Dictionary<string, LoginCode> _map = new();

        public Task<LoginCode?> FindAsync(string email, CancellationToken ct = default)
            => Task.FromResult(_map.TryGetValue(email, out var v) ? Clone(v) : null);

        public Task SaveAsync(LoginCode entry, CancellationToken ct = default)
        {
            _map[entry.Email] = Clone(entry);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string email, CancellationToken ct = default)
        {
            _map.Remove(email);
            return Task.CompletedTask;
        }

        public LoginCode? Peek(string email) => _map.TryGetValue(email, out var v) ? v : null;

        private static LoginCode Clone(LoginCode c) => new()
        {
            Email = c.Email,
            CodeHash = c.CodeHash,
            Salt = c.Salt,
            ExpiresAt = c.ExpiresAt,
            Attempts = c.Attempts,
            SentAt = c.SentAt,
        };
    }

    private sealed class CapturingEmailSender : IEmailSender
    {
        public int SendCount { get; private set; }
        public string? LastCode { get; private set; }

        public Task SendLoginCodeAsync(string email, string code, CancellationToken ct = default)
        {
            SendCount++;
            LastCode = code;
            return Task.CompletedTask;
        }
    }

    private const string Email = "User@Example.org";
    private const string Key = "user@example.org"; // normalized

    private static EmailVerificationService NewService(ILoginCodeStore store, IEmailSender sender)
        => new(store, sender, NullLogger<EmailVerificationService>.Instance);

    [Fact]
    public async Task RequestCode_Sends_AndStoresHashedCode()
    {
        var store = new InMemoryLoginCodeStore();
        var sender = new CapturingEmailSender();

        await NewService(store, sender).RequestCodeAsync(Email);

        Assert.Equal(1, sender.SendCount);
        Assert.Matches("^[0-9]{6}$", sender.LastCode!);

        // Stored under the normalized key, and never in plaintext.
        var row = store.Peek(Key);
        Assert.NotNull(row);
        Assert.NotEqual(sender.LastCode, row!.CodeHash);
        Assert.Equal(0, row.Attempts);
    }

    [Fact]
    public async Task VerifyCode_CorrectCode_Succeeds_AndIsSingleUse()
    {
        var store = new InMemoryLoginCodeStore();
        var sender = new CapturingEmailSender();
        var svc = NewService(store, sender);

        await svc.RequestCodeAsync(Email);

        Assert.Equal(VerifyResult.Success, await svc.VerifyCodeAsync(Email, sender.LastCode!));
        // Consumed — the same code cannot be reused.
        Assert.Equal(VerifyResult.InvalidOrExpired, await svc.VerifyCodeAsync(Email, sender.LastCode!));
        Assert.Null(store.Peek(Key));
    }

    [Fact]
    public async Task VerifyCode_IsCaseInsensitiveOnEmail()
    {
        var store = new InMemoryLoginCodeStore();
        var sender = new CapturingEmailSender();
        var svc = NewService(store, sender);

        await svc.RequestCodeAsync("MixedCase@Example.org");
        Assert.Equal(VerifyResult.Success, await svc.VerifyCodeAsync("mixedcase@example.ORG", sender.LastCode!));
    }

    [Fact]
    public async Task VerifyCode_WrongCode_LocksOut_AfterMaxAttempts()
    {
        var store = new InMemoryLoginCodeStore();
        var sender = new CapturingEmailSender();
        var svc = NewService(store, sender);

        await svc.RequestCodeAsync(Email);

        for (var i = 0; i < EmailVerificationService.MaxAttempts - 1; i++)
            Assert.Equal(VerifyResult.InvalidOrExpired, await svc.VerifyCodeAsync(Email, "000000"));

        // The attempt that reaches the cap reports lockout...
        Assert.Equal(VerifyResult.LockedOut, await svc.VerifyCodeAsync(Email, "000000"));
        // ...and even the correct code is refused while locked out.
        Assert.Equal(VerifyResult.LockedOut, await svc.VerifyCodeAsync(Email, sender.LastCode!));
    }

    [Fact]
    public async Task VerifyCode_ExpiredCode_IsRejected_AndDiscarded()
    {
        var store = new InMemoryLoginCodeStore();
        var sender = new CapturingEmailSender();
        var svc = NewService(store, sender);

        await svc.RequestCodeAsync(Email);
        // Back-date the stored expiry.
        store.Peek(Key)!.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);

        Assert.Equal(VerifyResult.InvalidOrExpired, await svc.VerifyCodeAsync(Email, sender.LastCode!));
        Assert.Null(store.Peek(Key)); // discarded
    }

    [Fact]
    public async Task RequestCode_WithinThrottleWindow_DoesNotResend()
    {
        var store = new InMemoryLoginCodeStore();
        var sender = new CapturingEmailSender();
        var svc = NewService(store, sender);

        await svc.RequestCodeAsync(Email);
        await svc.RequestCodeAsync(Email); // immediately again → inside 30s window

        Assert.Equal(1, sender.SendCount);
    }

    [Fact]
    public async Task VerifyCode_NoPendingCode_IsInvalid()
    {
        var svc = NewService(new InMemoryLoginCodeStore(), new CapturingEmailSender());
        Assert.Equal(VerifyResult.InvalidOrExpired, await svc.VerifyCodeAsync(Email, "123456"));
    }
}
