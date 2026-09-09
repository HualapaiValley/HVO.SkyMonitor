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
/// <para>
/// The open state is lost in two different ways and this helper covers both, so check which one a
/// new call site has rather than trusting either description alone. Most sections declare no
/// <c>open</c> attribute; there it is pure DOM state that Blazor's diff never tracks, and any
/// re-render recreating the element silently drops it. Two sections bind it instead.
/// <c>GalleryPage.razor</c> renders <c>open="@HasAdvancedFilters"</c>, which two of this helper's
/// eight call sites drive, and <c>ProcessingExecutionDetailPage.razor</c> renders
/// <c>open="@(node.Status is not "Completed")"</c>, which none currently drive. There Blazor does
/// track the attribute, so a re-render leaving the bound value unchanged emits no edit and an
/// out-of-band assignment survives; it is a transition of that value to false that removes the
/// attribute and closes an already-rendered section. The transition rather than the re-render is
/// the trigger, which is why no test trips this today.
/// </para>
/// <para>
/// Assignment plus the reveal wait covers both shapes. Assignment is idempotent and therefore safe
/// to repeat, and the loop re-applies it after either a recreation or a removal. A call site whose
/// bound value is genuinely false is asking for a state the page is actively refusing, and
/// exhausting the timeout there is the correct outcome rather than a defect in this helper.
/// </para>
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
