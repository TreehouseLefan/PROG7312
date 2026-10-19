using System.Runtime.CompilerServices;

namespace SmartX.Core.Telemetry;

/// <summary>
/// Converts a generic struct payload to a double WITHOUT boxing.
/// <para>
/// Casting a generic <c>T</c> to <c>object</c> and then to <c>float</c> would allocate a box on
/// the heap for every reading. Instead, each <c>typeof(T) == typeof(X)</c> check is a JIT-time
/// constant for value types, so the compiler emits only the branch for the real type, and
/// <see cref="Unsafe.As{TFrom,TTo}(ref TFrom)"/> reinterprets the value in place.
/// </para>
/// </summary>
public static class TelemetryValueConverter
{
    public static bool IsSupported<T>() where T : struct =>
        typeof(T) == typeof(float) ||
        typeof(T) == typeof(double) ||
        typeof(T) == typeof(int) ||
        typeof(T) == typeof(long) ||
        typeof(T) == typeof(bool);

    public static TelemetryValueKind KindOf<T>() where T : struct
    {
        if (typeof(T) == typeof(float) || typeof(T) == typeof(double))
        {
            return TelemetryValueKind.Float;
        }

        if (typeof(T) == typeof(int) || typeof(T) == typeof(long))
        {
            return TelemetryValueKind.Int;
        }

        if (typeof(T) == typeof(bool))
        {
            return TelemetryValueKind.Bool;
        }

        throw new NotSupportedException($"Telemetry payload type '{typeof(T).Name}' is not supported.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToDouble<T>(T value) where T : struct
    {
        if (typeof(T) == typeof(float))
        {
            return Unsafe.As<T, float>(ref value);
        }

        if (typeof(T) == typeof(double))
        {
            return Unsafe.As<T, double>(ref value);
        }

        if (typeof(T) == typeof(int))
        {
            return Unsafe.As<T, int>(ref value);
        }

        if (typeof(T) == typeof(long))
        {
            return Unsafe.As<T, long>(ref value);
        }

        if (typeof(T) == typeof(bool))
        {
            return Unsafe.As<T, bool>(ref value) ? 1d : 0d;
        }

        throw new NotSupportedException($"Telemetry payload type '{typeof(T).Name}' is not supported.");
    }
}
