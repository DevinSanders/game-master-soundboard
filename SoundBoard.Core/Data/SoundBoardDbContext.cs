using Microsoft.EntityFrameworkCore;
using SoundBoard.Core.Models;
using SoundBoard.Core.Services;
using System;
using System.IO;

namespace SoundBoard.Core.Data;

/// <summary>
/// EF Core context backing the user's library — tracks, presets, playlists,
/// soundboard pages, and the buttons on them. The database path is resolved
/// lazily from <see cref="ISettingsService"/> so the user can switch
/// libraries at runtime. There are no EF migrations; schema upgrades are
/// raw-SQL ALTER blocks in <c>App.axaml.cs</c> after <c>EnsureCreated()</c>.
/// </summary>
public class SoundBoardDbContext : DbContext
{
    private readonly ISettingsService? _settingsService;

    public DbSet<Track> Tracks { get; set; }
    public DbSet<Preset> Presets { get; set; }
    public DbSet<PresetTrack> PresetTracks { get; set; }
    public DbSet<Playlist> Playlists { get; set; }
    public DbSet<ShortcutPage> ShortcutPages { get; set; }
    public DbSet<ShortcutButton> ShortcutButtons { get; set; }
    public DbSet<PlaylistItem> PlaylistItems { get; set; }
    public DbSet<SamplerAttachment> SamplerAttachments { get; set; }
    public DbSet<Bus> Buses { get; set; }
    
    public SoundBoardDbContext()
    {
    }

    public SoundBoardDbContext(DbContextOptions<SoundBoardDbContext> options, ISettingsService settingsService) : base(options)
    {
        _settingsService = settingsService;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            string dbPath;
            // Use whatever path the user has chosen, even if the file doesn't
            // exist yet — EF Core's EnsureCreated will materialize a fresh
            // SQLite file (and schema) at that path on first use. The old
            // File.Exists check silently fell back to the default library
            // whenever a freshly-created library entry hadn't been opened yet.
            if (!string.IsNullOrEmpty(_settingsService?.Current.CurrentLibraryPath))
            {
                dbPath = _settingsService.Current.CurrentLibraryPath;
            }
            else
            {
                // First-run default: a "default" library inside the Libraries
                // folder. Putting it there (instead of the root) keeps it
                // discoverable in the Open-Library list without any special
                // casing — it just shows up as an entry named "default".
                dbPath = AppPaths.DefaultDatabasePath;

                // Persist so the next launch picks it up.
                if (_settingsService != null)
                {
                    _settingsService.Current.CurrentLibraryPath = dbPath;
                    _settingsService.Save();
                }
            }

            // Cross-platform safety: a settings.json copied from another OS
            // can carry a foreign-format path (e.g. Windows "D:\\..." moved
            // to macOS). On Unix that string contains no '/', so
            // Path.GetDirectoryName returns "" and Directory.CreateDirectory
            // throws ArgumentException — preventing the app from launching
            // at all. Detect that case, log it, and silently reset to the
            // platform-native default so the user can recover by
            // re-importing their library, instead of having to hand-edit
            // settings.json from the terminal.
            string? dbDir = Path.GetDirectoryName(dbPath);
            if (string.IsNullOrEmpty(dbDir))
            {
                var fallback = AppPaths.DefaultDatabasePath;
                Logging.Log.Warn("Db",
                    $"Configured library path '{dbPath}' has no resolvable parent on this " +
                    $"platform — falling back to {fallback}. (Most likely a settings.json " +
                    $"copied from another OS.)");
                dbPath = fallback;
                if (_settingsService != null)
                {
                    _settingsService.Current.CurrentLibraryPath = dbPath;
                    _settingsService.Save();
                }
                dbDir = Path.GetDirectoryName(dbPath);
            }

            Directory.CreateDirectory(dbDir!);
            optionsBuilder.UseSqlite($"Data Source={dbPath}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<PlaylistItem>()
            .HasOne(p => p.Track)
            .WithMany()
            .HasForeignKey(p => p.TrackId);

        modelBuilder.Entity<PlaylistItem>()
            .HasOne(p => p.Preset)
            .WithMany()
            .HasForeignKey(p => p.PresetId);

        modelBuilder.Entity<PresetTrack>()
            .HasOne(pt => pt.Preset)
            .WithMany(p => p.Tracks)
            .HasForeignKey(pt => pt.PresetId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PresetTrack>()
            .HasOne(pt => pt.Track)
            .WithMany()
            .HasForeignKey(pt => pt.TrackId);

        modelBuilder.Entity<ShortcutButton>()
            .HasOne(b => b.ShortcutPage)
            .WithMany(p => p.Buttons)
            .HasForeignKey(b => b.ShortcutPageId);

        modelBuilder.Entity<ShortcutButton>()
            .HasOne(b => b.Track)
            .WithMany()
            .HasForeignKey(b => b.TrackId);

        modelBuilder.Entity<ShortcutButton>()
            .HasOne(b => b.Preset)
            .WithMany()
            .HasForeignKey(b => b.PresetId);

        modelBuilder.Entity<ShortcutButton>()
            .HasOne(b => b.Playlist)
            .WithMany()
            .HasForeignKey(b => b.PlaylistId);
    }
}
