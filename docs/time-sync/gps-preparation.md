# CameraAgent GPS/PPS time source

The **Time** panel on System control configures a read-only serial receiver on Linux x64 or ARM64. Network time remains enabled. When no time server answers, a compatible local receiver can verify the host clock offline. The host operating system owns clock discipline.

Choose **Network and GPS receiver**, enter the device path as seen inside CameraAgent, select the receiver baud rate, and save. CameraAgent opens only that tty. Use an existing device directly under `/dev` or a numbered `/dev/pts` pseudo-terminal. The final path component must be a device node. The deployment must already provide access to it. The reader uses raw 8N1 at 4800, 9600, 38400 or 115200 baud. Choose **Network time servers** to release the receiver.

The operator file stores `Enabled`, `Device`, `BaudRate` and `UsePps` under `CameraAgent:TimeSync:Gps`. Missing settings default to GPS off, an empty device, 9600 baud and PPS off. The host checks configuration every second. An invalid setting prevents device access. Saving requires operations-change access, an editable settings file and its current version. A higher-precedence GPS setting makes the form read-only. A host without the reader keeps its unsupported explanation.

## Receiver evidence

Checksummed GP/GN RMC provides date, UTC and fix validity. The reader accepts legacy status A or measured modes A/D/P/R/F. Checksummed GGA reports fix quality and satellite count. Quality zero suppresses the fix. Each sentence has an 82-character limit. The serial worker reads at most 512 bytes per poll and keeps incomplete lines across polls. An invalid or overlong line is discarded through LF. No raw receiver traffic enters the logs or the operator file.

**Host offset at NMEA arrival** includes serial latency. The panel shows GPS fix state, satellites, quality and last sentence independently of clock health. RMC and GGA expire independently after five seconds of monotonic time. A host clock step immediately changes the projected offset. Disconnect or device replacement clears the whole session. An unavailable device is retried every second. Disabled clock checking releases the device.

This release accepts receiver UTC from 2026 through 2035, independently of host UTC. The supported deployment era is shorter than one 1024-week GPS rollover. Operation beyond this era requires an updated receiver date window.

## PPS receiver contract

Enable **Use rising DCD PPS** only for a receiver and adapter with these characteristics.

- The rising carrier-detect transition at the configured tty is within 5 ms of the UTC second.
- That transition is followed by an integral-second RMC naming the same UTC second. The line begins after the pulse and finishes within 800 ms.
- The DCD pulse is long enough to observe at a nominal 10 ms sampling interval. Unavailable DCD still permits NMEA diagnostics.

The reader brackets the edge with a low and a high observation. It discards ambiguous edges when serial bytes are already queued at the first high observation, or when the bracket exceeds 100 ms. Initial high, fractional RMC, a sentence spanning the edge, estimated/manual/simulated fixes, and an undated or stale edge cannot verify the clock. The uncertainty allowance includes the whole observation bracket, the 5 ms signal delivery budget and the time spent sampling the host clock. The reported offset is projected against the current host clock from the observed high.

A successful SNTP result retains precedence. Offline, the entire PPS offset interval must fit inside the configured clock tolerance for GPS to establish healthy time. A definitely out-of-tolerance offset reports drift. An explicitly unsynchronized kernel prevents a healthy in-tolerance result. Missing or inconclusive GPS evidence falls back to the existing kernel assessment.

## Cancellation and software validation

`GpsTimeReader.ReadAsync` accepts a caller-owned stream that honors cancellation. Each round consumes at most 4096 bytes within two seconds, including time queued. Stream faults invalidate evidence. Caller cancellation preserves the caller token. Deadline expiry alone returns normally. Disposal immediately revokes diagnostics and cancels active and queued reads. A pending read canceled by disposal throws `ObjectDisposedException` for the reader. Gate and token disposal wait until the last admitted read unwinds. Reads begun after disposal never touch the stream.

Hardware-free tests cover the production monitor with simulated bytes, modem transitions, hot unplug, reopening, configuration changes and shutdown. A Linux pseudo-terminal exercises the native tty adapter. Physical qualification remains open under [#1710](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1710). Record the receiver, adapter, baud, pulse polarity and width, RMC association, worst-case signal delivery latency, deployment permissions, measured offset against an independent reference, and disconnect/reconnect behavior before qualifying a particular installation.
