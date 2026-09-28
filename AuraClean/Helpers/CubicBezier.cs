namespace AuraClean.Helpers;

/// <summary>
/// Evaluates a CSS <c>cubic-bezier(x1, y1, x2, y2)</c> timing curve (no UI dependency, unit-tested).
/// </summary>
public static class CubicBezier
{
    private const int NewtonIterations = 8;
    private const double Epsilon = 1e-6;

    /// <summary>Returns the eased progress for linear progress <paramref name="x"/> in [0, 1].</summary>
    public static double Evaluate(double x, double x1, double y1, double x2, double y2)
    {
        if (double.IsNaN(x) || x <= 0)
            return 0;
        if (x >= 1)
            return 1;

        // CSS requires the x control points to stay within [0, 1] so the curve is a function of time.
        x1 = Math.Clamp(x1, 0, 1);
        x2 = Math.Clamp(x2, 0, 1);
        return Sample(SolveForT(x, x1, x2), y1, y2);
    }

    // B(t) for a 1-D cubic Bezier anchored at 0 and 1.
    private static double Sample(double t, double p1, double p2)
    {
        var u = 1 - t;
        return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
    }

    private static double SampleDerivative(double t, double p1, double p2)
    {
        var u = 1 - t;
        return 3 * u * u * p1 + 6 * u * t * (p2 - p1) + 3 * t * t * (1 - p2);
    }

    private static double SolveForT(double x, double x1, double x2)
    {
        var t = x;
        for (int i = 0; i < NewtonIterations; i++)
        {
            var error = Sample(t, x1, x2) - x;
            if (Math.Abs(error) < Epsilon)
                return t;
            var slope = SampleDerivative(t, x1, x2);
            if (Math.Abs(slope) < Epsilon)
                break;
            t -= error / slope;
        }

        // Bisection fallback for flat regions where Newton's method stalls.
        double low = 0, high = 1;
        t = x;
        while (high - low > Epsilon)
        {
            var value = Sample(t, x1, x2);
            if (Math.Abs(value - x) < Epsilon)
                break;
            if (value < x)
                low = t;
            else
                high = t;
            t = (low + high) / 2;
        }

        return t;
    }
}
