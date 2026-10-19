using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Query;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// Plugs <see cref="CaseInsensitiveLikeTranslator"/> into EF Core's method-call translation.
/// </summary>
/// <param name="sqlExpressionFactory">The provider's expression factory.</param>
internal sealed class CaseInsensitiveLikeTranslatorPlugin(ISqlExpressionFactory sqlExpressionFactory) : IMethodCallTranslatorPlugin
{
    /// <inheritdoc/>
    public IEnumerable<IMethodCallTranslator> Translators { get; } = [new CaseInsensitiveLikeTranslator(sqlExpressionFactory)];
}
