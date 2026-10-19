using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Plugin.Pgsql.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Pgsql.Tests.Database;

public class PostgreSqlDatabaseProviderTests
{
    private const string ValidConnectionString = "Host=db.example;Port=5433;Database=jellyfin;Username=jf;Password=s3cret-value";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Initialise_BlankConnectionString_ThrowsActionableError(string? connectionString)
    {
        var provider = CreateProvider();
        var configuration = CreateConfiguration(connectionString);

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.Initialise(new DbContextOptionsBuilder(), configuration));

        Assert.Contains("database.xml", exception.Message, StringComparison.Ordinal);
        Assert.Contains("CustomProviderOptions/ConnectionString", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Initialise_MissingCustomProviderOptions_ThrowsActionableError()
    {
        var provider = CreateProvider();
        var configuration = new DatabaseConfigurationOptions { DatabaseType = "PLUGIN_PROVIDER" };

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.Initialise(new DbContextOptionsBuilder(), configuration));

        Assert.Contains("CustomProviderOptions/ConnectionString", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Initialise_InvalidConnectionString_ThrowsWithoutLeakingIt()
    {
        var provider = CreateProvider();
        var configuration = CreateConfiguration("this is not a connection string;Password=topsecret");

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.Initialise(new DbContextOptionsBuilder(), configuration));

        Assert.Contains("not a valid PostgreSQL connection string", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("topsecret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Initialise_ValidConnectionString_ConfiguresNpgsqlWithPluginMigrationsAssembly()
    {
        var provider = CreateProvider();
        var builder = new DbContextOptionsBuilder();

        provider.Initialise(builder, CreateConfiguration(ValidConnectionString));

        var npgsql = builder.Options.Extensions.OfType<RelationalOptionsExtension>().Single();
        Assert.Contains("db.example", npgsql.ConnectionString, StringComparison.Ordinal);
        Assert.Contains("5433", npgsql.ConnectionString, StringComparison.Ordinal);
        Assert.Equal(typeof(PostgreSqlDatabaseProvider).Assembly.GetName().Name, npgsql.MigrationsAssembly);
    }

    [Fact]
    public void OnModelCreating_SetsNoDatabaseLevelCollation()
    {
        var model = BuildModel(CreateProvider());

        Assert.Null(model.GetCollation());
        Assert.Null(model.FindAnnotation("Relational:Collation"));
    }

    [Fact]
    public void ConfigureConventions_AppliesUtcConverterToDateTimeAndNullableDateTime()
    {
        var model = BuildModel(CreateProvider());
        var entity = model.FindEntityType(typeof(Sample))!;

        Assert.IsType<UtcDateTimeConverter>(entity.FindProperty(nameof(Sample.Created))!.GetValueConverter());
        Assert.IsType<UtcDateTimeConverter>(entity.FindProperty(nameof(Sample.Updated))!.GetValueConverter());
    }

    [Fact]
    public async Task BackupOperations_AreNotSupportedYet()
    {
        var provider = CreateProvider();

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.MigrationBackupFast(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.RestoreBackupFast("key", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DeleteBackup("key"));
    }

    private static PostgreSqlDatabaseProvider CreateProvider() => new(NullLogger<PostgreSqlDatabaseProvider>.Instance);

    private static DatabaseConfigurationOptions CreateConfiguration(string? connectionString) => new()
    {
        DatabaseType = "PLUGIN_PROVIDER",
        CustomProviderOptions = new CustomDatabaseOptions
        {
            PluginName = Plugin.PluginName,
            PluginAssembly = "Jellyfin.Plugin.Pgsql",
            ConnectionString = connectionString!,
        },
    };

    private static IModel BuildModel(PostgreSqlDatabaseProvider provider)
    {
        var builder = new DbContextOptionsBuilder<SampleContext>();
        provider.Initialise(builder, CreateConfiguration(ValidConnectionString));
        using var context = new SampleContext(builder.Options, provider);
        return context.GetService<IDesignTimeModel>().Model;
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Only instantiated by EF Core.")]
    private sealed class Sample
    {
        public int Id { get; set; }

        public DateTime Created { get; set; }

        public DateTime? Updated { get; set; }
    }

    private sealed class SampleContext(DbContextOptions<SampleContext> options, PostgreSqlDatabaseProvider provider) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            provider.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Sample>();
        }

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            provider.ConfigureConventions(configurationBuilder);
        }
    }
}
