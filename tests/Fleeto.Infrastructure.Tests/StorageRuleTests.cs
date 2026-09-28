using Fleeto.Core.Domain;
using Fleeto.Core.Entities;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Rules of storage analysis (0.6.0): folder paths, the scan growth is measured against, and how growth finds the folder that
/// grew most without being fooled by a folder the older scan did not list.
/// </summary>
public sealed class StorageRuleTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(@"C:\Users\Public", @"C:\Users")]
    [InlineData(@"C:\Users", @"C:\")]
    [InlineData(@"C:\Users\", @"C:\")]
    [InlineData(@"C:\", null)]
    [InlineData("/var/log", "/var")]
    [InlineData("/var", "/")]
    [InlineData("/var/", "/")]
    [InlineData("/", null)]
    [InlineData("/data/backups", "/data")]
    public void The_parent_of_a_path_follows_its_separator(string path, string? parent) =>
        Assert.Equal(parent, StorageRules.ParentPath(path));

    [Theory]
    [InlineData(@"C:\Users\Public", "Public")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData("/var/log", "log")]
    [InlineData("/", "/")]
    public void A_folder_is_named_by_its_last_part(string path, string name) => Assert.Equal(name, StorageRules.FolderName(path));

    [Fact]
    public void The_baseline_is_the_newest_scan_at_least_the_period_old_with_some_tolerance()
    {
        var scans = new[] { Now.AddDays(-10), Now.AddDays(-7).AddHours(3), Now.AddDays(-3), Now.AddDays(-1) }.Select(t => new Stamp(t)).ToList();

        // Seven days minus three hours is within the tolerance of six hours.
        Assert.Equal(Now.AddDays(-7).AddHours(3), StorageRules.Baseline(scans, s => s.At, Now, TimeSpan.FromDays(7))!.At);
        Assert.Equal(Now.AddDays(-1), StorageRules.Baseline(scans, s => s.At, Now, TimeSpan.FromDays(1))!.At);
    }

    [Fact]
    public void Without_a_scan_as_old_as_the_period_the_oldest_of_a_day_or_older_is_the_baseline()
    {
        var scans = new[] { Now.AddDays(-3), Now.AddDays(-2), Now.AddHours(-10) }.Select(t => new Stamp(t)).ToList();

        Assert.Equal(Now.AddDays(-3), StorageRules.Baseline(scans, s => s.At, Now, TimeSpan.FromDays(30))!.At);
        Assert.Null(StorageRules.Baseline([new Stamp(Now.AddHours(-10))], s => s.At, Now, TimeSpan.FromDays(7)));
        Assert.Null(StorageRules.Baseline(Array.Empty<Stamp>(), s => s.At, Now, TimeSpan.FromDays(7)));
    }

    [Fact]
    public void Growth_follows_the_child_that_holds_at_least_half_of_the_growth_of_its_parent()
    {
        var before = Scan(Now.AddDays(-7),
            (@"C:\", 100), (@"C:\SQL", 40), (@"C:\SQL\Backup", 30), (@"C:\SQL\Data", 10), (@"C:\Users", 50));
        var after = Scan(Now,
            (@"C:\", 150), (@"C:\SQL", 85), (@"C:\SQL\Backup", 72), (@"C:\SQL\Data", 13), (@"C:\Users", 55));

        var growth = StorageRules.Growth(after, before)!;

        Assert.Equal(50 * Gb, growth.GrowthBytes);
        Assert.Equal(@"C:\SQL\Backup", growth.Folder);
        Assert.Equal(42 * Gb, growth.FolderGrowthBytes);
        Assert.Equal(7, growth.Days);
        Assert.Equal(@"+50 GB in 7 days, most in C:\SQL\Backup (+42 GB)", StorageRules.GrowthDetail(growth));
    }

    [Fact]
    public void Growth_spread_over_folders_names_no_folder()
    {
        var before = Scan(Now.AddDays(-7), (@"C:\", 100), (@"C:\A", 30), (@"C:\B", 30), (@"C:\C", 30));
        var after = Scan(Now, (@"C:\", 130), (@"C:\A", 40), (@"C:\B", 40), (@"C:\C", 40));

        var growth = StorageRules.Growth(after, before)!;

        Assert.Null(growth.Folder);
        Assert.Equal("+30 GB in 7 days", StorageRules.GrowthDetail(growth));
    }

    [Fact]
    public void A_folder_the_older_scan_did_not_list_is_not_taken_for_new_growth()
    {
        // C:\Big was below the cut-off a week ago; its whole size must not count as growth.
        var before = Scan(Now.AddDays(-7), (@"C:\", 100), (@"C:\Data", 60));
        var after = Scan(Now, (@"C:\", 110), (@"C:\Data", 62), (@"C:\Big", 40));

        var growth = StorageRules.Growth(after, before)!;

        Assert.Equal(10 * Gb, growth.GrowthBytes);
        Assert.Null(growth.Folder);
    }

    [Fact]
    public void Shrinking_is_negative_growth_and_other_roots_are_not_compared()
    {
        var before = Scan(Now.AddDays(-7), (@"C:\", 100));
        Assert.Equal(-20 * Gb, StorageRules.Growth(Scan(Now, (@"C:\", 80)), before)!.GrowthBytes);
        Assert.Equal("-20 GB in 7 days", StorageRules.GrowthDetail(StorageRules.Growth(Scan(Now, (@"C:\", 80)), before)!));
        Assert.Null(StorageRules.Growth(Scan(Now, (@"D:\", 80)), before));
        Assert.Null(StorageRules.Growth(new StorageScanPoint(Now, []), before));
    }

    [Fact]
    public void Stored_folders_and_files_survive_a_round_trip_and_damage_reads_as_empty()
    {
        var folders = new[] { new StorageFolderEntry(@"C:\", 10 * Gb, 100, 10) };
        var files = new[] { new StorageFileEntry(@"C:\big.iso", 4 * Gb, Now) };

        Assert.Equal(folders, StorageRules.ParseFolders(StorageRules.SerializeFolders(folders)));
        Assert.Equal(files, StorageRules.ParseFiles(StorageRules.SerializeFiles(files)));
        Assert.Contains("\"p\":", StorageRules.SerializeFolders(folders));
        Assert.Empty(StorageRules.ParseFolders("{not json"));
        Assert.Empty(StorageRules.ParseFiles(null));
    }

    [Theory]
    [InlineData("0.6.0-alpha.9", false)]
    [InlineData("0.6.0-alpha.10", true)]
    [InlineData("0.6.0", true)]
    [InlineData("0.7.1", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_an_agent_of_this_release_or_newer_scans_storage(string? version, bool supported) =>
        Assert.Equal(supported, StorageRules.AgentSupportsStorageScan(version));

    [Fact]
    public void The_folder_growth_check_is_evaluated_by_Fleeto_after_every_storage_scan()
    {
        var info = CheckCatalog.Get(CheckType.FolderGrowth);

        Assert.False(info.RunsOnAgent);
        Assert.Equal("after every storage scan", info.EvaluatedWhen);
        Assert.Equal("GB", CheckCatalog.UnitOf(CheckType.FolderGrowth, new Dictionary<string, string>()));
        Assert.Equal("*", CheckCatalog.ParameterOrDefault(CheckType.FolderGrowth, new Dictionary<string, string>(), "drive"));
        Assert.Equal("7", CheckCatalog.ParameterOrDefault(CheckType.FolderGrowth, new Dictionary<string, string>(), "period_days"));
    }

    [Fact]
    public void Only_the_offered_scan_intervals_are_valid()
    {
        Assert.All(StorageRules.ScanIntervalChoices, h => Assert.True(StorageRules.IsValidScanInterval(h)));
        Assert.Contains(StorageRules.DefaultScanIntervalHours, StorageRules.ScanIntervalChoices);
        Assert.False(StorageRules.IsValidScanInterval(1));
        Assert.False(StorageRules.IsValidScanInterval(-1));
    }

    private static StorageScanPoint Scan(DateTime at, params (string Path, long Gigabytes)[] folders) =>
        new(at, folders.Select(f => new StorageFolderEntry(f.Path, f.Gigabytes * Gb, 0, 0)).ToList());

    private sealed record Stamp(DateTime At);
}
