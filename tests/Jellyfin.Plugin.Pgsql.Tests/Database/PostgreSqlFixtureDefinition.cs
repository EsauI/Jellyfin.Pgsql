using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests.Database;

[CollectionDefinition(Name)]
public sealed class PostgreSqlFixtureDefinition : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSQL";
}
