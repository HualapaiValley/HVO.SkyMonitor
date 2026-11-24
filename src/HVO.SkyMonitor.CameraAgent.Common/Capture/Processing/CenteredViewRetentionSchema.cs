namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal static class CenteredViewRetentionSchema
{
    internal static readonly string[] IndexNames =
    [
        "ix_processing_centered_view_changes_base",
        "ix_processing_centered_view_members_base",
        "ix_processing_centered_view_members_order",
        "ix_processing_centered_view_intervals_end",
        "ix_processing_centered_view_bases_parent",
        "ix_processing_centered_view_selections_revision"
    ];

    internal const string IndexSql = """
        CREATE INDEX ix_processing_centered_view_changes_base
            ON processing_centered_view_changes(base_id, selected_live, change_order);
        CREATE INDEX ix_processing_centered_view_members_base
            ON processing_centered_view_members(base_id);
        CREATE INDEX ix_processing_centered_view_members_order
            ON processing_centered_view_members(registered_order);
        CREATE INDEX ix_processing_centered_view_intervals_end
            ON processing_centered_view_intervals(ended_order);
        CREATE INDEX ix_processing_centered_view_bases_parent
            ON processing_centered_view_bases(recomputed_from);
        CREATE INDEX ix_processing_centered_view_selections_revision
            ON processing_centered_view_selections(base_id, revision_id);
        """;
}
