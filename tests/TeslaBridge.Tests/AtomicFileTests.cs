using TeslaBridge.Storage;

namespace TeslaBridge.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("atomic").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Writes_whole_files_and_secrets_are_owner_only_from_the_start()
    {
        var file = Path.Combine(_dir, "sub", "tokens.json");
        AtomicFile.WriteAllText(file, "one", secret: true);
        AtomicFile.WriteAllText(file, "two", secret: true);
        Assert.Equal("two", File.ReadAllText(file));
        Assert.False(File.Exists(file + ".tmp"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    [Fact]
    public void A_symlink_in_the_way_is_replaced_not_written_through()
    {
        if (OperatingSystem.IsWindows()) return;
        var victim = Path.Combine(_dir, "victim.txt");
        File.WriteAllText(victim, "keep me");
        var file = Path.Combine(_dir, "settings.json");
        File.CreateSymbolicLink(file, victim);

        AtomicFile.WriteAllText(file, "{}");
        Assert.Equal("keep me", File.ReadAllText(victim));
        Assert.Null(new FileInfo(file).LinkTarget);
    }

    [Fact]
    public void A_broken_file_is_kept_aside()
    {
        var file = Path.Combine(_dir, "state.json");
        File.WriteAllText(file, "{ not json");
        var moved = AtomicFile.Quarantine(file, new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.Parse("2026-11-13T20:01:02Z")));
        Assert.Equal(file + ".corrupt-20261113T200102Z", moved);
        Assert.False(File.Exists(file));
        Assert.Equal("{ not json", File.ReadAllText(moved!));
    }
}
