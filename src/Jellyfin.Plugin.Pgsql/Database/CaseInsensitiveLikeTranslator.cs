using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query;

namespace Jellyfin.Plugin.Pgsql.Database;

/// <summary>
/// Translates <c>EF.Functions.Like</c> to PostgreSQL <c>ILIKE</c>. SQLite's LIKE ignores ASCII case and Jellyfin relies on it
/// for OriginalTitle and activity log searches, while PostgreSQL's LIKE is always case-sensitive.
/// </summary>
internal sealed class CaseInsensitiveLikeTranslator : IMethodCallTranslator
{
    private readonly NpgsqlSqlExpressionFactory _sqlExpressionFactory;

    public CaseInsensitiveLikeTranslator(ISqlExpressionFactory sqlExpressionFactory)
    {
        _sqlExpressionFactory = sqlExpressionFactory as NpgsqlSqlExpressionFactory
            ?? throw new InvalidOperationException("The PostgreSQL provider's SQL expression factory is required to translate LIKE.");
    }

    /// <inheritdoc/>
    public SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(arguments);

        if (method.DeclaringType != typeof(DbFunctionsExtensions) || method.Name != nameof(DbFunctionsExtensions.Like))
        {
            return null;
        }

        // Arguments are (DbFunctions, matchExpression, pattern[, escapeCharacter]).
        return arguments.Count switch
        {
            3 => _sqlExpressionFactory.ILike(arguments[1], arguments[2]),
            4 => _sqlExpressionFactory.ILike(arguments[1], arguments[2], arguments[3]),
            _ => null,
        };
    }
}
