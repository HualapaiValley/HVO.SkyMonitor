def fail($message): error("issue #268 physical evidence: " + $message);
def require($condition; $message): if $condition then . else fail($message) end;
def stats:
  sort as $values |
  if ($values | length) == 0 then fail("statistics require at least one value") else {
    count: ($values | length),
    minimum: $values[0],
    median: (if ($values | length) % 2 == 0 then
      (($values[(($values | length) / 2) - 1] + $values[($values | length) / 2]) / 2)
      else $values[(($values | length) / 2 | floor)] end),
    p95: $values[((($values | length) * 0.95 | ceil) - 1)],
    maximum: $values[-1]
  } end;
def trial_range:
  sort as $values | {minimum: $values[0], median: $values[2], maximum: $values[4]};
def attribute_values: .value | .. | scalars | tostring;
def attribute_value: first(attribute_values) // "";
def point_attribute($point; $key):
  first($point.attributes[]? | select(.key == $key) | attribute_value) // "";
def metric_points($documents):
  [$documents[] | .resourceMetrics[]? | .scopeMetrics[]? as $scope |
    $scope.metrics[]? as $metric |
    ($metric.sum.dataPoints[]?, $metric.gauge.dataPoints[]?, $metric.histogram.dataPoints[]?) as $point |
    {scope: $scope.scope.name, name: $metric.name, unit: ($metric.unit // ""),
      attributes: ($point.attributes // []), timestampUnixNano: (($point.timeUnixNano // "0") | tonumber),
      value: (($point.asInt // $point.asDouble // 0) | tonumber), count: (($point.count // 0) | tonumber),
      explicitBounds: (($point.explicitBounds // []) | map(tonumber)),
      bucketCounts: (($point.bucketCounts // []) | map(tonumber))}];
def span_names($documents):
  [$documents[] | .resourceSpans[]?.scopeSpans[]?.spans[]?.name] | unique;
def span_pairs($documents):
  [$documents[] | .resourceSpans[]?.scopeSpans[]? as $scope | $scope.spans[]? |
    {scope: $scope.scope.name, name: .name}] | unique;
def span_points($documents):
  [$documents[] | .resourceSpans[]?.scopeSpans[]? as $scope | $scope.spans[]? |
    {scope: $scope.scope.name, name: .name,
     attributes: (.attributes // []),
     startUnixNano: ((.startTimeUnixNano // "0") | tonumber),
     endUnixNano: ((.endTimeUnixNano // "0") | tonumber)}];
def log_records($documents):
  [$documents[] | .resourceLogs[]?.scopeLogs[]?.logRecords[]?];
def log_event_id:
  first(.attributes[]? | select(.key == "EventId") | attribute_values | tonumber?) // null;
def log_timestamp:
  ((.timeUnixNano // .observedTimeUnixNano // "0") | tonumber);
def log_body_text:
  [(.body? // empty) | .. | strings] | join(" ");
def log_source:
  point_attribute(.; "SourceContext");
def maximum_gap($samples):
  [$samples | sort_by(.monotonicTimestampMilliseconds) | . as $ordered |
    range(1; $ordered | length) |
    $ordered[.].monotonicTimestampMilliseconds - $ordered[. - 1].monotonicTimestampMilliseconds] |
  max // 0;
def counter_delta($points; $name; $start; $end):
  [$points[] | select(.name == $name and (.timestampUnixNano / 1000000) >= $start and
    (.timestampUnixNano / 1000000) <= $end)] |
  sort_by(.attributes) | group_by(.attributes) |
  map(sort_by(.timestampUnixNano) | if length >= 2 then .[-1].value - .[0].value else 0 end) | add // 0;
def counter_delta_bracketed($points; $name; $start; $end):
  [$points[] | select(.name == $name)] | sort_by(.attributes) | group_by(.attributes) |
  map(sort_by(.timestampUnixNano) as $series |
    ([$series[] | select((.timestampUnixNano / 1000000) <= $start)][-1] // null) as $before |
    ([$series[] | select((.timestampUnixNano / 1000000) >= $end)][0] // null) as $after |
    if $before == null or $after == null then fail("metric does not bracket measured boundaries: " + $name)
    else $after.value - $before.value end) | add // 0;
def counter_delta_bracketed_attribute($points; $name; $attribute; $value; $start; $end):
  [$points[] | select(.name == $name and point_attribute(.; $attribute) == $value)] |
  if length == 0 then fail("missing metric series: " + $name + " " + $attribute + "=" + $value) else . end |
  sort_by(.attributes) | group_by(.attributes) |
  map(sort_by(.timestampUnixNano) as $series |
    ([$series[] | select((.timestampUnixNano / 1000000) <= $start)][-1] // null) as $before |
    ([$series[] | select((.timestampUnixNano / 1000000) >= $end)][0] // null) as $after |
    if $before == null or $after == null then fail("metric does not bracket measured boundaries: " + $name + " " + $attribute + "=" + $value)
    else $after.value - $before.value end) | add // 0;
def partition_series($series; $start; $split; $end):
  ($series | sort_by(.timestampUnixNano)) as $ordered |
  ([$ordered[] | select((.timestampUnixNano / 1000000) <= $start)][-1] // null) as $before |
  ([$ordered[] | select((.timestampUnixNano / 1000000) >= $end)][0] // null) as $after |
  ([$ordered[] | select((.timestampUnixNano / 1000000) <= $split)][-1] // null) as $splitBefore |
  ([$ordered[] | select((.timestampUnixNano / 1000000) >= $split)][0] // null) as $splitAfter |
  if $before == null or $after == null or $splitBefore == null or $splitAfter == null then
    fail("metric series does not bracket all phase boundaries")
  else
    (if $splitBefore.timestampUnixNano == $splitAfter.timestampUnixNano then $splitBefore.value
     else ($splitBefore.value + ($splitAfter.value - $splitBefore.value) *
       (($split * 1000000 - $splitBefore.timestampUnixNano) /
        ($splitAfter.timestampUnixNano - $splitBefore.timestampUnixNano))) end) as $splitValue |
    {acquisition: ($splitValue - $before.value), drain: ($after.value - $splitValue),
     total: ($after.value - $before.value)}
  end;
def counter_partition($points; $name; $start; $split; $end):
  [$points[] | select(.name == $name)] |
  if length == 0 then fail("missing metric series: " + $name) else . end |
  sort_by(.attributes) | group_by(.attributes) |
  map(partition_series(.; $start; $split; $end)) |
  {acquisition: (map(.acquisition) | add), drain: (map(.drain) | add), total: (map(.total) | add)};
def counter_partition_attribute($points; $name; $attribute; $value; $start; $split; $end):
  [$points[] | select(.name == $name and point_attribute(.; $attribute) == $value)] |
  if length == 0 then fail("missing metric series: " + $name + " " + $attribute + "=" + $value) else . end |
  sort_by(.attributes) | group_by(.attributes) |
  map(partition_series(.; $start; $split; $end)) |
  {acquisition: (map(.acquisition) | add), drain: (map(.drain) | add), total: (map(.total) | add)};
def prometheus_delta($capture; $boundary; $name):
  $capture.boundarySnapshots[$boundary].prometheusCounters[$name] -
    $capture.boundarySnapshots.acquisitionStart.prometheusCounters[$name];
def counter_delta_attribute($points; $name; $attribute; $value; $start; $end):
  [$points[] as $point | select($point.name == $name and point_attribute($point; $attribute) == $value and
    ($point.timestampUnixNano / 1000000) >= $start and ($point.timestampUnixNano / 1000000) <= $end) | $point] |
  sort_by(.timestampUnixNano) | if length >= 2 then .[-1].value - .[0].value else 0 end;
def subtract_arrays($after; $before):
  [range(0; $after | length) as $index | $after[$index] - $before[$index]];
def histogram_summaries($points; $name; $start; $end):
  [$points[] | select(.name == $name and (.bucketCounts | length) > 0 and
    (.timestampUnixNano / 1000000) >= $start and (.timestampUnixNano / 1000000) <= $end)] |
  sort_by(.attributes) | group_by(.attributes) | map(
    sort_by(.timestampUnixNano) as $series |
    if ($series | length) < 2 then empty else
      (subtract_arrays($series[-1].bucketCounts; $series[0].bucketCounts)) as $buckets |
      ($buckets | add) as $count |
      ($count * 0.95 | ceil) as $rank |
      ([range(0; $buckets | length) as $index |
        select(([$buckets[0:$index + 1][]] | add) >= $rank) | $index][0]) as $p95_index |
      {attributes: $series[0].attributes, count: $count,
       p95: (if $count == 0 then null elif $p95_index < ($series[-1].explicitBounds | length)
         then $series[-1].explicitBounds[$p95_index] else null end),
       overflowP95: ($count > 0 and $p95_index == ($series[-1].explicitBounds | length)),
        explicitBounds: $series[-1].explicitBounds, bucketDeltas: $buckets}
    end);
def histogram_summaries_attribute($points; $name; $attribute; $value; $start; $end):
  histogram_summaries(
    [$points[] | select(point_attribute(.; $attribute) == $value)];
    $name; $start; $end);
def parse_block_stat:
  [splits("[[:space:]]+") | select(length > 0) | tonumber];
def parse_cpu_stat:
  split("\n")[0] | [splits("[[:space:]]+") | select(test("^[0-9]+$")) | tonumber];
def human_bytes:
  gsub("^[[:space:]]+|[[:space:]]+$"; "") |
  capture("^(?<number>[0-9]+(?:[.][0-9]+)?)(?<unit>B|kB|KB|MB|GB|TB|KiB|MiB|GiB|TiB)$") as $value |
  ($value.number | tonumber) * ({B: 1, kB: 1000, KB: 1000, MB: 1000000, GB: 1000000000,
    TB: 1000000000000, KiB: 1024, MiB: 1048576, GiB: 1073741824, TiB: 1099511627776}[$value.unit]);
def io_pair:
  split("/") | map(human_bytes);
def normalize_delta($delta; $captures; $seconds): {
  raw: $delta,
  perCapture: ($delta | with_entries(.value = (.value / $captures))),
  perMatchingWindowSecond: ($delta | with_entries(.value = (.value / $seconds)))
};
def process_cpu_partition($points; $start; $split; $end):
  (counter_partition_attribute($points; "process.cpu.time"; "cpu.mode"; "user"; $start; $split; $end)) as $user |
  (counter_partition_attribute($points; "process.cpu.time"; "cpu.mode"; "system"; $start; $split; $end)) as $system |
  {acquisition: ($user.acquisition + $system.acquisition), drain: ($user.drain + $system.drain),
   total: ($user.total + $system.total)};
def process_cpu_series($points; $start; $end):
  [$points[] | select(.name == "process.cpu.time" and
    (point_attribute(.; "cpu.mode") | IN("user", "system")) and
    (.timestampUnixNano / 1000000) >= $start and (.timestampUnixNano / 1000000) <= $end)] |
  sort_by(.timestampUnixNano) | group_by(.timestampUnixNano) |
  map({timestampUnixNano: .[0].timestampUnixNano, value: (map(.value) | add)});
def process_cpu_intervals($series):
  [range(1; $series | length) |
    {gapMilliseconds: (($series[.].timestampUnixNano - $series[. - 1].timestampUnixNano) / 1000000),
     utilizationPercent: (100 * ($series[.].value - $series[. - 1].value) /
       (($series[.].timestampUnixNano - $series[. - 1].timestampUnixNano) / 1000000000))}];
def application_counter_partitions($points; $start; $split; $end):
  (counter_partition($points; "camera_agent.ingress.committed"; $start; $split; $end)) as $ingressCommitted |
  (counter_partition($points; "camera_agent.ingress.committed.bytes"; $start; $split; $end)) as $ingressCommittedBytes |
  (counter_partition($points; "camera_agent.ingress.sqlite.transactions"; $start; $split; $end)) as $ingressTransactions |
  (counter_partition($points; "camera_agent.ingress.sqlite.checkpoints"; $start; $split; $end)) as $ingressCheckpoints |
  (counter_partition($points; "camera_agent.lanes.work.created"; $start; $split; $end)) as $laneWorkCreated |
  (counter_partition_attribute($points; "camera_agent.lanes.claims"; "result"; "claimed"; $start; $split; $end)) as $laneClaims |
  (counter_partition($points; "camera_agent.lanes.completed"; $start; $split; $end)) as $laneCompleted |
  (counter_partition($points; "camera_agent.processing.graphs"; $start; $split; $end)) as $processingGraphs | {
    acquisition: {ingressCommitted: $ingressCommitted.acquisition, ingressCommittedBytes: $ingressCommittedBytes.acquisition,
      ingressTransactions: $ingressTransactions.acquisition, ingressCheckpoints: $ingressCheckpoints.acquisition,
      laneWorkCreated: $laneWorkCreated.acquisition, laneClaims: $laneClaims.acquisition,
      laneCompleted: $laneCompleted.acquisition, processingGraphs: $processingGraphs.acquisition},
    drain: {ingressCommitted: $ingressCommitted.drain, ingressCommittedBytes: $ingressCommittedBytes.drain,
      ingressTransactions: $ingressTransactions.drain, ingressCheckpoints: $ingressCheckpoints.drain,
      laneWorkCreated: $laneWorkCreated.drain, laneClaims: $laneClaims.drain,
      laneCompleted: $laneCompleted.drain, processingGraphs: $processingGraphs.drain},
    total: {ingressCommitted: $ingressCommitted.total, ingressCommittedBytes: $ingressCommittedBytes.total,
      ingressTransactions: $ingressTransactions.total, ingressCheckpoints: $ingressCheckpoints.total,
      laneWorkCreated: $laneWorkCreated.total, laneClaims: $laneClaims.total,
      laneCompleted: $laneCompleted.total, processingGraphs: $processingGraphs.total}
  };
def prometheus_counter_deltas($capture; $from; $to):
  ($capture.boundarySnapshots[$from].prometheusCounters) as $before |
  ($capture.boundarySnapshots[$to].prometheusCounters) as $after | {
    prometheusIngressCommitted: ($after.camera_agent_ingress_committed - $before.camera_agent_ingress_committed),
    prometheusIngressTransactions: ($after.camera_agent_ingress_sqlite_transactions - $before.camera_agent_ingress_sqlite_transactions),
    prometheusIngressCheckpoints: ($after.camera_agent_ingress_sqlite_checkpoints - $before.camera_agent_ingress_sqlite_checkpoints),
    prometheusIngressLockWaitCount: ($after.camera_agent_ingress_sqlite_lock_wait_duration_seconds_count - $before.camera_agent_ingress_sqlite_lock_wait_duration_seconds_count),
    prometheusIngressLockWaitSeconds: ($after.camera_agent_ingress_sqlite_lock_wait_duration_seconds_sum - $before.camera_agent_ingress_sqlite_lock_wait_duration_seconds_sum),
    prometheusLaneWorkCreated: ($after.camera_agent_lanes_work_created - $before.camera_agent_lanes_work_created),
    prometheusLaneClaims: ($after.camera_agent_lanes_claims - $before.camera_agent_lanes_claims),
    prometheusLaneCompleted: ($after.camera_agent_lanes_completed - $before.camera_agent_lanes_completed),
    prometheusProcessingGraphs: ($after.camera_agent_processing_graphs - $before.camera_agent_processing_graphs)
  };
def process_container_io_deltas($first; $last):
  ($first.dockerStats.BlockIO | io_pair) as $firstContainerBlock |
  ($last.dockerStats.BlockIO | io_pair) as $lastContainerBlock |
  ($first.dockerStats.NetIO | io_pair) as $firstContainerNetwork |
  ($last.dockerStats.NetIO | io_pair) as $lastContainerNetwork | {
    processReadBytes: ($last.process.readBytes - $first.process.readBytes),
    processWriteBytes: ($last.process.writeBytes - $first.process.writeBytes),
    processReadSyscalls: ($last.process.readSyscalls - $first.process.readSyscalls),
    processWriteSyscalls: ($last.process.writeSyscalls - $first.process.writeSyscalls),
    containerBlockReadBytes: ($lastContainerBlock[0] - $firstContainerBlock[0]),
    containerBlockWriteBytes: ($lastContainerBlock[1] - $firstContainerBlock[1]),
    containerNetworkReadBytes: ($lastContainerNetwork[0] - $firstContainerNetwork[0]),
    containerNetworkWriteBytes: ($lastContainerNetwork[1] - $firstContainerNetwork[1])
  };
def boundary_storage_io_deltas($first; $last; $logicalBlockSize):
  ($first.hostBlockDevice.stat | parse_block_stat) as $firstBlock |
  ($last.hostBlockDevice.stat | parse_block_stat) as $lastBlock | {
    blockReadBytes: (($lastBlock[2] - $firstBlock[2]) * $logicalBlockSize),
    blockWriteBytes: (($lastBlock[6] - $firstBlock[6]) * $logicalBlockSize),
    blockIoTimeMilliseconds: ($lastBlock[9] - $firstBlock[9]),
    sqliteDatabaseGrowthBytes: ($last.sqliteFiles.databaseBytes - $first.sqliteFiles.databaseBytes),
    sqliteWalGrowthBytes: ($last.sqliteFiles.walBytes - $first.sqliteFiles.walBytes),
    sqliteShmGrowthBytes: ($last.sqliteFiles.shmBytes - $first.sqliteFiles.shmBytes)
  };
def required_series_coverage($points; $required; $start; $end):
  ((($end - $start) / 2000 | floor) + 1) as $scheduled |
  [$points[] | select(.name as $name | $required | index($name))] |
  sort_by([.name, .attributes]) | group_by([.name, .attributes]) |
  map(sort_by(.timestampUnixNano) as $series |
    ([$series[] | select((.timestampUnixNano / 1000000) >= $start and
      (.timestampUnixNano / 1000000) <= $end)] | unique_by(.timestampUnixNano)) as $window |
    ([$window | range(1; length) as $index |
      ($window[$index].timestampUnixNano - $window[$index - 1].timestampUnixNano) / 1000000] | max // 0) as $maximumGap |
    {name: $series[0].name, attributes: $series[0].attributes, scheduledSamples: $scheduled,
     retainedSamples: ($window | length), coverage: (($window | length) / $scheduled),
     maximumGapMilliseconds: $maximumGap,
     firstOffsetMilliseconds: (if ($window | length) == 0 then null else ($window[0].timestampUnixNano / 1000000) - $start end),
     finalOffsetMilliseconds: (if ($window | length) == 0 then null else $end - ($window[-1].timestampUnixNano / 1000000) end)} |
    . + {passed: (.retainedSamples >= (.scheduledSamples * 0.95) and .maximumGapMilliseconds <= 5000 and
      .firstOffsetMilliseconds != null and .firstOffsetMilliseconds <= 5000 and
      .finalOffsetMilliseconds != null and .finalOffsetMilliseconds <= 5000)});
def runtime_partitions($points; $start; $split; $end):
  (counter_partition($points; "dotnet.gc.heap.total_allocated"; $start; $split; $end)) as $allocated |
  (counter_partition($points; "dotnet.gc.pause.time"; $start; $split; $end)) as $pause |
  (counter_partition($points; "dotnet.gc.collections"; $start; $split; $end)) as $collections | {
    acquisition: {allocatedBytes: $allocated.acquisition, gcPauseSeconds: $pause.acquisition,
      gcCollections: $collections.acquisition},
    drain: {allocatedBytes: $allocated.drain, gcPauseSeconds: $pause.drain, gcCollections: $collections.drain},
    total: {allocatedBytes: $allocated.total, gcPauseSeconds: $pause.total, gcCollections: $collections.total}
  };
def reconciliation_failures($acquisition; $drain; $total):
  [($total | keys[]) as $key |
    (($acquisition[$key] + $drain[$key]) - $total[$key]) as $difference |
    select(($difference | fabs) > (1e-9 * ([1, ($total[$key] | fabs)] | max))) |
    {metric: $key, acquisition: $acquisition[$key], drain: $drain[$key], total: $total[$key], difference: $difference}];
def reset_sensitive_deltas:
  del(.sqliteDatabaseGrowthBytes, .sqliteWalGrowthBytes, .sqliteShmGrowthBytes);
def forbidden_telemetry_value:
  test("(^|[^0-9A-Fa-f])[0-9A-Fa-f]{64}([^0-9A-Fa-f]|$)|[0-9a-fA-F]{8}-[0-9a-fA-F-]{27,}|/(home|workspaces|tmp|var/lib/hvo)/[^[:space:]]+|authorization:|bearer |set-cookie:|requestverificationtoken|connection[_-]?string|owner[_-]?password|idempotency|checksum|storage[_-]?(reference|path|id)|actor[=:]"; "i");

def trial:
  ($acquisition[0] // fail("missing capture evidence")) as $capture |
  ($host[0] // fail("missing host manifest")) as $host_manifest |
  ($trialManifests[0] // fail("missing authenticated trial input manifest")) as $trial_manifest |
  ($execution[0] // fail("missing trial execution evidence")) as $execution_evidence |
  (metric_points($metrics)) as $points |
  ([$points[].name] | unique) as $metric_names |
  ([$points[].scope] | unique) as $metric_scopes |
  (span_names($traces)) as $spans |
  (span_pairs($traces)) as $span_pairs |
  (span_points($traces)) as $span_points |
  (log_records($logs)) as $records |
  ([$records[] | log_event_id | select(. != null)] | unique) as $events |
  ([
    "capture-cycle", "capture-control", "capture-ingress-handoff",
    "raw-ingress.accept", "payload.publish", "sidecar.publish", "sqlite.commit",
    "capture-lanes.claim", "capture-lanes.process", "capture-lanes.ack",
    "processing-graph.validate", "processing-graph.execute"
  ]) as $required_spans |
  ([
    {scope: "HVO.SkyMonitor.CameraAgent.CaptureControl", name: "capture-cycle"},
    {scope: "HVO.SkyMonitor.CameraAgent.CaptureControl", name: "capture-control"},
    {scope: "HVO.SkyMonitor.CameraAgent.CaptureControl", name: "capture-ingress-handoff"},
    {scope: "HVO.SkyMonitor.CameraAgent.RawIngress", name: "raw-ingress.accept"},
    {scope: "HVO.SkyMonitor.CameraAgent.RawIngress", name: "payload.publish"},
    {scope: "HVO.SkyMonitor.CameraAgent.RawIngress", name: "sidecar.publish"},
    {scope: "HVO.SkyMonitor.CameraAgent.RawIngress", name: "sqlite.commit"},
    {scope: "HVO.SkyMonitor.CameraAgent.CaptureLanes", name: "capture-lanes.claim"},
    {scope: "HVO.SkyMonitor.CameraAgent.CaptureLanes", name: "capture-lanes.process"},
    {scope: "HVO.SkyMonitor.CameraAgent.CaptureLanes", name: "capture-lanes.ack"},
    {scope: "HVO.SkyMonitor.CameraAgent.ProcessingGraph", name: "processing-graph.validate"},
    {scope: "HVO.SkyMonitor.CameraAgent.ProcessingGraph", name: "processing-graph.execute"}
  ]) as $required_span_pairs |
  ([
    "camera_agent.capture_control.cycles", "camera_agent.capture_control.decisions",
    "camera_agent.capture_control.start_jitter", "camera_agent.capture_control.cycle.duration",
    "camera_agent.capture_control.segment.duration", "camera_agent.capture_control.decision.duration",
    "camera_agent.ingress.committed", "camera_agent.ingress.committed.bytes",
    "camera_agent.ingress.commit.duration", "camera_agent.ingress.sqlite.transactions",
    "camera_agent.ingress.sqlite.checkpoints", "camera_agent.ingress.sqlite.lock_wait.duration",
    "camera_agent.ingress.pending", "camera_agent.ingress.pending.bytes", "camera_agent.ingress.oldest.age",
    "camera_agent.lanes.work.created", "camera_agent.lanes.claims", "camera_agent.lanes.completed",
    "camera_agent.lanes.claim.duration", "camera_agent.lanes.processing.duration", "camera_agent.lanes.ack.duration",
    "camera_agent.lanes.pending", "camera_agent.lanes.pending.bytes", "camera_agent.lanes.oldest.age",
    "camera_agent.lanes.leased", "camera_agent.processing.graphs", "camera_agent.processing.graph.duration",
    "camera_agent.processing.pending", "camera_agent.processing.oldest.age",
    "dotnet.gc.heap.total_allocated", "dotnet.gc.last_collection.heap.size",
    "dotnet.gc.last_collection.heap.fragmentation.size", "dotnet.gc.collections",
    "dotnet.gc.pause.time", "process.cpu.time", "process.memory.working_set"
  ]) as $required_metrics |
  ([
    "camera_agent.capture_control.cycles", "camera_agent.capture_control.decisions",
    "camera_agent.capture_control.start_jitter", "camera_agent.capture_control.cycle.duration",
    "camera_agent.capture_control.segment.duration", "camera_agent.capture_control.decision.duration",
    "camera_agent.ingress.committed", "camera_agent.ingress.committed.bytes",
    "camera_agent.ingress.commit.duration", "camera_agent.ingress.sqlite.transactions",
    "camera_agent.ingress.sqlite.lock_wait.duration",
    "camera_agent.lanes.work.created", "camera_agent.lanes.claims", "camera_agent.lanes.completed",
    "camera_agent.lanes.claim.duration", "camera_agent.lanes.processing.duration", "camera_agent.lanes.ack.duration",
    "camera_agent.processing.graphs", "camera_agent.processing.graph.duration",
    "dotnet.gc.heap.total_allocated", "process.cpu.time"
  ]) as $required_positive_metrics |
  (["HVO.SkyMonitor.CameraAgent.CaptureControl", "HVO.SkyMonitor.CameraAgent.RawIngress",
    "HVO.SkyMonitor.CameraAgent.CaptureLanes", "HVO.SkyMonitor.CameraAgent.ProcessingGraph"] ) as $required_metric_scopes |
  ({
    "camera_agent.capture_control.cycles": "{cycle}",
    "camera_agent.capture_control.decisions": "{decision}",
    "camera_agent.capture_control.start_jitter": "s",
    "camera_agent.capture_control.cycle.duration": "s",
    "camera_agent.capture_control.segment.duration": "s",
    "camera_agent.capture_control.decision.duration": "s",
    "camera_agent.ingress.committed": "{capture}",
    "camera_agent.ingress.committed.bytes": "By",
    "camera_agent.ingress.commit.duration": "s",
    "camera_agent.ingress.sqlite.transactions": "{transaction}",
    "camera_agent.ingress.sqlite.checkpoints": "{checkpoint}",
    "camera_agent.ingress.sqlite.lock_wait.duration": "s",
    "camera_agent.ingress.pending": "{capture}",
    "camera_agent.ingress.pending.bytes": "By",
    "camera_agent.ingress.oldest.age": "s",
    "camera_agent.lanes.work.created": "{work}",
    "camera_agent.lanes.claims": "{claim}",
    "camera_agent.lanes.completed": "{work}",
    "camera_agent.lanes.claim.duration": "s",
    "camera_agent.lanes.processing.duration": "s",
    "camera_agent.lanes.ack.duration": "s",
    "camera_agent.lanes.pending": "{work}",
    "camera_agent.lanes.pending.bytes": "By",
    "camera_agent.lanes.oldest.age": "s",
    "camera_agent.lanes.leased": "{lease}",
    "camera_agent.processing.graphs": "{graph}",
    "camera_agent.processing.graph.duration": "s",
    "camera_agent.processing.pending": "{graph}",
    "camera_agent.processing.oldest.age": "s"
  }) as $units |
  (["cadence", "reason", "regime", "segment", "outcome", "root", "phase", "operation", "result",
    "lane", "required", "step", "recipe", "role", "variant", "le", "dotnet.gc.heap.generation.name", "cpu.mode",
    "otel_scope_name", "otel_scope_version", "service_name", "service_instance_id", "job", "instance", "target"] ) as $allowed_labels |
  ($capture.boundaries.acquisitionStartUnixMilliseconds) as $start |
  ($capture.boundaries.acquisitionEndUnixMilliseconds) as $acquisition_end |
  ($capture.boundaries.totalEndUnixMilliseconds) as $end |
  ($capture.boundarySnapshots.acquisitionStart.capturedUnixMilliseconds) as $telemetry_start |
  ($capture.boundarySnapshots.totalEnd.capturedUnixMilliseconds) as $telemetry_end |
  ([$span_points[] | select((.startUnixNano / 1000000) >= $telemetry_start and
    (.endUnixNano / 1000000) <= $telemetry_end)]) as $measured_spans |
  ([$records[] | select((log_timestamp / 1000000) >= $telemetry_start and
    (log_timestamp / 1000000) <= $telemetry_end)]) as $measured_logs |
  ({
    captureCycle: ([$measured_spans[] | select(.name == "capture-cycle")] | length),
    captureControl: ([$measured_spans[] | select(.name == "capture-control")] | length),
    captureIngressHandoff: ([$measured_spans[] | select(.name == "capture-ingress-handoff")] | length),
    rawIngressAccept: ([$measured_spans[] | select(.name == "raw-ingress.accept")] | length),
    payloadPublish: ([$measured_spans[] | select(.name == "payload.publish")] | length),
    sidecarPublish: ([$measured_spans[] | select(.name == "sidecar.publish")] | length),
    sqliteCommit: ([$measured_spans[] | select(.name == "sqlite.commit")] | length),
    laneClaim: ([$measured_spans[] | select(.name == "capture-lanes.claim" and point_attribute(.; "result") == "claimed")] | length),
    laneProcess: ([$measured_spans[] | select(.name == "capture-lanes.process")] | length),
    laneAck: ([$measured_spans[] | select(.name == "capture-lanes.ack")] | length),
    processingExecute: ([$measured_spans[] | select(.name == "processing-graph.execute")] | length)
  }) as $measured_span_counts |
  ({
    rawCommit: ([$measured_logs[] | select(log_event_id == 2042)] | length),
    laneComplete: ([$measured_logs[] | select(log_event_id == 2054)] | length),
    processingComplete: ([$measured_logs[] | select(log_event_id == 2072)] | length),
    controlDecision: ([$measured_logs[] | select(log_event_id == 2073)] | length),
    deadlineOverrun: ([$measured_logs[] | select(log_event_id == 2075)] | length)
  }) as $measured_log_counts |
  ([$records[] | select(log_event_id == 103 and (log_timestamp / 1000000) >= $telemetry_start) |
    {timestampUnixNano: log_timestamp, body: log_body_text}]) as $late_startup_warnings |
  ([$records[] | select(log_event_id == 2048 and
    ((point_attribute(.; "Result") | ascii_downcase) != "success" or
      (point_attribute(.; "Operation") | IN("commit", "checkpoint") | not) or
     ((log_body_text | ascii_downcase) | contains("completed with result success") | not))) |
    {timestampUnixNano: log_timestamp, operation: point_attribute(.; "Operation"),
     result: point_attribute(.; "Result"), body: log_body_text}]) as $invalid_sqlite_success_warnings |
  ([$records[] |
    (log_event_id) as $event_id |
    select($event_id == 15 or $event_id == 35) |
    {eventId: $event_id, timestampUnixNano: log_timestamp, source: log_source, body: log_body_text} |
    select((.timestampUnixNano / 1000000) >= $telemetry_start or
      (if .eventId == 15 then
        .source != "Microsoft.AspNetCore.Hosting.Diagnostics" or .body != "[REDACTED]"
       else
        .source != "Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager" or .body != "[REDACTED]"
       end))]) as $invalid_expected_startup_warnings |
  ([$records[] | (log_event_id) as $event_id | select($event_id == 15 or $event_id == 35) |
    {eventId: $event_id, timestampUnixNano: log_timestamp, source: log_source, body: log_body_text}]) as $expected_startup_warnings |
  ({
    cycles: prometheus_delta($capture; "acquisitionEnd"; "camera_agent_capture_control_cycles"),
    decisions: prometheus_delta($capture; "acquisitionEnd"; "camera_agent_capture_control_decisions"),
    ingressCommitted: prometheus_delta($capture; "acquisitionEnd"; "camera_agent_ingress_committed"),
    ingressTransactions: prometheus_delta($capture; "acquisitionEnd"; "camera_agent_ingress_sqlite_transactions"),
    ingressCheckpoints: prometheus_delta($capture; "totalEnd"; "camera_agent_ingress_sqlite_checkpoints"),
    ingressLockWaitCount: prometheus_delta($capture; "totalEnd"; "camera_agent_ingress_sqlite_lock_wait_duration_seconds_count"),
    ingressLockWaitSeconds: prometheus_delta($capture; "totalEnd"; "camera_agent_ingress_sqlite_lock_wait_duration_seconds_sum"),
    laneWorkCreated: prometheus_delta($capture; "totalEnd"; "camera_agent_lanes_work_created"),
    laneClaims: prometheus_delta($capture; "totalEnd"; "camera_agent_lanes_claims"),
    laneCompleted: prometheus_delta($capture; "totalEnd"; "camera_agent_lanes_completed"),
    processingGraphs: prometheus_delta($capture; "totalEnd"; "camera_agent_processing_graphs")
  }) as $prometheus_measured |
  ({
    cycles: counter_delta_bracketed($points; "camera_agent.capture_control.cycles"; $telemetry_start; $telemetry_end),
    decisions: counter_delta_bracketed($points; "camera_agent.capture_control.decisions"; $telemetry_start; $telemetry_end),
    ingressCommitted: counter_delta_bracketed($points; "camera_agent.ingress.committed"; $telemetry_start; $telemetry_end),
    ingressTransactions: counter_delta_bracketed($points; "camera_agent.ingress.sqlite.transactions"; $telemetry_start; $telemetry_end),
    ingressCheckpoints: counter_delta_bracketed($points; "camera_agent.ingress.sqlite.checkpoints"; $telemetry_start; $telemetry_end),
    laneWorkCreated: counter_delta_bracketed($points; "camera_agent.lanes.work.created"; $telemetry_start; $telemetry_end),
    laneClaims: counter_delta_bracketed_attribute($points; "camera_agent.lanes.claims"; "result"; "claimed"; $telemetry_start; $telemetry_end),
    laneCompleted: counter_delta_bracketed($points; "camera_agent.lanes.completed"; $telemetry_start; $telemetry_end),
    processingGraphs: counter_delta_bracketed($points; "camera_agent.processing.graphs"; $telemetry_start; $telemetry_end)
  }) as $otlp_measured |
  ([$resources[] | select(.timestampUnixMilliseconds >= $start and .timestampUnixMilliseconds <= $end)]) as $window_samples |
  ((($end - $start) / 2000 | floor) + 1) as $scheduled_samples |
  (maximum_gap($window_samples)) as $maximum_gap_ms |
  ([range(1; $window_samples | length) as $index |
    (($window_samples[$index].timestampUnixMilliseconds - $window_samples[$index - 1].timestampUnixMilliseconds) -
      ($window_samples[$index].monotonicTimestampMilliseconds - $window_samples[$index - 1].monotonicTimestampMilliseconds)) as $clock_step |
    select($window_samples[$index].monotonicTimestampMilliseconds <= $window_samples[$index - 1].monotonicTimestampMilliseconds or
      ($window_samples[$index].monotonicTimestampMilliseconds - $window_samples[$index - 1].monotonicTimestampMilliseconds) > 5000 or
      ($clock_step | fabs) > 250) |
    {index: $index, wallDeltaMilliseconds: ($window_samples[$index].timestampUnixMilliseconds - $window_samples[$index - 1].timestampUnixMilliseconds),
     monotonicDeltaMilliseconds: ($window_samples[$index].monotonicTimestampMilliseconds - $window_samples[$index - 1].monotonicTimestampMilliseconds),
     clockStepMilliseconds: $clock_step}]) as $clock_step_findings |
  ([$resources[] | select(.timestampUnixMilliseconds >= $start and .timestampUnixMilliseconds <= $acquisition_end)]) as $acquisition_samples |
  ([$acquisition_samples[].process.rssBytes] | if length >= 40 then
    ((.[-20:] | sort | .[9:11] | add / 2) - (.[0:20] | sort | .[9:11] | add / 2))
    else fail("fewer than 40 acquisition RSS samples") end) as $rss_growth |
  ($window_samples[0]) as $first_sample |
  ($window_samples[-1]) as $last_sample |
  (($capture.boundaries.acquisitionEndUnixMilliseconds - $start) / 1000) as $acquisition_seconds |
  (($end - $capture.boundaries.acquisitionEndUnixMilliseconds) / 1000) as $drain_seconds |
  (($end - $start) / 1000) as $total_seconds |
  (process_cpu_partition($points; $start; $acquisition_end; $end)) as $process_cpu_partition |
  ($process_cpu_partition.total) as $process_cpu_seconds |
  (process_cpu_series($points; $start; $end)) as $process_cpu_series |
  (process_cpu_intervals($process_cpu_series)) as $process_cpu_intervals |
  ([$process_cpu_intervals[].utilizationPercent]) as $process_interval_cpu |
  ($first_sample.host.stat | parse_cpu_stat) as $first_host_cpu |
  ($last_sample.host.stat | parse_cpu_stat) as $last_host_cpu |
  (($last_host_cpu | add) - ($first_host_cpu | add)) as $host_cpu_ticks |
  ((($last_host_cpu[3] + $last_host_cpu[4]) - ($first_host_cpu[3] + $first_host_cpu[4]))) as $host_idle_ticks |
  ($host_manifest.storage.runtime.logicalBlockSize | tonumber) as $logical_block_size |
  (runtime_partitions($points; $start; $acquisition_end; $end)) as $runtime_partitions |
  (application_counter_partitions($points; $start; $acquisition_end; $end)) as $application_partitions |
  ($capture.boundarySnapshots.acquisitionStart) as $boundary_start |
  ($capture.boundarySnapshots.acquisitionEnd) as $boundary_acquisition_end |
  ($capture.boundarySnapshots.totalEnd) as $boundary_total_end |
  ([$boundary_start, $boundary_acquisition_end, $boundary_total_end] | map(
    (((.completedMonotonicTimestamp - .startedMonotonicTimestamp) * 1000 / $capture.monotonicFrequency) -
      (.completedUnixMilliseconds - .capturedUnixMilliseconds)) as $difference |
    select(($difference | fabs) > 250) |
    {boundary: .name, differenceMilliseconds: $difference})) as $boundary_duration_clock_findings |
  ([[$boundary_start, $boundary_acquisition_end], [$boundary_acquisition_end, $boundary_total_end]] | map(
    . as $pair |
    ((($pair[1].startedMonotonicTimestamp - $pair[0].startedMonotonicTimestamp) * 1000 / $capture.monotonicFrequency) -
      ($pair[1].capturedUnixMilliseconds - $pair[0].capturedUnixMilliseconds)) as $difference |
    select(($difference | fabs) > 250) |
    {from: $pair[0].name, to: $pair[1].name, differenceMilliseconds: $difference})) as $boundary_interval_clock_findings |
  ((process_container_io_deltas($boundary_start; $boundary_acquisition_end)) +
    (boundary_storage_io_deltas($boundary_start; $boundary_acquisition_end; $logical_block_size)) +
    $runtime_partitions.acquisition + $application_partitions.acquisition +
    (prometheus_counter_deltas($capture; "acquisitionStart"; "acquisitionEnd")) +
    {processCpuSeconds: $process_cpu_partition.acquisition}) as $acquisition_deltas |
  ((process_container_io_deltas($boundary_acquisition_end; $boundary_total_end)) +
    (boundary_storage_io_deltas($boundary_acquisition_end; $boundary_total_end; $logical_block_size)) +
    $runtime_partitions.drain + $application_partitions.drain +
    (prometheus_counter_deltas($capture; "acquisitionEnd"; "totalEnd")) +
    {processCpuSeconds: $process_cpu_partition.drain}) as $drain_deltas |
  ((process_container_io_deltas($boundary_start; $boundary_total_end)) +
    (boundary_storage_io_deltas($boundary_start; $boundary_total_end; $logical_block_size)) +
    $runtime_partitions.total + $application_partitions.total +
    (prometheus_counter_deltas($capture; "acquisitionStart"; "totalEnd")) +
    {processCpuSeconds: $process_cpu_partition.total}) as $total_deltas |
  (reconciliation_failures($acquisition_deltas; $drain_deltas; $total_deltas)) as $phase_reconciliation_failures |
  (required_series_coverage($points; $required_metrics; $telemetry_start; $telemetry_end)) as $required_series_coverage |
  ($total_deltas.allocatedBytes) as $allocated_bytes |
  ($total_deltas.gcPauseSeconds) as $gc_pause_seconds |
  ($total_deltas.gcCollections) as $gc_collections |
  ([$points[] as $point | select($point.name == "dotnet.gc.last_collection.heap.size" and
    point_attribute($point; "dotnet.gc.heap.generation.name") == "loh" and ($point.timestampUnixNano / 1000000) >= $start and
    ($point.timestampUnixNano / 1000000) <= $end) | $point.value]) as $loh_values |
  ([$points[] as $point | select($point.name == "dotnet.gc.last_collection.heap.size" and
    point_attribute($point; "dotnet.gc.heap.generation.name") == "poh" and ($point.timestampUnixNano / 1000000) >= $start and
    ($point.timestampUnixNano / 1000000) <= $end) | $point.value]) as $poh_values |
  ([$points[] as $point | select($point.name == "dotnet.gc.last_collection.heap.fragmentation.size" and
    point_attribute($point; "dotnet.gc.heap.generation.name") == "loh" and ($point.timestampUnixNano / 1000000) >= $start and
    ($point.timestampUnixNano / 1000000) <= $end) | $point.value]) as $loh_fragmentation_values |
  ([$points[] as $point | select($point.scope | startswith("HVO.SkyMonitor.")) |
    $point.attributes[]? | {metric: $point.name, key: .key, value: attribute_value}]) as $application_dimensions |
  ([$points[] as $point | $point.attributes[]? |
    {metric: $point.name, key: .key, value: attribute_value}]) as $all_metric_dimensions |
  ([($records[] | ((.body? // empty) | .. | strings), (.attributes[]? | attribute_values)),
    ($traces[] | .resourceSpans[]? |
      (.resource.attributes[]? | attribute_values),
      (.scopeSpans[]? | (.scope.attributes[]? | attribute_values),
        (.spans[]? | (.attributes[]? | attribute_values), (.events[]?.attributes[]? | attribute_values)))),
    ($metrics[] | .resourceMetrics[]? |
      (.resource.attributes[]? | attribute_values),
      (.scopeMetrics[]? | (.scope.attributes[]? | attribute_values),
        (.metrics[]? | (.sum.dataPoints[]?.attributes[]?, .gauge.dataPoints[]?.attributes[]?,
          .histogram.dataPoints[]?.attributes[]?) | attribute_values)))] |
    map(select(forbidden_telemetry_value))) as $private_values |
  ([($records, $traces, $metrics) | .. | objects | .key? |
      select(type == "string") |
      select(test("actor|idempotency|checksum|storage[_-]?(reference|path|id)|requestverificationtoken|authorization"; "i"))] |
    unique) as $private_attribute_keys |
  ([$records[] | select((.severityNumber // 0) >= 17) | {severity: .severityText, eventId: log_event_id}]) as $severe_logs |
  ([$records[] | select((.severityNumber // 0) >= 13 and (.severityNumber // 0) < 17) |
    (log_event_id) as $event_id | select(($event_id | IN(15, 35, 103, 2048) | not)) |
    {severity: .severityText, eventId: $event_id}]) as $unexpected_warnings |
  ([$points[] | select($units[.name] != null and .unit != $units[.name]) |
    {name, expected: $units[.name], actual: .unit}] | unique) as $unit_mismatches |
  (($required_spans - $spans)) as $missing_spans |
  (($required_span_pairs - $span_pairs)) as $missing_span_sources |
  (($required_metrics - $metric_names)) as $missing_metrics |
  (($required_metric_scopes - $metric_scopes)) as $missing_metric_scopes |
  ([$required_positive_metrics[] as $name |
    select(any($points[]; .name == $name and (.value > 0 or .count > 0)) | not) | $name]) as $nonpositive_metrics |
  ([$points[] as $point |
    select(
      ($point.name | IN("camera_agent.ingress.failures", "camera_agent.ingress.quarantine.records",
        "camera_agent.lanes.retries", "camera_agent.lanes.quarantined", "camera_agent.lanes.abandoned",
        "camera_agent.processing.retry")) and $point.value > 0 or
      ($point.name == "camera_agent.ingress.sqlite.transactions" and point_attribute($point; "result") == "failure" and $point.value > 0) or
      ($point.name == "camera_agent.ingress.sqlite.checkpoints" and point_attribute($point; "result") == "failure" and $point.value > 0) or
      ($point.name == "camera_agent.ingress.reconciliation.records" and
        (point_attribute($point; "outcome") | IN("quarantined", "missing", "index-failed")) and $point.value > 0)) |
    {name: $point.name, value: $point.value, attributes: $point.attributes}] | unique) as $nonzero_failure_metrics |
  ([2040, 2041, 2042, 2050, 2054, 2058, 2064, 2072, 2073] - $events) as $missing_events |
  ([2010, 2032, 2034, 2044, 2047, 2055, 2056, 2059, 2068] | map(select(. as $id | $events | index($id)))) as $prohibited_events |
  ([$application_dimensions[] as $dimension |
    select(($allowed_labels | index($dimension.key)) == null) | $dimension] | unique) as $unsupported_labels |
  ([$all_metric_dimensions | group_by([.metric, .key])[] |
    {metric: .[0].metric, key: .[0].key, cardinality: ([.[].value] | unique | length)} |
    select(.cardinality > 32)]) as $excessive_cardinality |
  ([$all_metric_dimensions[] |
    select((.key == "cpu.mode" and (.value | IN("user", "system") | not)) or
      (.key == "dotnet.gc.heap.generation.name" and (.value | IN("gen0", "gen1", "gen2", "loh", "poh") | not)))] |
    unique) as $invalid_runtime_labels |
  ({
    captureStartJitter: histogram_summaries($points; "camera_agent.capture_control.start_jitter"; $start; $end),
    captureCycle: histogram_summaries($points; "camera_agent.capture_control.cycle.duration"; $start; $end),
    captureSegments: histogram_summaries($points; "camera_agent.capture_control.segment.duration"; $start; $end),
    controlDecision: histogram_summaries($points; "camera_agent.capture_control.decision.duration"; $start; $end),
    ingressCommit: histogram_summaries($points; "camera_agent.ingress.commit.duration"; $start; $end),
    laneClaim: histogram_summaries_attribute($points; "camera_agent.lanes.claim.duration"; "result"; "claimed"; $start; $end),
    laneProcessing: histogram_summaries($points; "camera_agent.lanes.processing.duration"; $start; $end),
    laneAck: histogram_summaries($points; "camera_agent.lanes.ack.duration"; $start; $end),
    processingGraph: histogram_summaries($points; "camera_agent.processing.graph.duration"; $start; $end)
  }) as $histogram_deltas |
  {
    schemaVersion: "issue-268-physical-trial-summary-v1",
    profile: $capture.profile,
    trial: $capture.trial,
    trialInputIdentity: $trial_manifest.evidenceIdentitySha256,
    trialInputManifest: ($trial_manifest | {schemaVersion, profile, trial, revision, evidenceIdentitySha256, fileCount, totalBytes}),
    revision: $host_manifest.revision,
    sourceIdentities: $host_manifest.sourceIdentities,
    host: $host_manifest.host,
    networkIsolation: $host_manifest.networkIsolation,
    storage: $host_manifest.storage,
    mountAuthentication: $host_manifest.mountAuthentication,
    startupReadiness: $host_manifest.startupReadiness,
    centralTraffic: $host_manifest.centralTraffic,
    privacy: $host_manifest.privacy,
    wholeTrialElapsedSeconds: $execution_evidence.wholeTrialElapsedSeconds,
    acquisition: $capture.result,
    boundaries: $capture.boundaries,
    statistics: $capture.statistics,
    sqlite: $capture.sqlite,
    boundaryAuthentication: {
      monotonicFrequency: $capture.monotonicFrequency,
      acquisitionStart: $capture.boundarySnapshots.acquisitionStart,
      acquisitionEnd: $capture.boundarySnapshots.acquisitionEnd,
      totalEnd: $capture.boundarySnapshots.totalEnd,
      allCommandsAuthenticated: all($capture.barriers[]; .authenticated == true),
      durationClockFindings: $boundary_duration_clock_findings,
      intervalClockFindings: $boundary_interval_clock_findings,
      wallMonotonicCorrelationPassed: (($boundary_duration_clock_findings | length) == 0 and
        ($boundary_interval_clock_findings | length) == 0)
    },
    exportIo: $exportIo[0],
    dmesgMonitor: $dmesg[0],
    resources: {
      scheduledSamples: $scheduled_samples,
      retainedSamples: ($window_samples | length),
      coverage: (($window_samples | length) / $scheduled_samples),
      maximumGapMilliseconds: $maximum_gap_ms,
      timestampsMonotonicWithoutClockStep: (($clock_step_findings | length) == 0),
      clockStepFindings: $clock_step_findings,
      boundaryCoverage: {firstOffsetMilliseconds: ($first_sample.timestampUnixMilliseconds - $start),
        finalOffsetMilliseconds: ($end - $last_sample.timestampUnixMilliseconds)},
      rssFinal20MinusInitial20Bytes: $rss_growth,
      maximumCombinedBacklog: ([$window_samples[].backlog.combined] | max),
      maximumStageBacklog: ([$window_samples[].backlog.ingress, $window_samples[].backlog.lanes, $window_samples[].backlog.processing] | max),
      maximumOldestBacklogAgeSeconds: ([$window_samples[].backlog.oldestAgeSeconds] | max),
      usbPresentThroughout: all($window_samples[]; .usb.present == true and .usb.speedMegabits == 5000),
      armNeverThrottled: (if $capture.profile == "asi676mc" then all($window_samples[]; .arm.throttled == "0x0") else true end),
      process: {
        cpuSeconds: $process_cpu_seconds,
        coreEquivalentUtilizationPercent: (100 * $process_cpu_seconds / $total_seconds),
        hostCapacityUtilizationPercent: (100 * $process_cpu_seconds / $total_seconds / $host_manifest.host.logicalCpuCount),
        intervalCoreEquivalentUtilizationPercent: ($process_interval_cpu | stats),
        intervalSamples: $process_cpu_intervals,
        source: "bracketed OTLP process.cpu.time cpu.mode=user|system",
        ioSource: "exact C# acquisition-start/acquisition-end/total-end process snapshots",
        readBytes: $total_deltas.processReadBytes,
        writeBytes: $total_deltas.processWriteBytes,
        readSyscalls: $total_deltas.processReadSyscalls,
        writeSyscalls: $total_deltas.processWriteSyscalls,
        rssBoundaryBytes: {acquisitionStart: $boundary_start.process.rssBytes,
          acquisitionEnd: $boundary_acquisition_end.process.rssBytes, totalEnd: $boundary_total_end.process.rssBytes}
      },
      runtime: {allocatedBytes: $allocated_bytes, gcPauseSeconds: $gc_pause_seconds, gcCollections: $gc_collections,
        deltas: {
          acquisition: ($acquisition_deltas | {allocatedBytes, gcPauseSeconds, gcCollections}),
          drain: ($drain_deltas | {allocatedBytes, gcPauseSeconds, gcCollections}),
          total: ($total_deltas | {allocatedBytes, gcPauseSeconds, gcCollections})
        },
        gcCollectionsByGeneration: {
          gen0: counter_delta_attribute($points; "dotnet.gc.collections"; "dotnet.gc.heap.generation.name"; "gen0"; $start; $end),
          gen1: counter_delta_attribute($points; "dotnet.gc.collections"; "dotnet.gc.heap.generation.name"; "gen1"; $start; $end),
          gen2: counter_delta_attribute($points; "dotnet.gc.collections"; "dotnet.gc.heap.generation.name"; "gen2"; $start; $end)
        },
        largeObjectHeapBytes: ($loh_values | stats), pinnedObjectHeapBytes: ($poh_values | stats),
        largeObjectHeapFragmentationBytes: ($loh_fragmentation_values | stats)},
      blockDevice: {
        readBytes: $total_deltas.blockReadBytes,
        writeBytes: $total_deltas.blockWriteBytes,
        ioTimeMilliseconds: $total_deltas.blockIoTimeMilliseconds,
        source: "exact C# acquisition-start/acquisition-end/total-end host block-device snapshots"
      },
      phaseDeltas: {
        acquisition: normalize_delta($acquisition_deltas; $capture.result.measuredCount; $acquisition_seconds),
        drain: normalize_delta($drain_deltas; $capture.result.measuredCount; $drain_seconds),
        total: normalize_delta($total_deltas; $capture.result.measuredCount; $total_seconds),
        reconciliation: {rule: "acquisition + drain = total", failures: $phase_reconciliation_failures,
          passed: (($phase_reconciliation_failures | length) == 0)}
      },
      containerIo: {
        blockReadBytes: $total_deltas.containerBlockReadBytes,
        blockWriteBytes: $total_deltas.containerBlockWriteBytes,
        networkReadBytes: $total_deltas.containerNetworkReadBytes,
        networkWriteBytes: $total_deltas.containerNetworkWriteBytes,
        source: "exact C# acquisition-start/acquisition-end/total-end Docker stats snapshots"
      },
      sqliteFiles: {
        databaseGrowthBytes: $total_deltas.sqliteDatabaseGrowthBytes,
        walGrowthBytes: $total_deltas.sqliteWalGrowthBytes,
        shmGrowthBytes: $total_deltas.sqliteShmGrowthBytes,
        maximumWalBytes: ([$window_samples[].sqliteFiles.walBytes] | max),
        deltaSource: "exact C# acquisition-start/acquisition-end/total-end SQLite file snapshots"
      },
      rssBytes: ({start: $boundary_start.process.rssBytes, acquisitionEnd: $boundary_acquisition_end.process.rssBytes,
        totalEnd: $boundary_total_end.process.rssBytes} + ([$window_samples[].process.rssBytes] | stats)),
      hostAvailableMemoryBytes: ([$window_samples[].host.availableMemoryBytes] | stats),
      hostCpuUtilizationPercent: (100 * ($host_cpu_ticks - $host_idle_ticks) / $host_cpu_ticks),
      hostLoad: {
        oneMinute: ([$window_samples[].host.loadAverage | split(" ")[0] | tonumber] | stats),
        fiveMinute: ([$window_samples[].host.loadAverage | split(" ")[1] | tonumber] | stats),
        fifteenMinute: ([$window_samples[].host.loadAverage | split(" ")[2] | tonumber] | stats)
      },
      matchingWindowSeconds: {acquisition: $acquisition_seconds, drain: $drain_seconds, total: $total_seconds},
      measuredThroughputCapturesPerSecond: ($capture.result.measuredCount / $acquisition_seconds),
      normalized: normalize_delta($total_deltas; $capture.result.measuredCount; $total_seconds),
      samples: $window_samples
    },
      telemetry: {
      missingSpans: $missing_spans,
      missingSpanSources: $missing_span_sources,
      missingMetrics: $missing_metrics,
      missingMetricScopes: $missing_metric_scopes,
      nonpositiveRequiredMetrics: $nonpositive_metrics,
      nonzeroFailureMetrics: $nonzero_failure_metrics,
      unitMismatches: $unit_mismatches,
      missingInformationEvents: $missing_events,
      prohibitedEvents: $prohibited_events,
      severeLogs: $severe_logs,
      unexpectedWarnings: $unexpected_warnings,
      warningDispositions: [
        {eventId: 15, disposition: "accepted", reason: "Container URL binding overrides base-image port variables."},
        {eventId: 35, disposition: "accepted", reason: "Ephemeral acceptance Data Protection keys intentionally have no XML encryptor."},
        {eventId: 103, disposition: "accepted-startup-only", reason: "Health polling begins during bounded startup convergence; retained boundary snapshots must be Healthy."},
        {eventId: 2048, disposition: "accepted-success-only", reason: "The production ingress logs successful SQLite commit/checkpoint boundaries at Warning; SQLite and failure metrics must independently show no failure."}
      ],
      authenticatedExpectedStartupWarnings: $expected_startup_warnings,
      invalidStartupWarnings: $late_startup_warnings,
      invalidExpectedStartupWarnings: $invalid_expected_startup_warnings,
      invalidSqliteSuccessWarnings: $invalid_sqlite_success_warnings,
      unsupportedApplicationLabels: $unsupported_labels,
      excessiveMetricCardinality: $excessive_cardinality,
      invalidRuntimeLabels: $invalid_runtime_labels,
      requiredSeriesExportCoverage: $required_series_coverage,
      privateValueFindings: $private_values,
      privateAttributeKeyFindings: $private_attribute_keys,
      expectedZero: [
        {name: "ingress/lane/processing failures and retries", value: 0,
         proof: "Absent counter series is accepted only with no severe/failure logs and terminal SQLite state."},
        {name: "processing nodes/outputs", disposition: "N/A",
         reason: "The declared v2 processing graph is explicitly empty."},
        {name: "capture-meter", disposition: "N/A",
         reason: "Automatic exposure and gain are disabled."}
      ],
      notApplicableServices: [
        {name: "central-integration", disposition: "N/A", reason: "The standalone physical profile disables central integration."},
        {name: "artifact-outbox", disposition: "N/A", reason: "Upload distribution is disabled for immutable local raw evidence."},
        {name: "fleet", disposition: "N/A", reason: "No central fleet transport is configured in the standalone physical topology."},
        {name: "environmental-acquisition", disposition: "N/A", reason: "No environmental source is configured for the empty physical graph."},
        {name: "event-2012", disposition: "N/A", reason: "The optional telemetry processing step is absent from the empty graph."}
      ],
      usbWireUtilization: {disposition: "N/A", reason: "usbmon is intentionally not enabled; SDK read duration is synchronous read-call throughput only."},
      histogramDeltas: $histogram_deltas,
      measuredWindowReconciliation: {
        telemetryStartUnixMilliseconds: $telemetry_start,
        telemetryEndUnixMilliseconds: $telemetry_end,
        spans: $measured_span_counts,
        logs: $measured_log_counts,
        prometheus: $prometheus_measured,
        otlpCounters: $otlp_measured,
        expectedDeadlineOverrunEvents: $capture.statistics.deadlineOverrunCaptureCount
      }
    },
    passed: (
      $capture.result.passed == true and
      ($capture.profile | IN("asi178mc", "asi676mc")) and
      $host_manifest.profile.id == $capture.profile and
      $trial_manifest.schemaVersion == "issue-268-trial-input-manifest-v1" and
      $trial_manifest.profile == $capture.profile and $trial_manifest.trial == $capture.trial and
      $trial_manifest.revision == $host_manifest.revision.commit and
      $execution_evidence.schemaVersion == "issue-268-trial-execution-v1" and
      $execution_evidence.profile == $capture.profile and $execution_evidence.trial == $capture.trial and
      $execution_evidence.revision == $host_manifest.revision.commit and
      $execution_evidence.wholeTrialElapsedSeconds > 0 and $execution_evidence.wholeTrialElapsedSeconds <= 4800 and
      ($trial_manifest.evidenceIdentitySha256 | type) == "string" and
      ($trial_manifest.evidenceIdentitySha256 | test("^[0-9A-F]{64}$")) and
      $trial_manifest.fileCount == ($trial_manifest.files | length) and
      $trial_manifest.totalBytes == ([$trial_manifest.files[].bytes] | add // 0) and
      ([$trial_manifest.files[].path] | unique | length) == $trial_manifest.fileCount and
      all($trial_manifest.files[]; (.path | type) == "string" and (.path | length) > 0 and
        (.sha256 | test("^[0-9A-F]{64}$")) and .bytes >= 0) and
      $capture.result.warmupCount == 5 and $capture.result.measuredCount == 100 and
      $capture.result.finalHealthStatus == "Healthy" and $capture.result.gracefulExitCode == 0 and
      $capture.boundarySnapshots.acquisitionStart.captureCount == 5 and
      $capture.boundarySnapshots.acquisitionEnd.captureCount == 105 and
      $capture.boundarySnapshots.totalEnd.captureCount == 105 and
      $capture.boundarySnapshots.acquisitionStart.healthStatus == "Healthy" and
      $capture.boundarySnapshots.acquisitionEnd.healthStatus == "Healthy" and
      $capture.boundarySnapshots.totalEnd.healthStatus == "Healthy" and
      $capture.boundarySnapshots.acquisitionStart.completedMonotonicTimestamp < $capture.boundarySnapshots.acquisitionEnd.startedMonotonicTimestamp and
      $capture.boundarySnapshots.acquisitionEnd.completedMonotonicTimestamp < $capture.boundarySnapshots.totalEnd.startedMonotonicTimestamp and
      ($boundary_duration_clock_findings | length) == 0 and ($boundary_interval_clock_findings | length) == 0 and
      all($capture.barriers[]; .authenticated == true) and
      ($capture.barriers | map(.state)) == ["Paused", "Running", "Paused", "Running", "Paused"] and
      all($measured_span_counts[]; . == 100) and
      $measured_log_counts.rawCommit == 100 and $measured_log_counts.laneComplete == 100 and
      $measured_log_counts.processingComplete == 100 and $measured_log_counts.controlDecision == 100 and
      $measured_log_counts.deadlineOverrun == $capture.statistics.deadlineOverrunCaptureCount and
      $prometheus_measured.cycles == 100 and $prometheus_measured.decisions == 100 and
      $prometheus_measured.ingressCommitted == 100 and $prometheus_measured.ingressTransactions == 200 and
      $prometheus_measured.ingressCheckpoints >= 0 and $prometheus_measured.ingressLockWaitCount >= 0 and
      $prometheus_measured.ingressLockWaitSeconds >= 0 and $prometheus_measured.laneWorkCreated == 100 and
      $prometheus_measured.laneClaims == 100 and $prometheus_measured.laneCompleted == 100 and
      $prometheus_measured.processingGraphs == 100 and
      $otlp_measured.cycles == 100 and $otlp_measured.decisions == 100 and
      $otlp_measured.ingressCommitted == 100 and $otlp_measured.ingressTransactions == 200 and
      $otlp_measured.ingressCheckpoints == $prometheus_measured.ingressCheckpoints and
      $otlp_measured.laneWorkCreated == 100 and $otlp_measured.laneClaims == 100 and
      $otlp_measured.laneCompleted == 100 and $otlp_measured.processingGraphs == 100 and
      $total_deltas.ingressCommittedBytes == $capture.statistics.measuredPayloadBytes and
      $capture.sqlite.userVersion == 10 and $dmesg[0].schemaVersion == "issue-268-dmesg-monitor-v2" and
      $dmesg[0].passed == true and $dmesg[0].liveBeforeCameraAgent == true and
      $dmesg[0].followMode == "follow-new" and $dmesg[0].privacySanitized == true and $dmesg[0].oomEvidenceLineCount == 0 and
      $dmesg[0].retainedLineCount == ($dmesg[0].resetDisposition.observedResetCount +
        $dmesg[0].resetDisposition.unexpectedSelectedUsbLineCount + $dmesg[0].oomEvidenceLineCount) and
      $dmesg[0].resetDisposition.passed == true and
      $dmesg[0].resetDisposition.authority == "https://github.com/RoySalisbury/HVO.SkyMonitor/issues/268#issuecomment-5181679194" and
      $dmesg[0].resetDisposition.expectedCaptureCount == 105 and
      $dmesg[0].resetDisposition.unexpectedSelectedUsbLineCount == 0 and
      $dmesg[0].resetDisposition.oomEvidenceLineCount == 0 and
      $dmesg[0].resetDisposition.phaseCorrelationPassed == true and
      (if $capture.profile == "asi676mc" then
        $dmesg[0].resetDisposition.classification == "accepted-sdk-commanded-per-capture" and
        $dmesg[0].resetDisposition.initiatorEvidenceSha256 == "67EE85B0F65C2A5660B3678292F8740FE41C628B73A2EED5E72CF83F5A376FC0" and
        $dmesg[0].resetDisposition.expectedResetCount == 105 and
        $dmesg[0].resetDisposition.observedResetCount == 105 and
        $dmesg[0].resetDisposition.preMeasuredResetCount == 5 and
        $dmesg[0].resetDisposition.measuredResetCount == 100 and
        $dmesg[0].resetDisposition.postMeasuredResetCount == 0
       else
        $dmesg[0].resetDisposition.classification == "zero-reset-required" and
        $dmesg[0].resetDisposition.initiatorEvidenceSha256 == null and
        $dmesg[0].resetDisposition.expectedResetCount == 0 and
        $dmesg[0].resetDisposition.observedResetCount == 0 and
        $dmesg[0].resetDisposition.preMeasuredResetCount == 0 and
        $dmesg[0].resetDisposition.measuredResetCount == 0 and
        $dmesg[0].resetDisposition.postMeasuredResetCount == 0
       end) and
      $host_manifest.mountAuthentication.prestart.passed == true and
      $host_manifest.mountAuthentication.startup.passed == true and
      $host_manifest.mountAuthentication.shutdown.passed == true and
      $host_manifest.mountAuthentication.startup.runtime.writable == true and
      $host_manifest.mountAuthentication.shutdown.runtime.writable == true and
      $host_manifest.startupReadiness.endpoint == "/alive" and
      $host_manifest.startupReadiness.anonymous == true and $host_manifest.startupReadiness.ready == true and
      $host_manifest.startupReadiness.terminalResult == "ready" and
      $host_manifest.startupReadiness.attempts > 0 and $host_manifest.startupReadiness.elapsedSeconds >= 0 and
      $host_manifest.startupReadiness.elapsedSeconds <= 122 and
      $host_manifest.networkIsolation == {mode:"host-to-internal-container-ip", internal:true,
        networkCount:1, publishedHostPorts:false, containerIpRetained:false} and
      $host_manifest.mountAuthentication.startup.runtime.mount.Type == $host_manifest.storage.runtime.containerMount.type and
      $host_manifest.mountAuthentication.startup.runtime.mount.Source == $host_manifest.storage.runtime.containerMount.source and
      $host_manifest.mountAuthentication.startup.runtime.mount.Name == $host_manifest.storage.runtime.containerMount.name and
      $host_manifest.mountAuthentication.startup.runtime.mount.Destination == $host_manifest.storage.runtime.containerMount.destination and
      $host_manifest.mountAuthentication.startup.runtime.mount.RW == true and
      $host_manifest.mountAuthentication.shutdown.runtime.mount == $host_manifest.mountAuthentication.startup.runtime.mount and
      $host_manifest.mountAuthentication.startup.sdkSha256 == $host_manifest.sourceIdentities.sdkLibrarySha256 and
      $host_manifest.mountAuthentication.shutdown.sdkSha256 == $host_manifest.sourceIdentities.sdkLibrarySha256 and
      $host_manifest.mountAuthentication.startup.sdkSource == $host_manifest.mountAuthentication.shutdown.sdkSource and
      $host_manifest.sourceIdentities.sourceCatalogTreeSha256 == $host_manifest.sourceIdentities.copiedCatalogTreeSha256 and
      $host_manifest.centralTraffic.passed == true and $host_manifest.centralTraffic.attemptCount == 0 and
      $host_manifest.centralTraffic.startup.Running == true and $host_manifest.centralTraffic.preStop.Running == true and
      $host_manifest.centralTraffic.shutdown.Running == false and $host_manifest.centralTraffic.shutdown.Status == "exited" and
      ($host_manifest.centralTraffic.shutdown.ExitCode | IN(0, 143)) and $host_manifest.privacy.passed == true and
      $exportIo[0].excludedFromTrialMeasurements == true and
      ($missing_spans | length) == 0 and ($missing_span_sources | length) == 0 and
      ($missing_metrics | length) == 0 and ($missing_metric_scopes | length) == 0 and
      ($nonpositive_metrics | length) == 0 and ($nonzero_failure_metrics | length) == 0 and
      ($unit_mismatches | length) == 0 and ($missing_events | length) == 0 and
      ($prohibited_events | length) == 0 and ($severe_logs | length) == 0 and ($unexpected_warnings | length) == 0 and
      ($late_startup_warnings | length) == 0 and ($invalid_expected_startup_warnings | length) == 0 and
      ([$expected_startup_warnings[].eventId] | sort) == [15, 35] and
      ($invalid_sqlite_success_warnings | length) == 0 and
      ($unsupported_labels | length) == 0 and ($excessive_cardinality | length) == 0 and
      ($invalid_runtime_labels | length) == 0 and ($private_values | length) == 0 and
      ($required_series_coverage | length) >= ($required_metrics | length) and
      (($required_metrics - ([$required_series_coverage[].name] | unique)) | length) == 0 and
      all($required_series_coverage[]; .passed == true) and
      ($private_attribute_keys | length) == 0 and
      all($histogram_deltas[]; length > 0 and all(.[]; .count > 0)) and
      ($window_samples | length) >= ($scheduled_samples * 0.95) and $maximum_gap_ms <= 5000 and
      ($clock_step_findings | length) == 0 and
      ($first_sample.timestampUnixMilliseconds - $start) <= 2000 and ($end - $last_sample.timestampUnixMilliseconds) <= 5000 and
      $rss_growth <= 67108864 and
      ([$window_samples[].backlog.combined] | max) <= 3 and
      ([$window_samples[].backlog.ingress, $window_samples[].backlog.lanes, $window_samples[].backlog.processing] | max) <= 1 and
      ([$window_samples[].backlog.oldestAgeSeconds] | max) < 25 and
      all($window_samples[]; .process.available == true and .containerAvailable == true and
        .centralDeny.available == true and .centralDeny.state.Running == true and
        .centralDeny.state.Status == "running" and .centralDeny.state.OOMKilled == false and
        .usb.present == true and .usb.speedMegabits == 5000) and
      all($window_samples[]; .application.metricsAvailable == true and .application.healthStatus == "Healthy") and
      $acquisition_seconds > 0 and $drain_seconds > 0 and $total_seconds > 0 and
      $process_cpu_seconds >= 0 and ($process_cpu_series | length) >= 2 and
      all($process_cpu_intervals[]; .gapMilliseconds > 0 and .gapMilliseconds <= 5000 and .utilizationPercent >= 0) and
      all(($acquisition_deltas | reset_sensitive_deltas)[]; . >= 0) and
      all(($drain_deltas | reset_sensitive_deltas)[]; . >= 0) and
      all(($total_deltas | reset_sensitive_deltas)[]; . >= 0) and
      ($phase_reconciliation_failures | length) == 0 and
      all([$boundary_start, $boundary_acquisition_end, $boundary_total_end][];
        .hostBlockDevice.name == ($host_manifest.storage.runtime.device | split("/")[-1]) and
        .process.rssBytes >= 0 and .sqliteFiles.databaseBytes >= 0 and
        .sqliteFiles.walBytes >= 0 and .sqliteFiles.shmBytes >= 0) and
      all($window_samples[]; .sqliteFiles.databaseBytes >= 0 and .sqliteFiles.walBytes >= 0 and .sqliteFiles.shmBytes >= 0) and
      $host_cpu_ticks > 0 and $host_idle_ticks >= 0 and $host_idle_ticks <= $host_cpu_ticks and
      $allocated_bytes >= 0 and $gc_pause_seconds >= 0 and $gc_collections >= 0 and
      ($loh_values | length) > 0 and ($poh_values | length) > 0 and ($loh_fragmentation_values | length) > 0 and
      (if $capture.profile == "asi676mc" then all($window_samples[]; .arm.throttled == "0x0") else true end) and
      $capture.sqlite.nonCommittedRawCount == 0 and $capture.sqlite.nonTerminalLaneCount == 0 and
      $capture.sqlite.laneFailureCount == 0 and $capture.sqlite.processingNodeCount == 0 and
      $capture.sqlite.processingOutputCount == 0
    )
  } |
  require(.passed; "trial acceptance failed") |
  .;

def campaign:
  ($trials[0] | sort_by(.boundaries.acquisitionStartUnixMilliseconds)) as $ordered |
  ($campaignManifests[0] | sort_by(.trial)) as $manifests |
  require(($ordered | length) == 5; "campaign requires exactly five trials") |
  require(($manifests | length) == 5; "campaign requires exactly five authenticated post-summary trial manifests") |
  require(([$ordered[].trial] | unique | length) == 5; "campaign trial identifiers must be unique") |
  require(all($ordered[]; (.trial | type) == "string" and (.trial | length) > 0); "campaign trial identifiers must be nonempty strings") |
  require(([$manifests[].trial] | unique | length) == 5 and
    ([$manifests[].evidenceIdentitySha256] | unique | length) == 5;
    "campaign post-summary manifests must have distinct trial and evidence identities") |
  require(all($manifests[]; .evidenceIdentitySha256 | test("^[0-9A-F]{64}$"));
    "campaign post-summary manifest identities must be SHA-256 values") |
  require(all($manifests[];
      .schemaVersion == "issue-268-post-summary-trial-manifest-v1" and
      ([.files[] | select(.path == "evidence/trial-summary.json")] | length) == 1);
    "each campaign manifest must authenticate exactly one retained trial summary") |
  require(all($ordered[]; . as $trial | any($manifests[];
      .schemaVersion == "issue-268-post-summary-trial-manifest-v1" and
      .trial == $trial.trial and .profile == $trial.profile and
      .revision == $trial.revision.commit));
    "campaign trial summaries must match their authenticated post-summary manifests") |
  require(all($ordered[]; .boundaries.acquisitionStartUnixMilliseconds < .boundaries.acquisitionEndUnixMilliseconds and
    .boundaries.acquisitionEndUnixMilliseconds < .boundaries.totalEndUnixMilliseconds); "trial boundaries must be strictly ordered") |
  require(([range(1; $ordered | length) |
    $ordered[. - 1].boundaries.totalEndUnixMilliseconds < $ordered[.].boundaries.acquisitionStartUnixMilliseconds] | all);
    "campaign trial boundaries must not overlap") |
  require(all($ordered[]; .passed == true); "every declared trial must pass") |
  require(([$ordered[].profile] | unique | length) == 1; "profile changed across trials") |
  require(([$ordered[].revision.commit] | unique | length) == 1; "revision changed across trials") |
  require(([$ordered[].sourceIdentities | del(.renderedComposeSha256)] | unique | length) == 1; "source identity changed across trials") |
  require(([$ordered[].host] | unique | length) == 1; "host identity changed across trials") |
  require(([$ordered[].storage | {
    runtime: {kind: .runtime.kind, device: .runtime.device, filesystem: .runtime.filesystem,
      mount: .runtime.mount, mountOptions: .runtime.mountOptions, logicalBlockSize: .runtime.logicalBlockSize,
      containerMount: {type: .runtime.containerMount.type, destination: .runtime.containerMount.destination}},
    evidence: {device: .evidence.device, filesystem: .evidence.filesystem,
      mount: .evidence.mount, mountOptions: .evidence.mountOptions},
    docker: {device: .docker.device, filesystem: .docker.filesystem,
      mount: .docker.mount, mountOptions: .docker.mountOptions}}] | unique | length) == 1; "storage identity changed across trials") |
  {
    schemaVersion: "issue-268-physical-five-trial-summary-v1",
    profile: $ordered[0].profile,
    trialCount: ($ordered | length),
    revision: $ordered[0].revision,
    sourceIdentities: ($ordered[0].sourceIdentities | del(.renderedComposeSha256)),
    host: $ordered[0].host,
    storage: $ordered[0].storage,
    trialStorageTopologies: ($ordered | map({trial, storage})),
    centralTraffic: {attemptCount: ([$ordered[].centralTraffic.attemptCount] | add),
      allPassed: all($ordered[]; .centralTraffic.passed == true)},
    readiness: {authenticatedPostSummaryManifestCount: ($manifests | length),
      allTrialSummariesBound: true, allTrialsPassed: true},
    scopeDisposition: {
      sustainedPhysicalAcquisition: {disposition: "included", reason: "This campaign measures the declared local CameraAgent capture, ingress, lane, and empty processing path."},
      centralIntegration: {disposition: "N/A", reason: "Standalone profiles deny central traffic; central services are outside the physical stratum."},
      physicalCalibration: {disposition: "N/A", reason: "Issue #268 records performance and durability evidence without changing or validating calibration."},
      maximumUsbSaturation: {disposition: "N/A", reason: "usbmon and saturation workloads are intentionally outside the declared sustained cadence."},
      crossCameraRegression: {disposition: "prohibited", reason: "Camera, payload, host, storage, optics, and scene differ between strata."}
    },
    trialStatistics: {
      startIntervalMedianSeconds: ([$ordered[].statistics.startIntervalSeconds.median] | trial_range),
      startIntervalP95Seconds: ([$ordered[].statistics.startIntervalSeconds.p95] | trial_range),
      startIntervalMaximumSeconds: ([$ordered[].statistics.startIntervalSeconds.maximum] | trial_range),
      readoutMedianSeconds: ([$ordered[].statistics.readoutSeconds.median] | trial_range),
      readoutP95Seconds: ([$ordered[].statistics.readoutSeconds.p95] | trial_range),
      readoutMaximumSeconds: ([$ordered[].statistics.readoutSeconds.maximum] | trial_range),
      synchronousSdkReadCallMedianBytesPerSecond: ([$ordered[].statistics.synchronousSdkReadCallBytesPerSecond.median] | trial_range),
      ingressMedianSeconds: ([$ordered[].statistics.readoutToDurableSeconds.median] | trial_range),
      ingressP95Seconds: ([$ordered[].statistics.readoutToDurableSeconds.p95] | trial_range),
      ingressMaximumSeconds: ([$ordered[].statistics.readoutToDurableSeconds.maximum] | trial_range),
      cycleMedianSeconds: ([$ordered[].statistics.startToDurableSeconds.median] | trial_range),
      cycleP95Seconds: ([$ordered[].statistics.startToDurableSeconds.p95] | trial_range),
      cycleMaximumSeconds: ([$ordered[].statistics.startToDurableSeconds.maximum] | trial_range),
      rssGrowthBytes: ([$ordered[].resources.rssFinal20MinusInitial20Bytes] | trial_range),
      processCpuSeconds: ([$ordered[].resources.process.cpuSeconds] | trial_range),
      measuredThroughputCapturesPerSecond: ([$ordered[].resources.measuredThroughputCapturesPerSecond] | trial_range),
      processCoreEquivalentUtilizationPercent: ([$ordered[].resources.process.coreEquivalentUtilizationPercent] | trial_range),
      hostCpuUtilizationPercent: ([$ordered[].resources.hostCpuUtilizationPercent] | trial_range),
      processReadBytes: ([$ordered[].resources.process.readBytes] | trial_range),
      processWriteBytes: ([$ordered[].resources.process.writeBytes] | trial_range),
      processRssAcquisitionStartBytes: ([$ordered[].resources.process.rssBoundaryBytes.acquisitionStart] | trial_range),
      processRssAcquisitionEndBytes: ([$ordered[].resources.process.rssBoundaryBytes.acquisitionEnd] | trial_range),
      processRssTotalEndBytes: ([$ordered[].resources.process.rssBoundaryBytes.totalEnd] | trial_range),
      allocatedBytes: ([$ordered[].resources.runtime.allocatedBytes] | trial_range),
      gcPauseSeconds: ([$ordered[].resources.runtime.gcPauseSeconds] | trial_range),
      blockReadBytes: ([$ordered[].resources.blockDevice.readBytes] | trial_range),
      blockWriteBytes: ([$ordered[].resources.blockDevice.writeBytes] | trial_range),
      containerBlockReadBytes: ([$ordered[].resources.containerIo.blockReadBytes] | trial_range),
      containerBlockWriteBytes: ([$ordered[].resources.containerIo.blockWriteBytes] | trial_range),
      containerNetworkReadBytes: ([$ordered[].resources.containerIo.networkReadBytes] | trial_range),
      containerNetworkWriteBytes: ([$ordered[].resources.containerIo.networkWriteBytes] | trial_range),
      sqliteDatabaseGrowthBytes: ([$ordered[].resources.sqliteFiles.databaseGrowthBytes] | trial_range),
      sqliteWalGrowthBytes: ([$ordered[].resources.sqliteFiles.walGrowthBytes] | trial_range),
      sqliteShmGrowthBytes: ([$ordered[].resources.sqliteFiles.shmGrowthBytes] | trial_range),
      cameraTemperatureStartC: ([$ordered[].statistics.cameraTemperatureC.start] | trial_range),
      cameraTemperatureEndC: ([$ordered[].statistics.cameraTemperatureC.end] | trial_range),
      cameraTemperatureSlopeDegreesCPerHour: ([$ordered[].statistics.cameraTemperatureC.theilSenDegreesCPerHour] | trial_range)
    },
    trials: ([$ordered[] as $trial |
      ($manifests[] | select(.trial == $trial.trial)) as $manifest |
      $trial | {trial, trialEvidenceIdentity: $manifest.evidenceIdentitySha256, trialInputIdentity,
        boundaries, statistics, mountAuthentication, startupReadiness, networkIsolation, centralTraffic,
        resources: (.resources | del(.samples)), telemetry: (.telemetry | del(.privateValueFindings)), passed}]),
    crossCameraComparison: {
      permitted: false,
      reason: "P178-X64-v1 and P676-ARM64-v1 are separate absolute baselines with confounded sensors, frame sizes, hosts, USB controllers, storage, optics, and scenes."
    },
    allPassed: true,
    citable: true
  };

if $mode == "trial" then trial
elif $mode == "campaign" then campaign
elif $mode == "compile" then {schemaVersion: "issue-268-aggregate-compile-v1", passed: true}
else fail("unsupported aggregation mode")
end
