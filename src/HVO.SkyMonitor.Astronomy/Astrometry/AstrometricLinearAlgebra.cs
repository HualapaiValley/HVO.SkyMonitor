namespace HVO.SkyMonitor.Astronomy;

/// <summary>
/// Small dense symmetric numerics shared by astrometric diagnostics, session calibration and uncertainty. Matrices
/// are row-major <c>n x n</c> arrays; callers own scaling so pixels, radians and log scale stay comparable.
/// </summary>
internal static class AstrometricLinearAlgebra
{
    /// <summary>Cyclic Jacobi eigenvalues, stopping when the squared off-diagonal mass falls below <paramref name="tolerance"/>.</summary>
    internal static double[] SymmetricEigenvalues(double[] matrix, int n, double tolerance)
    {
        var a = (double[])matrix.Clone();
        for (var sweep = 0; sweep < 100; sweep++)
        {
            var off = 0d;
            for (var p = 0; p < n; p++) for (var q = p + 1; q < n; q++) off += a[p * n + q] * a[p * n + q];
            if (off < tolerance) break;
            for (var p = 0; p < n; p++) for (var q = p + 1; q < n; q++)
            {
                var apq = a[p * n + q];
                if (Math.Abs(apq) < 1e-300) continue;
                var theta = (a[q * n + q] - a[p * n + p]) / (2 * apq);
                var t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                var c = 1 / Math.Sqrt(t * t + 1); var s = t * c;
                for (var k = 0; k < n; k++)
                {
                    var akp = a[k * n + p]; var akq = a[k * n + q];
                    a[k * n + p] = c * akp - s * akq; a[k * n + q] = s * akp + c * akq;
                }
                for (var k = 0; k < n; k++)
                {
                    var apk = a[p * n + k]; var aqk = a[q * n + k];
                    a[p * n + k] = c * apk - s * aqk; a[q * n + k] = s * apk + c * aqk;
                }
            }
        }
        return [.. Enumerable.Range(0, n).Select(i => a[i * n + i])];
    }

    /// <summary>Inverse of a symmetric positive-definite matrix by Cholesky factorization; null unless strictly positive definite.</summary>
    internal static double[]? SymmetricPositiveDefiniteInverse(double[] matrix, int n)
    {
        var l = new double[n * n];
        for (var i = 0; i < n; i++)
            for (var j = 0; j <= i; j++)
            {
                var sum = matrix[i * n + j];
                for (var k = 0; k < j; k++) sum -= l[i * n + k] * l[j * n + k];
                if (i == j)
                {
                    if (!(sum > 0) || !double.IsFinite(sum)) return null;
                    l[i * n + i] = Math.Sqrt(sum);
                }
                else l[i * n + j] = sum / l[j * n + j];
            }
        // Solve L L^T X = I one column at a time.
        var inverse = new double[n * n]; var y = new double[n];
        for (var column = 0; column < n; column++)
        {
            for (var i = 0; i < n; i++)
            {
                var sum = i == column ? 1d : 0d;
                for (var k = 0; k < i; k++) sum -= l[i * n + k] * y[k];
                y[i] = sum / l[i * n + i];
            }
            for (var i = n - 1; i >= 0; i--)
            {
                var sum = y[i];
                for (var k = i + 1; k < n; k++) sum -= l[k * n + i] * inverse[k * n + column];
                inverse[i * n + column] = sum / l[i * n + i];
            }
        }
        for (var i = 0; i < n; i++) for (var j = i + 1; j < n; j++) inverse[i * n + j] = inverse[j * n + i] = (inverse[i * n + j] + inverse[j * n + i]) / 2;
        return inverse.All(double.IsFinite) ? inverse : null;
    }
}
