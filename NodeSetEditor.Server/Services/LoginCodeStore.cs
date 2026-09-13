using Microsoft.EntityFrameworkCore;
using NodeSetEditor.Model;

namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// Persistence for pending email login codes, abstracted so the verification logic
    /// (throttle / lockout / expiry / single-use) can be unit-tested without a database.
    /// Keyed by the normalized (lowercased) email.
    /// </summary>
    public interface ILoginCodeStore
    {
        Task<LoginCode?> FindAsync(string email, CancellationToken ct = default);
        /// <summary>Inserts or updates the row for <c>entry.Email</c>.</summary>
        Task SaveAsync(LoginCode entry, CancellationToken ct = default);
        Task RemoveAsync(string email, CancellationToken ct = default);
    }

    /// <summary>EF Core / PostgreSQL implementation backed by <see cref="NodeSetEditorDbContext"/>.</summary>
    public sealed class DbLoginCodeStore : ILoginCodeStore
    {
        private readonly NodeSetEditorDbContext _db;

        public DbLoginCodeStore(NodeSetEditorDbContext db) => _db = db;

        public Task<LoginCode?> FindAsync(string email, CancellationToken ct = default)
            => _db.LoginCodes.FirstOrDefaultAsync(c => c.Email == email, ct);

        public async Task SaveAsync(LoginCode entry, CancellationToken ct = default)
        {
            var tracked = await _db.LoginCodes.FirstOrDefaultAsync(c => c.Email == entry.Email, ct);
            if (tracked == null)
            {
                _db.LoginCodes.Add(entry);
            }
            else if (!ReferenceEquals(tracked, entry))
            {
                // Caller passed a fresh instance for an existing key — copy the mutable fields.
                tracked.CodeHash = entry.CodeHash;
                tracked.Salt = entry.Salt;
                tracked.ExpiresAt = entry.ExpiresAt;
                tracked.Attempts = entry.Attempts;
                tracked.SentAt = entry.SentAt;
            }
            await _db.SaveChangesAsync(ct);
        }

        public async Task RemoveAsync(string email, CancellationToken ct = default)
        {
            var row = await _db.LoginCodes.FirstOrDefaultAsync(c => c.Email == email, ct);
            if (row != null)
            {
                _db.LoginCodes.Remove(row);
                await _db.SaveChangesAsync(ct);
            }
        }
    }
}
