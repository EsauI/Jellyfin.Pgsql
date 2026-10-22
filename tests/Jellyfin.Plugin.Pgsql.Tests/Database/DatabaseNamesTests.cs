using System.Linq;
using System.Text;
using Jellyfin.Plugin.Pgsql.Database;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests.Database;

public class DatabaseNamesTests
{
    private const int MaxBytes = 63;

    [Fact]
    public void Derive_ShortName_KeepsTheReadableForm()
    {
        Assert.Equal("jellyfin_backup_20260101000000", DatabaseNames.Backup("jellyfin", "20260101000000"));
        Assert.Equal("jellyfin_restore_20260101000000", DatabaseNames.Restore("jellyfin", "20260101000000"));
        Assert.Equal("jellyfin_failed_20260101000000_2", DatabaseNames.Failed("jellyfin", "20260101000000_2"));
    }

    [Fact]
    public void Derive_LongName_StaysWithinTheIdentifierLimitAndStaysDistinct()
    {
        var database = new string('a', 62);
        const string Stamp = "20260101000000";

        var names = new[]
        {
            database,
            DatabaseNames.Backup(database, Stamp),
            DatabaseNames.Backup(database, Stamp + "_1"),
            DatabaseNames.Restore(database, Stamp),
            DatabaseNames.Failed(database, Stamp),
        };

        Assert.All(names, name => Assert.True(Encoding.UTF8.GetByteCount(name) <= MaxBytes, name));
        Assert.Equal(names.Length, names.Distinct(System.StringComparer.Ordinal).Count());
        Assert.Equal(DatabaseNames.Backup(database, Stamp), DatabaseNames.Backup(database, Stamp));
    }

    [Fact]
    public void Derive_LongNamesSharingAPrefix_DoNotCollide()
    {
        var prefix = new string('a', 58);

        var first = DatabaseNames.Backup(prefix + "_one", "20260101000000");
        var second = DatabaseNames.Backup(prefix + "_two", "20260101000000");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Derive_LongMultiByteName_NeverSplitsACharacter()
    {
        var database = string.Concat(Enumerable.Repeat("ñandú", 14));

        var name = DatabaseNames.Backup(database, "20260101000000");

        Assert.True(Encoding.UTF8.GetByteCount(name) <= MaxBytes);
        Assert.DoesNotContain('�', name);
    }

    [Theory]
    [InlineData("jellyfin", "\"jellyfin\"")]
    [InlineData("Mixed Case-Name", "\"Mixed Case-Name\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("x\"; DROP DATABASE y; --", "\"x\"\"; DROP DATABASE y; --\"")]
    public void Quote_EscapesEmbeddedQuotes(string identifier, string expected)
        => Assert.Equal(expected, DatabaseNames.Quote(identifier));
}
