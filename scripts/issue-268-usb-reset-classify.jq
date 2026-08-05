def fail($message): error("issue #268 USB reset evidence: " + $message);
def require($condition; $message): if $condition then . else fail($message) end;

($capture | length == 1) as $single_capture |
require($single_capture; "exactly one capture evidence document is required") |
require(($profile | IN("asi178mc", "asi676mc")); "profile is not allowlisted") |
($capture[0]) as $evidence |
require($evidence.profile == $profile; "capture profile mismatch") |
require($evidence.result.passed == true; "capture evidence did not pass") |
($evidence.result.warmupCount + $evidence.result.measuredCount) as $capture_count |
require($capture_count == 105; "capture cardinality must be exactly 105") |
($evidence.monotonicFrequency) as $frequency |
require($frequency > 0; "monotonic frequency must be positive") |
([$evidence.barriers[] | select(.phase == "measured-resume")]) as $resume |
([$evidence.barriers[] | select(.phase == "measured-complete")]) as $complete |
require(($resume | length) == 1 and ($complete | length) == 1; "measured barriers must be unique") |
($resume[0].startedMonotonicTimestamp / $frequency) as $measured_start |
($complete[0].startedMonotonicTimestamp / $frequency) as $measured_end |
require($measured_start < $measured_end; "measured monotonic interval is invalid") |
($token | gsub("\\."; "\\.")) as $escaped_token |
("\\[[[:space:]]*(?<timestamp>[0-9]+\\.[0-9]+)\\][[:space:]]+usb " + $escaped_token +
  ": reset SuperSpeed USB device number [0-9]+ using xhci-hcd$") as $canonical_pattern |
(split("\n") | map(select(length > 0))) as $lines |
([$lines[] | select(test("oom|out of memory|killed process"; "i"))]) as $oom_lines |
([$lines[] | select(contains("usb " + $token + ":"))]) as $selected_usb_lines |
([$selected_usb_lines[] | try (capture($canonical_pattern).timestamp | tonumber) catch empty]) as $reset_timestamps |
(($selected_usb_lines | length) - ($reset_timestamps | length)) as $unexpected_usb_lines |
([$reset_timestamps[] | select(. < $measured_start)] | length) as $pre_measured_resets |
([$reset_timestamps[] | select(. >= $measured_start and . <= $measured_end)] | length) as $measured_resets |
([$reset_timestamps[] | select(. > $measured_end)] | length) as $post_measured_resets |
(if $profile == "asi676mc" then $capture_count else 0 end) as $expected_resets |
(if $profile == "asi676mc" then
   $pre_measured_resets == $evidence.result.warmupCount and
   $measured_resets == $evidence.result.measuredCount and
   $post_measured_resets == 0
 else
   ($reset_timestamps | length) == 0
 end) as $phase_correlation_passed |
{
  classification: (if $profile == "asi676mc" then "accepted-sdk-commanded-per-capture" else "zero-reset-required" end),
  authority: "https://github.com/RoySalisbury/HVO.SkyMonitor/issues/268#issuecomment-5181679194",
  initiatorEvidenceSha256: (if $profile == "asi676mc" then
    "67EE85B0F65C2A5660B3678292F8740FE41C628B73A2EED5E72CF83F5A376FC0" else null end),
  expectedCaptureCount: $capture_count,
  expectedResetCount: $expected_resets,
  observedResetCount: ($reset_timestamps | length),
  preMeasuredResetCount: $pre_measured_resets,
  measuredResetCount: $measured_resets,
  postMeasuredResetCount: $post_measured_resets,
  unexpectedSelectedUsbLineCount: $unexpected_usb_lines,
  oomEvidenceLineCount: ($oom_lines | length),
  retainedLineCount: ($lines | length),
  phaseCorrelationPassed: $phase_correlation_passed,
  passed: (($reset_timestamps | length) == $expected_resets and $unexpected_usb_lines == 0 and
    ($oom_lines | length) == 0 and $phase_correlation_passed)
}
