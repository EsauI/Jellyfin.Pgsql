using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.Pgsql.Database;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests.Database;

public class PostgreSqlBackupServiceTests
{
    // The host cannot be resolved: any attempt to reach the server would fail with an NpgsqlException instead of
    // the argument error these tests expect.
    private readonly NpgsqlConnectionStringBuilder _connection = new() { Host = "unreachable.invalid", Database = "jellyfin", Username = "jf" };

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("../x")]
    [InlineData("20260101000000\"; DROP DATABASE x; --")]
    [InlineData("nonsense")]
    [InlineData("")]
    [InlineData("20260101000000\n")]
    [InlineData("٢٠٢٦٠١٠١٠٠٠٠٠٠")]
    public async Task Restore_KeyNotCreatedByTheProvider_ThrowsBeforeAnySql(string key)
    {
        var service = new PostgreSqlBackupService(NullLogger.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RestoreAsync(_connection, key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Restore_EmptyBackupKey_IsANoOp()
    {
        var service = new PostgreSqlBackupService(NullLogger.Instance);

        await service.RestoreAsync(_connection, PostgreSqlBackupService.NoBackupKey, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(PostgreSqlBackupService.NoBackupKey)]
    [InlineData("../escape")]
    [InlineData("20260101000000\"; DROP DATABASE x; --")]
    public async Task Delete_KeyThatIsNotABackup_DoesNothingAndDoesNotThrow(string key)
        => await new PostgreSqlBackupService(NullLogger.Instance).DeleteAsync(_connection, key, TestContext.Current.CancellationToken);
}
