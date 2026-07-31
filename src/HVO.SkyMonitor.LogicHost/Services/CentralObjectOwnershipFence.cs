using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal static class CentralObjectOwnershipFence
{
    internal const string Bucket = "skymonitor-artifacts";
    internal const string BucketPrefix = "minio://skymonitor-artifacts/";
    internal const string BinaryCollation = "Latin1_General_100_BIN2";

    public static async Task<bool> IsRetiredAsync(
        ApplicationDbContext db,
        string storageReference,
        CancellationToken cancellationToken)
    {
        if (await db.CentralArtifacts.AsNoTracking().AnyAsync(artifact =>
                (artifact.RetentionDeletionToken != null
                    || artifact.ObjectState == CentralArtifactObjectState.Expired)
                && EF.Functions.Collate(artifact.StorageReference, BinaryCollation) == storageReference,
                cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        if (storageReference.StartsWith(BucketPrefix, StringComparison.Ordinal)
            && storageReference.Length > BucketPrefix.Length)
        {
            var objectKey = storageReference[BucketPrefix.Length..];
            var objectKeyIdentity = CreateObjectKeyIdentity(objectKey);
            var dispositionKeys = await db.CentralObjectRecoveryDispositions.AsNoTracking()
                .Where(disposition => disposition.SourceObjectIdentitySha256 == objectKeyIdentity
                    && disposition.Kind == CentralObjectRecoveryKinds.ExpiredDelete
                    && (disposition.OperationToken != null
                        || disposition.State == CentralObjectRecoveryStates.Completed))
                .Select(disposition => disposition.SourceObjectKey)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            if (dispositionKeys.Any(key => string.Equals(key, objectKey, StringComparison.Ordinal)))
            {
                return true;
            }
            if (dispositionKeys.Length != 0)
            {
                throw new InvalidOperationException("A retention tombstone object-key identity collision was detected.");
            }
        }
        var storageReferenceSha256 = SHA256.HashData(Encoding.Unicode.GetBytes(storageReference));
        return await db.CentralTransientDerivativeOutputIntents.AsNoTracking().AnyAsync(intent =>
            EF.Property<byte[]>(intent, "StorageReferenceSha256") == storageReferenceSha256
            && EF.Functions.Collate(intent.StorageReference, BinaryCollation) == storageReference
            && intent.ObjectState == CentralArtifactObjectState.Expired,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> HasActiveOwnerAsync(
        ApplicationDbContext db,
        string storageReference,
        Guid centralArtifactId,
        CancellationToken cancellationToken)
    {
        var activeArtifactOwner = await db.Database.SqlQuery<int>($"""
                SELECT TOP(1) CAST(1 AS int) AS [Value]
                FROM [CentralArtifacts] WITH (INDEX([IX_CentralArtifacts_StorageReference]))
                WHERE [StorageReference] = {storageReference}
                  AND [Id] != {centralArtifactId}
                  AND [ObjectState] != N'Expired'
                """)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (activeArtifactOwner != 0)
        {
            return true;
        }

        var storageReferenceSha256 = SHA256.HashData(Encoding.Unicode.GetBytes(storageReference));
        return await db.CentralTransientDerivativeOutputIntents.AsNoTracking().AnyAsync(intent =>
            EF.Property<byte[]>(intent, "StorageReferenceSha256") == storageReferenceSha256
            && EF.Functions.Collate(intent.StorageReference, BinaryCollation) == storageReference
            && intent.ObjectState != CentralArtifactObjectState.Expired,
            cancellationToken).ConfigureAwait(false);
    }

    public static string CreateObjectKeyIdentity(string objectKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(objectKey)));
}
