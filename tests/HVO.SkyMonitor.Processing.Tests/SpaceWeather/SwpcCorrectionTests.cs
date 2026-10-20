using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.Processing.Tests.SpaceWeather;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class SwpcCorrectionTests
{
    public TestContext TestContext { get; set; } = null!;
    private const string OriginalProbabilityPattern = @"\A([^ \t\r\n\f\v].*?) {2,}([0-9]{1,3})% +([0-9]{1,3})% +([0-9]{1,3})%\z";

    [TestMethod]
    public void AdversarialProbabilityRowRejectsPromptlyWithoutTimeout()
    {
        var row = "X" + new string(' ', 60000) + "9999% 1% 1%";
        var bytes = Forecast(row);
        Assert.IsLessThanOrEqualTo(SwpcLimits.SmallProductBytes, bytes.Length);
        var timer = Stopwatch.StartNew();
        Assert.AreEqual("schema", SwpcThreeDayForecastParser.Parse(bytes, "text/plain").RejectionReason);
        TestContext.WriteLine($"F1 new parser 60000 spaces: {timer.Elapsed.TotalMilliseconds:F3} ms");
        Assert.IsLessThan(TimeSpan.FromSeconds(2), timer.Elapsed);
        timer.Restart();
        var timedOut = false;
        try { Assert.IsFalse(Regex.IsMatch(row, OriginalProbabilityPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))); }
        catch (RegexMatchTimeoutException) { timedOut = true; }
        TestContext.WriteLine($"F1 reviewed regex 60000 spaces: {timer.Elapsed.TotalMilliseconds:F3} ms, timeout={timedOut}");
    }

    [TestMethod]
    public void ProbabilityRowsMatchReviewedRegexOverGeneratedGrammar()
    {
        // F:616,689–694: ASCII \S and [0-9], shortest successful label, 1–3 digits.
        var oracle = new Regex(OriginalProbabilityPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        foreach (var label in new[] { "X", "R1-R2", "A  B", "A  12%", "A%  1", "\u00a0", "\u001c", "\tX", " X", "X\tY" })
            foreach (var separator in new[] { " ", "  ", "     ", "\t\t" })
                foreach (var values in new[] { "0% 1% 100%", "001% 99% 999%", "9999% 1% 1%", "1%  2%   3%", "1% 2% 3% x", "١% 2% 3%", "1%\t2% 3%" })
                {
                    var row = label + separator + values;
                    var expected = oracle.Match(row);
                    var actual = SwpcThreeDayForecastParser.Parse(Forecast(row), "text/plain");
                    Assert.AreEqual(expected.Success, actual.Accepted, JsonSerializer.Serialize(row));
                    if (!expected.Success) { Assert.AreEqual("schema", actual.RejectionReason); continue; }
                    var table = actual.Records.Single().RadiationProbabilities;
                    Assert.AreEqual(expected.Groups[1].Value, table.Keys.Single());
                    CollectionAssert.AreEqual(Enumerable.Range(2, 3).Select(i => expected.Groups[i].Value).ToArray(), table.Single().Value.OrderBy(p => p.Key).Select(p => p.Value).ToArray());
                }
    }

    [TestMethod]
    public void MaximumWatchEvaluationIsBoundedAndMatchesSmallBaseline()
    {
        var groups = SwpcGroupBuilder.Build(Enumerable.Range(1, 2000).Select(i => new SwpcMessageVersion(Message(i, Epoch.AddMinutes(i), SpaceWeatherFamily.Watch, "G3"), i)).ToArray());
        var touched = groups.SelectMany(g => g.Nodes).ToImmutableHashSet();
        var small = groups.Take(250).ToImmutableArray();
        var input = new SwpcAttentionInput(small, touched, ImmutableDictionary<SwpcMessageNode, int>.Empty, Epoch.AddDays(5));
        var timer = Stopwatch.StartNew();
        var expected = OriginalAttentionEvaluator.Evaluate(input);
        TestContext.WriteLine($"F2 reviewed algorithm N=250: {timer.Elapsed.TotalMilliseconds:F3} ms; cubic N=2000 extrapolation factor=512");
        timer.Restart();
        Equivalent(expected, SwpcAttentionEvaluator.Evaluate(input));
        TestContext.WriteLine($"F2 new algorithm N=250: {timer.Elapsed.TotalMilliseconds:F3} ms");
        timer.Restart();
        var result = SwpcAttentionEvaluator.Evaluate(input with { Groups = groups });
        TestContext.WriteLine($"F2 new algorithm N=2000: {timer.Elapsed.TotalMilliseconds:F3} ms, events={result.Events.Length}, raised={result.RaisedRanks.Count}");
        Assert.IsLessThan(TimeSpan.FromSeconds(10), timer.Elapsed);
        Assert.IsEmpty(result.Events);
    }

    [TestMethod]
    public void AttentionAndLifecycleMatchReviewedAlgorithmsOverGeneratedHistories()
    {
        // Deterministic combinations include same-minute roots, raised carry-over,
        // missing ranks, cancels, extensions, orphan links, and Dec/Jan rollover.
        for (var seed = 0; seed < 32; seed++)
        {
            var versions = new List<SwpcMessageVersion>();
            for (var i = 1; i <= 18; i++)
            {
                var time = Epoch.AddMinutes(i / 2 * (seed % 3 + 1));
                var family = (SpaceWeatherFamily)((seed + i) % 8);
                var message = Message(i, time, family, i % 5 == 0 ? null : "G" + (i % 5 + 1).ToString(CultureInfo.InvariantCulture));
                versions.Add(new(message, versions.Count));
                if ((seed + i) % 3 == 0)
                    versions.Add(new(message with { IssueMinuteUtc = time.AddHours(1), Header = message.Header with { Level = "G5", Modifier = "EXTENDED" } }, versions.Count));
                if ((seed + i) % 4 == 0)
                    versions.Add(new(message with { IssueMinuteUtc = time.AddMinutes(seed % 2 == 0 ? -2 : 120), Header = message.Header with { Modifier = "CANCEL" } }, versions.Count));
                if ((seed + i) % 7 == 0)
                    versions.Add(new(message with { Serial = i + 100, Header = message.Header with { Link = new("continuation", i) } }, versions.Count));
            }
            var groups = SwpcGroupBuilder.Build(versions);
            var raised = groups.Where((_, i) => (i + seed) % 3 == 0).ToImmutableDictionary(g => g.Nodes.Last(), _ => seed % 5);
            var touched = groups.Where((_, i) => (i + seed) % 4 != 0).SelectMany(g => g.Nodes).ToImmutableHashSet();
            foreach (var now in new[] { Epoch.AddHours(-1), Epoch.AddMinutes(5), Epoch.AddHours(2), Epoch.AddDays(5) })
            {
                foreach (var group in groups) Assert.AreEqual(OriginalNoticeLifecycle.Evaluate(group, groups, now), SwpcNoticeLifecycle.Evaluate(group, groups, now));
                var input = new SwpcAttentionInput(groups, touched, raised, now);
                var expected = OriginalAttentionEvaluator.Evaluate(input);
                var actual = SwpcAttentionEvaluator.Evaluate(input);
                Equivalent(expected, actual);
                // A second merge reads ranks written by the preceding evaluation.
                Equivalent(OriginalAttentionEvaluator.Evaluate(input with { RaisedRanks = expected.RaisedRanks }),
                    SwpcAttentionEvaluator.Evaluate(input with { RaisedRanks = actual.RaisedRanks }));
            }
        }
    }

    [TestMethod]
    public void OversizeMessageIsRejectedBeforeMaterialization()
    {
        var bytes = AlertJson(new string('x', SwpcLimits.AlertsBytes - 200));
        Assert.IsLessThan(SwpcLimits.AlertsBytes, bytes.Length);
        SwpcAlertsParser.Parse(AlertJson("bad"), "application/json"); // Warm the fixed product/time regexes.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = SwpcAlertsParser.Parse(bytes, "application/json");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual("record-oversize", result.Rejections.Single().Reason);
        TestContext.WriteLine($"F3 near-cap ASCII message: body={bytes.Length}, allocated={allocated} bytes");
        Assert.IsLessThan(131072L, allocated);
    }

    [TestMethod]
    public void RecordCountIsRejectedBeforeDomButAfterCompleteSyntaxValidation()
    {
        var body = "[" + string.Join(',', Enumerable.Repeat("{}", 2001)) + "]";
        SwpcJsonBody.Read("[{}]"u8, SwpcProduct.Alerts, "application/json");
        var bytes = Encoding.UTF8.GetBytes(body);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = SwpcJsonBody.Read(bytes, SwpcProduct.Alerts, "application/json");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual("too-many-records", result.RejectionReason);
        Assert.IsNull(result.Root);
        Assert.IsLessThan(4096L, allocated);
        TestContext.WriteLine($"F3 2001 minimal records: allocated={allocated} bytes");
        foreach (var suffix in new[] { " NaN]", " {}", ",]" })
            Assert.AreEqual("malformed-json", SwpcAlertsParser.Parse(Encoding.UTF8.GetBytes(body[..^1] + suffix), "application/json").RejectionReason);
        Assert.AreEqual("schema", SwpcAlertsParser.Parse(Encoding.UTF8.GetBytes("{\"x\":" + body + "}"), "application/json").RejectionReason);
    }

    [TestMethod]
    public void RequiredStringsUseUnescapedUtf8LengthsAndSchemaPrecedence()
    {
        var message = MessageText("WATCH: Category G3", "Jan 01: G3\n");
        var literal = Encoding.UTF8.GetString(AlertJson(message));
        var escaped = literal.Replace("W", "\\u0057", StringComparison.Ordinal);
        Assert.IsTrue(SwpcAlertsParser.Parse(Encoding.UTF8.GetBytes(escaped), "application/json").Accepted);
        // Escaped source length exceeds 16 KiB; unescaped length is exactly the cap.
        var padded = message + new string('x', SwpcLimits.MessageBytes - Encoding.UTF8.GetByteCount(message));
        literal = Encoding.UTF8.GetString(AlertJson(padded));
        escaped = literal.Replace("x", "\\u0078", StringComparison.Ordinal);
        Assert.IsTrue(SwpcAlertsParser.Parse(Encoding.UTF8.GetBytes(escaped), "application/json").Accepted);
        Assert.AreEqual("record-oversize", SwpcAlertsParser.Parse(AlertJson(padded + "é"), "application/json").Rejections.Single().Reason);
        // A supplementary scalar occupies four UTF-8 bytes after unescaping.
        literal = Encoding.UTF8.GetString(AlertJson(padded[..^3] + "😀"));
        Assert.AreEqual("record-oversize", SwpcAlertsParser.Parse(Encoding.UTF8.GetBytes(literal), "application/json").Rejections.Single().Reason);
        foreach (var raw in new[] { "\\ud800", "\\udc00", "\\ud800x", "\\ud800\\u0041" })
        {
            var json = "[{\"product_id\":\"AA\",\"issue_datetime\":\"bad\",\"message\":\"" + new string('x', 17000) + raw + "\"}]";
            Assert.AreEqual("record-schema", SwpcAlertsParser.Parse(Encoding.UTF8.GetBytes(json), "application/json").Rejections.Single().Reason);
        }
        Assert.AreEqual("issue-datetime", SwpcAlertsParser.Parse(AlertJson(new string('x', 17000), "bad"), "application/json").Rejections.Single().Reason);
    }

    [TestMethod]
    public void UnreadSurrogatePropertyNamesAndDuplicateKeysMatchPythonDictionaryLookup()
    {
        // F:373–387 uses record.get on json.loads' last-key-wins dictionary.
        var body = Encoding.UTF8.GetString(AlertJson(MessageText("WATCH: Category G3", "Jan 01: G3\n")));
        foreach (var property in new[] { "\"messag\\ud800\":\"x\",", "\"message\":123,\"messag\\ud800\":\"x\"," })
        {
            var input = body.Insert(2, property);
            Assert.IsTrue(SwpcAlertsParser.Parse(Encoding.UTF8.GetBytes(input), "application/json").Accepted);
        }
        var unreadLast = body.Insert(body.Length - 2, ",\"messag\\ud800\":\"x\"");
        Assert.IsTrue(SwpcAlertsParser.Parse(Encoding.UTF8.GetBytes(unreadLast), "application/json").Accepted);
        using (var document = JsonDocument.Parse(unreadLast))
        {
            try
            {
                document.RootElement[0].TryGetProperty("message", out _);
                TestContext.WriteLine("Probe: reviewed JsonElement lookup returned without exception.");
            }
            catch (InvalidOperationException)
            {
                TestContext.WriteLine("Probe: reviewed JsonElement lookup throws InvalidOperationException for the unread property name.");
            }
        }
        var badLast = body.Insert(body.Length - 2, ",\"message\":123");
        Assert.AreEqual("record-schema", SwpcAlertsParser.Parse(Encoding.UTF8.GetBytes(badLast), "application/json").Rejections.Single().Reason);
    }

    [TestMethod]
    public void CompetingLinksUseCancelThenContinuationThenExtensionPrecedence()
    {
        // F:283–289 overwrites the link in this order, regardless of source order.
        var fields = "Cancel Serial Number: 33\nExtension to Serial Number: 11\nContinuation of Serial Number: 22\n";
        SwpcMessageLink? Link(string text) => SwpcAlertsParser.Parse(AlertJson(MessageText("WATCH: Category G3", text)), "application/json").Records.Single().Header.Link;
        Assert.AreEqual(new SwpcMessageLink("cancel", 33), Link(fields));
        Assert.AreEqual(new SwpcMessageLink("continuation", 22), Link(fields.Replace("Cancel Serial Number: 33", "Cancel Serial Number: bad", StringComparison.Ordinal)));
        Assert.AreEqual(new SwpcMessageLink("extension", 11), Link("Extension to Serial Number: 11\n"));
    }

    [TestMethod]
    public void UnknownWatchMonthIsSkippedWithoutDiscardingKnownDays()
    {
        var record = SwpcAlertsParser.Parse(AlertJson(MessageText("WATCH: Category G3", "Xxx 32: G5\nJan 01: G3\n")), "application/json").Records.Single();
        CollectionAssert.AreEqual(new[] { new SwpcWatchDay(new DateOnly(2027, 1, 1), "G3") }, record.Header.Days!.Value.ToArray());
    }

    [TestMethod]
    public void SynopticEndUsesOffsetArithmeticForOutOfRangeComponents()
    {
        // F:496–504: 25 hours + 99 minutes is Jan 1 02:39, not a missing time.
        var record = Message(1, Epoch, SpaceWeatherFamily.KAlert, "G3");
        record = record with { Header = record.Header with { SynopticPeriod = "9999-2599", Times = record.Header.Times.SetItem("Threshold Reached", Epoch) } };
        var groups = SwpcGroupBuilder.Build([new(record, 1)]);
        var window = SwpcNoticeLifecycle.Evaluate(groups[0], groups, Epoch);
        Assert.AreEqual(Epoch.Date.AddDays(1).AddHours(2).AddMinutes(39), window.UntilUtc.UtcDateTime);
        Assert.IsFalse(window.Fallback);
    }

    [TestMethod]
    public void MissingLevelRecordsZeroRankAndEarlierRaiseEscalatesOutsideEligibility()
    {
        var groups = SwpcGroupBuilder.Build([new(Message(1, Epoch, SpaceWeatherFamily.Warning, null), 1)]);
        var input = new SwpcAttentionInput(groups, groups[0].Nodes, ImmutableDictionary<SwpcMessageNode, int>.Empty, Epoch);
        var first = SwpcAttentionEvaluator.Evaluate(input);
        Assert.AreEqual(0, first.RaisedRanks[groups[0].CanonicalNode]);
        Assert.AreEqual(new SwpcAttentionEvent(groups[0].Identity, "new", null), first.Events.Single());
        var elevated = groups[0].Versions[0].Message with { Header = groups[0].Versions[0].Message.Header with { Level = "G5" } };
        var laterGroups = SwpcGroupBuilder.Build([new(elevated, 1)]);
        var laterInput = input with { Groups = laterGroups, NowUtc = Epoch.AddDays(10) };
        Assert.IsEmpty(SwpcAttentionEvaluator.Evaluate(laterInput).Events); // No prior raise, outside eligibility.
        var escalation = SwpcAttentionEvaluator.Evaluate(laterInput with { RaisedRanks = first.RaisedRanks });
        Assert.AreEqual(new SwpcAttentionEvent(groups[0].Identity, "escalated", "G5"), escalation.Events.Single());
        Assert.AreEqual(5, escalation.RaisedRanks[groups[0].CanonicalNode]);
    }

    [TestMethod]
    public void ThreeDayProductMarkerRequiresImmediateLfAfterNormalization()
    {
        var good = Encoding.UTF8.GetString(Forecast("X  1% 2% 3%"));
        Assert.IsTrue(SwpcThreeDayForecastParser.Parse(Encoding.UTF8.GetBytes(good), "text/plain").Accepted);
        foreach (var terminator in new[] { " prefix\n", "\r", "\v", "\u001c", "" })
        {
            var changed = good.Replace(":Product: 3-Day Forecast\n", ":Product: 3-Day Forecast" + terminator, StringComparison.Ordinal);
            Assert.AreEqual("schema", SwpcThreeDayForecastParser.Parse(Encoding.UTF8.GetBytes(changed), "text/plain").RejectionReason);
        }
    }

    [TestMethod]
    public void FieldsAndSourceTimesTrimPythonInformationSeparators()
    {
        foreach (var control in new[] { '\u001c', '\u001d', '\u001e', '\u001f' })
        {
            var fields = $"Cancel Serial Number: {control}42{control}\nValid From: {control}2026 Dec 31 2300 UTC{control}\nValid To: {control}2027 Jan 01 0100 UTC{control}\n";
            var record = SwpcAlertsParser.Parse(AlertJson(MessageText("WATCH: Category G3", fields)), "application/json").Records.Single();
            Assert.AreEqual(new SwpcMessageLink("cancel", 42), record.Header.Link);
            Assert.AreEqual(Epoch, record.Header.Times["Valid From"]);
            Assert.AreEqual(Epoch.AddHours(2), record.Header.Times["Valid To"]);
            var forecast = Encoding.UTF8.GetString(Forecast("X  1% 2% 3%")).Replace(":Issued: 2026 Oct 19 1230 UTC", $":Issued: {control}2026 Oct 19 1230 UTC{control}", StringComparison.Ordinal);
            Assert.IsTrue(SwpcThreeDayForecastParser.Parse(Encoding.UTF8.GetBytes(forecast), "text/plain").Accepted);
        }
    }

    private static DateTimeOffset Epoch => new(2026, 12, 31, 23, 0, 0, TimeSpan.Zero);
    private static byte[] AlertJson(string message, string issue = "2026-12-31 23:00:00.000") => JsonSerializer.SerializeToUtf8Bytes(new[] { new { product_id = "AA", issue_datetime = issue, message } });
    private static string MessageText(string headline, string fields) => "Space Weather Message Code: WATA50\nSerial Number: 1\nIssue Time: 2026 Dec 31 2300 UTC\n" + headline + "\n" + fields;
    private static byte[] Forecast(string radiationRow)
    {
        var text = File.ReadAllText(Path.Combine(SwpcScenarioFactsTests.Pack, "capture-20261019T1857Z/3-day-forecast.txt"));
        var title = text.IndexOf("Solar Radiation Storm Forecast for ", StringComparison.Ordinal);
        var lines = text[title..].Split('\n');
        var index = 1;
        while (string.IsNullOrWhiteSpace(lines[index])) index++;
        index++;
        return Encoding.UTF8.GetBytes(text[..title] + string.Join('\n', lines.Take(index)) + "\n" + radiationRow + "\n" + string.Join('\n', lines.Skip(index + 1)));
    }
    private static SwpcMessageRecord Message(int serial, DateTimeOffset issue, SpaceWeatherFamily family, string? level)
    {
        var code = family == SpaceWeatherFamily.Watch ? "WATA50" : "TEST" + ((int)family).ToString(CultureInfo.InvariantCulture);
        var times = ImmutableDictionary<string, DateTimeOffset?>.Empty.Add("Valid From", issue.AddMinutes(5)).Add("Valid To", issue.AddHours(6))
            .Add("Threshold Reached", issue).Add("Begin Time", issue);
        var header = new SwpcMessageHeader("WATCH: Category " + level, "WATCH", null, false, null, family, level, null,
            [new(new DateOnly(2027, 1, 2), "G3")], times, "2100-2400", null);
        return new(code + "/" + serial.ToString(CultureInfo.InvariantCulture), code, serial, issue, issue.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), "HASH", "{}", header, "AA", "");
    }
    private static void Equivalent(SwpcAttentionResult expected, SwpcAttentionResult actual)
    {
        CollectionAssert.AreEqual(expected.Events.ToArray(), actual.Events.ToArray());
        CollectionAssert.AreEquivalent(expected.RaisedRanks.ToArray(), actual.RaisedRanks.ToArray());
    }
}
