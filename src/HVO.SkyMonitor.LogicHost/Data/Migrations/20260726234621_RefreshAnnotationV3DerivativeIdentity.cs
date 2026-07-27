using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class RefreshAnnotationV3DerivativeIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            UpdateIdentity(
                migrationBuilder,
                "0D47360EE5C1FEEE0D6726D9EE082AEE555D519326B348EDCF1C8A3E9D4EA426",
                "D3481EE40566FBCD7B33596E654137A0605432B465328FCF5B43807A089BC61E");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            UpdateIdentity(
                migrationBuilder,
                "D3481EE40566FBCD7B33596E654137A0605432B465328FCF5B43807A089BC61E",
                "0D47360EE5C1FEEE0D6726D9EE082AEE555D519326B348EDCF1C8A3E9D4EA426");
        }

        private static void UpdateIdentity(MigrationBuilder migrationBuilder, string priorIdentity, string currentIdentity)
            => migrationBuilder.Sql($"""
                UPDATE jobs
                SET [RequestedRecipeIdentitySha256] = '{currentIdentity}',
                    [ExpectedRecipeIdentitySha256] = '{currentIdentity}',
                    [RequestIdentitySha256] = CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varchar(max), CONCAT(
                        'hvo-central-derivative-request-v2', CHAR(10),
                        LOWER(REPLACE(CONVERT(varchar(36), COALESCE(source.[DevicePublicId], source.[Id])), '-', '')), CHAR(10),
                        LOWER(REPLACE(CONVERT(varchar(36), source.[ArtifactId]), '-', '')), CHAR(10),
                        jobs.[TargetRole], CHAR(10), jobs.[TargetVariant], CHAR(10),
                        '{currentIdentity}'))), 2)
                FROM [CentralDerivativeJobs] AS jobs
                INNER JOIN [CentralArtifacts] AS source ON source.[Id] = jobs.[SourceCentralArtifactId]
                WHERE jobs.[RecipeName] = N'annotation'
                    AND jobs.[TargetRecipeVersion] = N'central-annotated-preview-v1'
                    AND jobs.[RequestedRecipeIdentitySha256] = '{priorIdentity}';
                """);
    }
}
