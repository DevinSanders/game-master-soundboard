using Microsoft.EntityFrameworkCore;
using SoundBoard.Core.Data;

namespace SoundBoard.Core.Services;

/// <summary>
/// Hands out fresh <see cref="SoundBoardDbContext"/> instances on demand,
/// one per logical operation. Use this in preference to constructor-injecting
/// the context directly when:
///
///   - the operation mutates data (CRUD), or
///   - the consumer's lifetime might outlive a transient failure (so a
///     poisoned change tracker on the shared context can't bleed in).
///
/// Pattern:
/// <code>
///     using var db = _factory.CreateDbContext();
///     db.Tracks.Update(track);
///     db.SaveChanges();
/// </code>
/// </summary>
public interface ISoundBoardDbContextFactory
{
    SoundBoardDbContext CreateDbContext();
}

/// <inheritdoc cref="ISoundBoardDbContextFactory"/>
public sealed class SoundBoardDbContextFactory : ISoundBoardDbContextFactory
{
    private readonly ISettingsService _settings;

    public SoundBoardDbContextFactory(ISettingsService settings)
    {
        _settings = settings;
    }

    public SoundBoardDbContext CreateDbContext()
    {
        // SoundBoardDbContext.OnConfiguring reads the current library path
        // off the settings service, so all we need is to construct the
        // context and let it self-configure. Each call returns a brand-new
        // context with its own change tracker.
        var options = new DbContextOptionsBuilder<SoundBoardDbContext>().Options;
        return new SoundBoardDbContext(options, _settings);
    }
}
