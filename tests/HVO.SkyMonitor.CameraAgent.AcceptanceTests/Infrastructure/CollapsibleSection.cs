using System.Diagnostics;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;

/// <summary>
/// Puts a <c>&lt;details&gt;</c> section into the open state so the content inside it can be asserted.
/// </summary>
/// <remarks>
/// Clicking the section's <c>summary</c> is the obvious way to do this and it is wrong. A click
/// toggles whatever state the element is already in, while every caller here means "make sure this
/// is open" — an idempotent intent expressed as a non-idempotent operation. When the section happens
/// to be open already the click closes it, and because a closed <c>details</c> keeps its children in
/// the DOM and merely hides them, the next locator resolves to a present-but-invisible element and
/// waits out its full timeout. That is exactly how issue #794 failed the issue #211 W6 campaign.
/// The <c>open</c> state is DOM state that Blazor's diff does not track, since the markup declares no
/// <c>open</c> attribute, so any re-render that recreates the element silently drops it.
/// Assignment is idempotent and therefore safe to repeat; the retry loop covers a re-render landing
/// after the assignment but before the content is observed.
/// </remarks>
internal static class CollapsibleSection
{
    private const int DefaultTimeoutMilliseconds = 30_000;
    private const int RevealAttemptMilliseconds = 2_000;

    /// <summary>
    /// Opens <paramref name="section"/> and returns once <paramref name="revealed"/> is visible,
    /// re-applying the open state if a re-render closes it in between.
    /// </summary>
    /// <param name="section">The <c>&lt;details&gt;</c> element. Must resolve to exactly one element.</param>
    /// <param name="revealed">Content inside the section whose visibility proves the section is open.</param>
    /// <param name="timeoutMilliseconds">Total budget across every attempt.</param>
    public static async Task EnsureOpenAsync(
        ILocator section,
        ILocator revealed,
        int timeoutMilliseconds = DefaultTimeoutMilliseconds)
    {
        await section.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = timeoutMilliseconds
        }).ConfigureAwait(false);

        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            await section.EvaluateAsync("section => { section.open = true; }").ConfigureAwait(false);

            var remaining = timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds;
            var finalAttempt = remaining <= RevealAttemptMilliseconds;
            try
            {
                // The reveal wait is the proof, not the assignment: a closed <details> hides its
                // children, so visible content cannot coexist with a section that closed again.
                await revealed.First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = Math.Max(1, Math.Min(RevealAttemptMilliseconds, remaining))
                }).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException) when (!finalAttempt)
            {
                // Re-apply and wait again. The final attempt deliberately rethrows so the caller
                // keeps Playwright's locator call log, which is the only diagnostic these tests emit.
            }
        }
    }
}
