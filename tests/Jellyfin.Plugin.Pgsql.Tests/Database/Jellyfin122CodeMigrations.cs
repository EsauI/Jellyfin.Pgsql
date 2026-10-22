using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Pgsql.Tests.Database;

/// <summary>
/// The code migrations Jellyfin 12.2.0 records in the migration history of a fresh install before it applies any EF migration.
/// </summary>
/// <remarks>
/// This list mirrors Jellyfin 12.2: every <c>[JellyfinMigration]</c> without <c>RunMigrationOnSetup</c>, seeded by
/// <c>JellyfinMigrationService.PrepareSystemForMigration</c>. Ids are built like <c>CodeMigration.BuildCodeMigrationId</c>
/// (<c>Order</c> formatted as <c>yyyyMMddHHmmsss</c>, an underscore and the migration name).
/// </remarks>
internal static class Jellyfin122CodeMigrations
{
    private static readonly (string Order, string Name)[] Migrations =
    [
        ("2026-01-13T23:00:00", "CleanupOrphanedExtras"),
        ("2026-06-10T12:00:00", "RefreshCleanNamesAndValues"),
        ("2026-09-15T10:43:05", "FixNullEncoderPreset"),
        ("2025-04-20T14:00:00", "MigrateAuthenticationDb"),
        ("2025-06-20T18:00:00", "FixDates"),
        ("2025-04-20T19:30:00", "MigrateLibraryDbCompatibilityCheck"),
        ("2026-09-15T12:00:00", "MigrateRatingLevels"),
        ("2025-04-20T10:00:00", "MigrateUserDb"),
        ("2025-07-30T21:50:00", "ReseedFolderFlag"),
        ("2025-04-20T15:00:00", "FixPlaylistOwner"),
        ("2026-05-22T09:23:04", "UpdateNormalizedUsername"),
        ("2026-08-31T10:00:00", "EnableLocalSimilarityProviders"),
        ("2026-05-25T01:00:00", "CleanupOrphanedExternalData"),
        ("2025-04-20T21:00:00", "MoveExtractedFiles"),
        ("2025-04-20T06:00:00", "CreateUserLoggingConfigFile"),
        ("2026-09-11T12:00:00", "StripEmbeddedLinkedChildren"),
        ("2026-05-31T16:00:00", "DisableLegacyAuthorization"),
        ("2026-08-25T20:00:00", "ConsolidateLocalizedUserViews"),
        ("2025-04-20T08:00:00", "RemoveDuplicateExtras"),
        ("2025-04-21T00:00:00", "MigrateKeyframeData"),
        ("2025-06-18T01:00:00", "MigrateLibraryUserData"),
        ("2025-10-09T20:00:00", "CleanMusicArtist"),
        ("2026-09-12T12:00:00", "HarmonizeConflictingUserData"),
        ("2025-04-20T12:00:00", "MigrateDisplayPreferencesDb"),
        ("2025-04-20T01:00:00", "MigrateNetworkConfiguration"),
        ("2026-01-15T12:00:00", "FixIncorrectOwnerIdRelationships"),
        ("2025-04-20T05:00:00", "DisableTranscodingThrottling"),
        ("2025-04-20T04:00:00", "RenameEnableGroupingIntoCollections"),
        ("2026-01-13T12:00:00", "MigrateLinkedChildren"),
        ("2026-05-08T13:00:00", "MergeDuplicatePeople"),
        ("2025-04-20T23:00:00", "RefreshInternalDateModified"),
        ("2025-04-20T18:00:00", "FixAudioData"),
        ("2026-07-29T12:00:00", "RestorePlaylistChildrenFromMetadata"),
        ("2025-04-20T20:00:00", "MigrateLibraryDb"),
        ("2025-04-20T13:00:00", "RemoveDownloadImagesInAdvance"),
        ("2025-04-20T00:00:00", "CreateNetworkConfiguration"),
        ("2026-09-08T12:00:00", "RepairAlternateVersionLinks"),
        ("2025-04-20T03:00:00", "MigrateEncodingOptions"),
        ("2026-08-21T12:00:00", "RecomputeSeriesPresentationKey"),
        ("2026-05-08T12:00:00", "MergeDuplicateMusicArtists"),
        ("2026-02-06T20:00:00", "FixLibrarySubtitleDownloadLanguages"),
        ("2025-04-20T07:00:00", "MigrateActivityLogDb"),
        ("2026-07-22T12:00:00", "RefreshForcedSortNames"),
        ("2025-04-20T02:00:00", "MigrateMusicBrainzTimeout"),
    ];

    /// <summary>
    /// Gets the ids seeded on a fresh install, in ordinal order.
    /// </summary>
    public static IReadOnlyList<string> SeededIds { get; } = Migrations
        .Select(migration => DateTime.Parse(migration.Order, CultureInfo.InvariantCulture).ToString("yyyyMMddHHmmsss", CultureInfo.InvariantCulture) + "_" + migration.Name)
        .Order(StringComparer.Ordinal)
        .ToArray();
}
