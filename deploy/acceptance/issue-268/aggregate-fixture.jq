def attr($key; $value): {key: $key, value: {stringValue: $value}};
def point($time; $value; $attrs):
  {timeUnixNano: (($time * 1000000) | tostring), asDouble: $value, attributes: $attrs};
def counter($name; $unit; $end; $attrs):
  {name: $name, unit: $unit, sum: {dataPoints:
    [range(0; 57) | point(99000 + (. * 2000); ($end * . / 56); $attrs)]}};
def cpu_counter($mode; $end):
  {name: "process.cpu.time", unit: "s", sum: {dataPoints:
    [range(0; 57) | point(99000 + (. * 2000); ($end * . / 56); [attr("cpu.mode"; $mode)])]}};
def gauge($name; $unit; $value; $attrs):
  {name: $name, unit: $unit, gauge: {dataPoints:
    [range(0; 57) | point(99000 + (. * 2000); $value; $attrs)]}};
def histogram($name; $unit; $attrs):
  {name: $name, unit: $unit, histogram: {dataPoints:
    [range(0; 57) as $index | point(99000 + ($index * 2000); 0; $attrs) +
      {count: ($index | tostring), explicitBounds: [1], bucketCounts: [($index | tostring), "0"]}]}};
def metric_scope($name; $metrics): {scope: {name: $name}, metrics: $metrics};

def prom($cycles; $transactions; $checkpoints; $lockCount; $lockSeconds): {
  camera_agent_capture_control_cycles: $cycles,
  camera_agent_capture_control_decisions: $cycles,
  camera_agent_ingress_committed: $cycles,
  camera_agent_ingress_sqlite_transactions: $transactions,
  camera_agent_ingress_sqlite_checkpoints: $checkpoints,
  camera_agent_ingress_sqlite_lock_wait_duration_seconds_count: $lockCount,
  camera_agent_ingress_sqlite_lock_wait_duration_seconds_sum: $lockSeconds,
  camera_agent_lanes_work_created: $cycles,
  camera_agent_lanes_claims: $cycles,
  camera_agent_lanes_completed: $cycles,
  camera_agent_processing_graphs: $cycles
};
def snapshot($captured; $started; $completed; $count; $prometheus): {
  capturedUnixMilliseconds: $captured,
  startedMonotonicTimestamp: $started,
  completedMonotonicTimestamp: $completed,
  completedUnixMilliseconds: ($captured + ($completed - $started)),
  captureCount: $count,
  healthStatus: "Healthy",
  process: {rssBytes: (104857600 + $captured), readBytes: $captured, writeBytes: (2 * $captured), readSyscalls: ($captured / 10),
    writeSyscalls: ($captured / 5)},
  dockerStats: {BlockIO: (($captured | tostring) + "B / " + (2 * $captured | tostring) + "B"),
    NetIO: ((3 * $captured | tostring) + "B / " + (4 * $captured | tostring) + "B")},
  hostBlockDevice: {name: "sda1", stat: ("8 0 " + ($captured / 1000 | tostring) + " 0 0 0 " +
    ($captured / 1000 | tostring) + " 0 0 " + ($captured / 1000 | tostring) + " 0")},
  sqliteFiles: {databaseBytes: $captured, walBytes: ($captured / 10), shmBytes: 32768},
  prometheusCounters: $prometheus
};

def resource_sample($index): {
  timestampUnixMilliseconds: (100000 + ($index * 2000)),
  monotonicTimestampMilliseconds: (500000 + ($index * 2000)),
  process: {available: true, rssBytes: 104857600, cpuTicks: (1000 + $index), clockTicksPerSecond: 100,
    readBytes: (1000 + $index), writeBytes: (2000 + $index), readSyscalls: (100 + $index), writeSyscalls: (200 + $index)},
  containerAvailable: true,
  centralDeny: {available: true, state: {Running: true, Status: "running", OOMKilled: false, Error: ""}},
  container: {BlockIO: (($index | tostring) + "B / " + ($index | tostring) + "B"),
    NetIO: (($index | tostring) + "B / " + ($index | tostring) + "B")},
  host: {blockDeviceStat: ("8 0 " + ($index | tostring) + " 0 0 0 " + ($index | tostring) + " 0 0 " + ($index | tostring) + " 0"),
    stat: ("cpu " + (100 + $index | tostring) + " 0 0 " + (100 + $index | tostring) + " 0"),
    availableMemoryBytes: 8000000000, loadAverage: "0.10 0.20 0.30"},
  sqliteFiles: {databaseBytes: (4096 + $index), walBytes: $index, shmBytes: 32768},
  backlog: {combined: 0, ingress: 0, lanes: 0, processing: 0, oldestAgeSeconds: 0},
  usb: {present: true, speedMegabits: 5000},
  arm: {throttled: "0x0"},
  application: {metricsAvailable: true, healthStatus: "Healthy"}
};

def app_metrics: [
  counter("camera_agent.capture_control.cycles"; "{cycle}"; 100; []),
  counter("camera_agent.capture_control.decisions"; "{decision}"; 100; []),
  histogram("camera_agent.capture_control.start_jitter"; "s"; []),
  histogram("camera_agent.capture_control.cycle.duration"; "s"; []),
  histogram("camera_agent.capture_control.segment.duration"; "s"; []),
  histogram("camera_agent.capture_control.decision.duration"; "s"; []),
  counter("camera_agent.ingress.committed"; "{capture}"; 100; []),
  counter("camera_agent.ingress.committed.bytes"; "By"; 1000; []),
  histogram("camera_agent.ingress.commit.duration"; "s"; []),
  counter("camera_agent.ingress.sqlite.transactions"; "{transaction}"; 200; [attr("result"; "success")]),
  counter("camera_agent.ingress.sqlite.checkpoints"; "{checkpoint}"; 1; [attr("result"; "success")]),
  histogram("camera_agent.ingress.sqlite.lock_wait.duration"; "s"; []),
  gauge("camera_agent.ingress.pending"; "{capture}"; 0; []),
  gauge("camera_agent.ingress.pending.bytes"; "By"; 0; []),
  gauge("camera_agent.ingress.oldest.age"; "s"; 0; []),
  counter("camera_agent.lanes.work.created"; "{work}"; 100; []),
  counter("camera_agent.lanes.claims"; "{claim}"; 100; [attr("result"; "claimed")]),
  counter("camera_agent.lanes.claims"; "{claim}"; 10; [attr("result"; "empty")]),
  counter("camera_agent.lanes.completed"; "{work}"; 100; []),
  histogram("camera_agent.lanes.claim.duration"; "s"; [attr("result"; "claimed")]),
  histogram("camera_agent.lanes.claim.duration"; "s"; [attr("result"; "empty")]),
  histogram("camera_agent.lanes.processing.duration"; "s"; []),
  histogram("camera_agent.lanes.ack.duration"; "s"; []),
  gauge("camera_agent.lanes.pending"; "{work}"; 0; []),
  gauge("camera_agent.lanes.pending.bytes"; "By"; 0; []),
  gauge("camera_agent.lanes.oldest.age"; "s"; 0; []),
  gauge("camera_agent.lanes.leased"; "{lease}"; 0; []),
  counter("camera_agent.processing.graphs"; "{graph}"; 100; [attr("target"; "local")]),
  histogram("camera_agent.processing.graph.duration"; "s"; []),
  gauge("camera_agent.processing.pending"; "{graph}"; 0; []),
  gauge("camera_agent.processing.oldest.age"; "s"; 0; [])
];

def runtime_metrics: [
  counter("dotnet.gc.heap.total_allocated"; "By"; 1000; []),
  gauge("dotnet.gc.last_collection.heap.size"; "By"; 1000; [attr("dotnet.gc.heap.generation.name"; "loh")]),
  gauge("dotnet.gc.last_collection.heap.size"; "By"; 100; [attr("dotnet.gc.heap.generation.name"; "poh")]),
  gauge("dotnet.gc.last_collection.heap.fragmentation.size"; "By"; 10; [attr("dotnet.gc.heap.generation.name"; "loh")]),
  counter("dotnet.gc.collections"; "{collection}"; 1; [attr("dotnet.gc.heap.generation.name"; "gen0")]),
  counter("dotnet.gc.collections"; "{collection}"; 1; [attr("dotnet.gc.heap.generation.name"; "gen1")]),
  counter("dotnet.gc.collections"; "{collection}"; 1; [attr("dotnet.gc.heap.generation.name"; "gen2")]),
  counter("dotnet.gc.pause.time"; "s"; 0.1; []),
  cpu_counter("user"; 5.6),
  cpu_counter("system"; 2.8),
  gauge("process.memory.working_set"; "By"; 104857600; [])
];

def span($name; $index; $result):
  {name: $name, startTimeUnixNano: ((101000 + $index) * 1000000 | tostring),
   endTimeUnixNano: ((101001 + $index) * 1000000 | tostring),
   attributes: (if $result == null then [] else [attr("result"; $result)] end)};
def spans:
  [
    ["HVO.SkyMonitor.CameraAgent.CaptureControl", "capture-cycle"],
    ["HVO.SkyMonitor.CameraAgent.CaptureControl", "capture-control"],
    ["HVO.SkyMonitor.CameraAgent.CaptureControl", "capture-ingress-handoff"],
    ["HVO.SkyMonitor.CameraAgent.RawIngress", "raw-ingress.accept"],
    ["HVO.SkyMonitor.CameraAgent.RawIngress", "payload.publish"],
    ["HVO.SkyMonitor.CameraAgent.RawIngress", "sidecar.publish"],
    ["HVO.SkyMonitor.CameraAgent.RawIngress", "sqlite.commit"],
    ["HVO.SkyMonitor.CameraAgent.CaptureLanes", "capture-lanes.claim"],
    ["HVO.SkyMonitor.CameraAgent.CaptureLanes", "capture-lanes.process"],
    ["HVO.SkyMonitor.CameraAgent.CaptureLanes", "capture-lanes.ack"],
    ["HVO.SkyMonitor.CameraAgent.ProcessingGraph", "processing-graph.validate"],
    ["HVO.SkyMonitor.CameraAgent.ProcessingGraph", "processing-graph.execute"]
  ] | map(. as $pair | {scope: {name: $pair[0]}, spans:
    (if $pair[1] == "capture-lanes.claim" then
      ([range(0; 100) | span($pair[1]; .; "claimed")] + [range(100; 110) | span($pair[1]; .; "empty")])
     else [range(0; 100) | span($pair[1]; .; null)] end)});

def log_record($event; $index): {
  timeUnixNano: ((102000 + $index) * 1000000 | tostring), severityNumber: 9, severityText: "Information",
  body: {stringValue: "fixture"}, attributes: [attr("EventId"; ($event | tostring))]
};
def logs:
  ([2042, 2054, 2072, 2073] | map(. as $event | [range(0; 100) | log_record($event; .)]) | add) +
  ([2040, 2041, 2050, 2058, 2064] | map(log_record(.; 0))) +
  [{timeUnixNano: "80000000000", severityNumber: 13, severityText: "Warning", body: {stringValue: "[REDACTED]"},
    attributes: [attr("EventId"; "15"), attr("SourceContext"; "Microsoft.AspNetCore.Hosting.Diagnostics")]},
   {timeUnixNano: "85000000000", severityNumber: 13, severityText: "Warning", body: {stringValue: "[REDACTED]"},
    attributes: [attr("EventId"; "35"), attr("SourceContext"; "Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager")]},
   {timeUnixNano: "90000000000", severityNumber: 13, severityText: "Warning", body: {stringValue: "startup convergence"},
    attributes: [attr("EventId"; "103")]},
   {timeUnixNano: "150000000000", severityNumber: 13, severityText: "Warning",
    body: {stringValue: "Raw ingress SQLite operation commit completed with result success"},
    attributes: [attr("EventId"; "2048"), attr("Operation"; "commit"), attr("Result"; "success")]}];

{
  acquisition: [{
    profile: "asi178mc", trial: "trial-1",
    result: {passed: true, warmupCount: 5, measuredCount: 100, finalHealthStatus: "Healthy", gracefulExitCode: 0},
    boundaries: {acquisitionStartUnixMilliseconds: 100000, acquisitionEndUnixMilliseconds: 200000, totalEndUnixMilliseconds: 210000},
    statistics: {deadlineOverrunCaptureCount: 0, measuredPayloadBytes: 1000,
      startIntervalSeconds: {median: 1, p95: 1, maximum: 1},
      readoutSeconds: {median: 1, p95: 1, maximum: 1}, synchronousSdkReadCallBytesPerSecond: {median: 1},
      readoutToDurableSeconds: {median: 1, p95: 1, maximum: 1}, startToDurableSeconds: {median: 1, p95: 1, maximum: 1},
      cameraTemperatureC: {start: 1, end: 1, theilSenDegreesCPerHour: 0}},
    sqlite: {userVersion: 10, nonCommittedRawCount: 0, nonTerminalLaneCount: 0, laneFailureCount: 0,
      processingNodeCount: 0, processingOutputCount: 0},
    monotonicFrequency: 1000,
    boundarySnapshots: {
      acquisitionStart: snapshot(100000; 100000; 100001; 5; prom(0; 0; 0; 0; 0)),
      acquisitionEnd: snapshot(200000; 200000; 200001; 105; prom(100; 200; 0; 0; 0)),
      totalEnd: snapshot(210000; 210000; 210001; 105; prom(100; 200; 1; 1; 0.01))
    },
    barriers: [{authenticated: true, state: "Paused"}, {authenticated: true, state: "Running"},
      {authenticated: true, state: "Paused"}, {authenticated: true, state: "Running"},
      {authenticated: true, state: "Paused"}]
  }],
  host: [{
    revision: {commit: "fixture"}, sourceIdentities: {renderedComposeSha256: "fixture",
      sdkLibrarySha256: "fixture-sdk", sourceCatalogTreeSha256: "fixture-tree", copiedCatalogTreeSha256: "fixture-tree"},
    host: {logicalCpuCount: 8},
    storage: {runtime: {logicalBlockSize: 512, path: "runtime", device: "/dev/sda1", volumeInspect: {}, freeBytes: 1,
        containerMount: {type: "volume", source: "runtime", name: "fixture", destination: "/var/lib/hvo/data/agent"}},
      evidence: {freeBytes: 1}, docker: {freeBytes: 1}},
    mountAuthentication: {prestart: {passed: true},
      startup: {passed: true, sdkSha256: "fixture-sdk", sdkSource: "fixture-sdk-source",
        runtime: {writable: true, mount: {Type: "volume", Source: "runtime", Name: "fixture", Destination: "/var/lib/hvo/data/agent", RW: true}}},
      shutdown: {passed: true, sdkSha256: "fixture-sdk", sdkSource: "fixture-sdk-source",
        runtime: {writable: true, mount: {Type: "volume", Source: "runtime", Name: "fixture", Destination: "/var/lib/hvo/data/agent", RW: true}}}},
    startupReadiness: {endpoint: "/alive", anonymous: true, ready: true, attempts: 1,
      elapsedSeconds: 1, terminalResult: "ready"},
    centralTraffic: {passed: true, attemptCount: 0,
      startup: {Running: true, Status: "running", OOMKilled: false, Error: ""},
      preStop: {Running: true, Status: "running", OOMKilled: false, Error: ""},
      shutdown: {Running: false, Status: "exited", ExitCode: 143, OOMKilled: false, Error: ""}},
    privacy: {passed: true}
  }],
  resources: [range(0; 56) | resource_sample(.)],
  metrics: [{resourceMetrics: [{scopeMetrics: [
    metric_scope("HVO.SkyMonitor.CameraAgent.CaptureControl"; [app_metrics[] | select(.name | startswith("camera_agent.capture_control"))]),
    metric_scope("HVO.SkyMonitor.CameraAgent.RawIngress"; [app_metrics[] | select(.name | startswith("camera_agent.ingress"))]),
    metric_scope("HVO.SkyMonitor.CameraAgent.CaptureLanes"; [app_metrics[] | select(.name | startswith("camera_agent.lanes"))]),
    metric_scope("HVO.SkyMonitor.CameraAgent.ProcessingGraph"; [app_metrics[] | select(.name | startswith("camera_agent.processing"))]),
    metric_scope("OpenTelemetry.Instrumentation.Runtime"; runtime_metrics)
  ]}]}],
  traces: [{resourceSpans: [{scopeSpans: spans}]}],
  logs: [{resourceLogs: [{scopeLogs: [{scope: {name: "fixture"}, logRecords: logs}]}]}],
  dmesg: [{passed: true, liveBeforeCameraAgent: true, followMode: "follow-new",
    privacySanitized: true, oomEvidenceLineCount: 0}],
  exportIo: [{excludedFromTrialMeasurements: true}]
  ,execution: [{schemaVersion: "issue-268-trial-execution-v1", revision: "fixture", profile: "asi178mc",
    trial: "trial-1", wholeTrialElapsedSeconds: 120}]
  ,trialManifests: [{schemaVersion: "issue-268-trial-input-manifest-v1", revision: "fixture",
    profile: "asi178mc", trial: "trial-1", evidenceIdentitySha256: ("A" * 64),
    fileCount: 1, totalBytes: 1, files: [{path: "input", bytes: 1, sha256: ("B" * 64)}]}]
}
