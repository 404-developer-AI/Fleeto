using Fleeto.Core.Domain;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Infrastructure.Services;

/// <summary>
/// Reads the instance-wide choice of patch management (0.9.0, <see cref="PatchManagement"/>) from the database. Web, the workers and
/// fleeto-signer each read it themselves, so the signer never takes the word of web for it.
/// </summary>
public static class PatchManagers
{
    public static async Task<PatchManager> GetAsync(FleetoDbContext db, CancellationToken cancellationToken) =>
        PatchManagement.Parse(await db.Settings.AsNoTracking()
            .Where(s => s.Key == PatchManagement.SettingKey && !s.IsEncrypted)
            .Select(s => s.Value)
            .SingleOrDefaultAsync(cancellationToken));
}
