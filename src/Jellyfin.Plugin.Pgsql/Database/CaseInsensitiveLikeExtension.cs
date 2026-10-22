using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// Registers <see cref="CaseInsensitiveLikeTranslatorPlugin"/> with the context's internal service provider.
/// </summary>
internal sealed class CaseInsensitiveLikeExtension : IDbContextOptionsExtension
{
    /// <inheritdoc/>
    public DbContextOptionsExtensionInfo Info => field ??= new ExtensionInfo(this);

    /// <inheritdoc/>
    public void ApplyServices(IServiceCollection services)
    {
        // Plugin translators are consulted before the provider's own, so this wins over Npgsql's case-sensitive LIKE.
        services.AddScoped<IMethodCallTranslatorPlugin, CaseInsensitiveLikeTranslatorPlugin>();
    }

    /// <inheritdoc/>
    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using case-insensitive LIKE ";

        // The extension has no state, so every instance yields the same services and shares one cached provider.
        public override int GetServiceProviderHashCode() => typeof(CaseInsensitiveLikeExtension).GetHashCode();

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            ArgumentNullException.ThrowIfNull(debugInfo);
            debugInfo["Pgsql:CaseInsensitiveLike"] = "1";
        }
    }
}
