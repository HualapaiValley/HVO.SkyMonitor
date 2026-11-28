# CameraAgent archived replay runner

CameraAgent uses the in-process executor for live work. Its optional replay
runner handles explicitly requested archived replay through a local Unix socket.
The CameraAgent instance owns job state, lease decisions and publication.
[Local archived replay](deployment-installer.md#local-replay-runner).

The former central runner and its transport are recorded in the [historical runbook](https://github.com/HualapaiValley/HVO.SkyMonitor/blob/66e6aa237b40c70342815af6e2511122daa56bb6/docs/runbooks/processing-runner.md).
