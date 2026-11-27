using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeLapses;

internal sealed partial class SqliteTimeLapseStore
{
    internal async ValueTask<IReadOnlyList<SiloRetentionHold>> ReadSiloTimeLapseHoldsAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var holds = new HashSet<SiloRetentionHold>();
        using (var command = Command(connection, null, """
            SELECT plan_json, plan_sha256 FROM jobs WHERE state IN ('Queued','Working') AND plan_json IS NOT NULL LIMIT 65;
            """))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            var plans = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (++plans > MaximumPendingJobs) throw new InvalidDataException("Time-lapse retention holds exceed the queue bound.");
                var plan = DeserializeVerified<CameraAgentTimeLapseSourcePlan>(reader.GetString(0), reader.GetString(1));
                foreach (var source in plan.Sources)
                {
                    var id = source.Descriptor.Artifact.ArtifactId.ToString("D");
                    holds.Add(new(SiloHoldKind.TimeLapseInput, id, CaptureContractJson.ComputeDescriptorSha256(source.Descriptor)));
                    holds.Add(new(SiloHoldKind.TimeLapseInput, id, SiloArtifactStorage.RawEvidenceRevision));
                }
            }
        }

        using (var command = Command(connection, null, """
            SELECT p.product_id, p.document_json, p.document_sha256
            FROM jobs parent JOIN jobs child ON child.parent_job_id = parent.job_id
                JOIN products p ON p.job_id = child.job_id
            WHERE parent.state IN ('Queued','Working');
            """))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var product = DeserializeVerified<CameraAgentTimeLapseProduct>(reader.GetString(1), reader.GetString(2));
                holds.Add(new(SiloHoldKind.TimeLapseInput, Guid.ParseExact(reader.GetString(0), "N").ToString("D"),
                    product.OutputIdentitySha256));
            }
        }
        return holds.ToArray();
    }
}
