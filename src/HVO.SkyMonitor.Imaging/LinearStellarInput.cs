namespace HVO.SkyMonitor.Imaging;

internal static class LinearStellarInput
{
    internal static void Validate(ReadOnlySpan<double> pixels, ReadOnlySpan<bool> validMask,
        int width, int height, int minimumDimension, int maximumPixels, CancellationToken cancellationToken)
    {
        if (width < minimumDimension || height < minimumDimension ||
            (long)width * height > maximumPixels || (long)width * height != pixels.Length || validMask.Length != pixels.Length)
        {
            throw new ArgumentException("Linear image dimensions, buffers, or pixel budget are invalid.", nameof(pixels));
        }
        for (var index = 0; index < pixels.Length; index++)
        {
            if ((index & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (!double.IsFinite(pixels[index]))
            {
                throw new ArgumentException("Every input sample must be finite, including masked samples.", nameof(pixels));
            }
        }
    }
}
