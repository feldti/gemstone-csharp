#if !NET
// Polyfills for .NET Framework 4.8: just enough of the newer BCL for this library, so that the
// code base stays the same for both targets. Only compiled for net48.

using System.Runtime.CompilerServices;

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Class, Inherited = false)]
    internal sealed class RequiresUnreferencedCodeAttribute(string message) : Attribute
    {
        public string Message { get; } = message;
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Class, Inherited = false)]
    internal sealed class RequiresDynamicCodeAttribute(string message) : Attribute
    {
        public string Message { get; } = message;
    }
}

namespace System.Collections.Generic
{
    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object?>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object? obj) => RuntimeHelpers.GetHashCode(obj!);
    }
}

namespace System.Runtime.InteropServices
{
    internal static unsafe class NativeMemory
    {
        // AllocHGlobal is 16-byte aligned on 64-bit Windows, enough for the 8-byte GCI buffers.
        public static void* AlignedAlloc(nuint byteCount, nuint alignment)
        {
            var p = Marshal.AllocHGlobal(checked((nint)byteCount));
            if (((ulong)p.ToInt64() & ((ulong)alignment - 1)) != 0)
            {
                Marshal.FreeHGlobal(p);
                throw new InvalidOperationException($"AllocHGlobal returned memory not aligned to {alignment}.");
            }
            return (void*)p;
        }

        public static void AlignedFree(void* ptr) => Marshal.FreeHGlobal((nint)ptr);
    }
}

namespace System.Text
{
    internal static class Ascii
    {
        public static bool IsValid(string value)
        {
            foreach (var c in value)
                if (c > 0x7F) return false;
            return true;
        }
    }

    /// <summary>
    /// Minimal stand-in for .NET's <c>System.Text.Rune</c> (a Unicode scalar value) on .NET
    /// Framework, so that GemStone Characters map to the same type on both targets.
    /// </summary>
    public readonly struct Rune : IEquatable<Rune>, IComparable<Rune>
    {
        private readonly uint _value;

        public Rune(int value)
        {
            if (!IsValid(value)) throw new ArgumentOutOfRangeException(nameof(value), value, "Not a Unicode scalar value.");
            _value = (uint)value;
        }

        public Rune(char ch) : this((int)ch) { }

        public int Value => (int)_value;
        public bool IsBmp => _value <= 0xFFFF;

        public static bool IsValid(int value) => value is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF);

        public override string ToString() => char.ConvertFromUtf32(Value);
        public bool Equals(Rune other) => _value == other._value;
        public override bool Equals(object? obj) => obj is Rune r && Equals(r);
        public override int GetHashCode() => Value;
        public int CompareTo(Rune other) => _value.CompareTo(other._value);
        public static bool operator ==(Rune a, Rune b) => a.Equals(b);
        public static bool operator !=(Rune a, Rune b) => !a.Equals(b);
    }
}

namespace GemStone.Gci
{
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;

    internal static class NetFrameworkPolyfills
    {
        extension(Environment)
        {
            public static long TickCount64 => System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;
        }

        extension(TimeSpan)
        {
            public static long TicksPerMicrosecond => 10;
        }

        extension(DateTime)
        {
            public static DateTime UnixEpoch => new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        extension(System.Text.Encoding)
        {
            public static System.Text.Encoding Latin1 => System.Text.Encoding.GetEncoding(28591);
        }

        extension(Math)
        {
            public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
        }

        extension(decimal d)
        {
            public byte Scale => (byte)((decimal.GetBits(d)[3] >> 16) & 0xFF);
        }

        public static unsafe string GetString(this System.Text.Encoding encoding, ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty) return string.Empty;
            fixed (byte* p = bytes) return encoding.GetString(p, bytes.Length);
        }

        public static bool TryDequeue<T>(this Queue<T> queue, [MaybeNullWhen(false)] out T result)
        {
            if (queue.Count == 0)
            {
                result = default;
                return false;
            }
            result = queue.Dequeue();
            return true;
        }

        public static bool TryAdd<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key, TValue value) where TKey : notnull
        {
            if (dict.ContainsKey(key)) return false;
            dict.Add(key, value);
            return true;
        }

        public static IEnumerable<T[]> Chunk<T>(this IEnumerable<T> source, int size)
        {
            var chunk = new List<T>(size);
            foreach (var item in source)
            {
                chunk.Add(item);
                if (chunk.Count == size)
                {
                    yield return chunk.ToArray();
                    chunk.Clear();
                }
            }
            if (chunk.Count > 0) yield return chunk.ToArray();
        }

        extension(ArgumentNullException)
        {
            public static void ThrowIfNull([NotNull] object? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
            {
                if (argument is null) throw new ArgumentNullException(paramName);
            }
        }

        extension(ArgumentException)
        {
            public static void ThrowIfNullOrEmpty([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
            {
                if (argument is null) throw new ArgumentNullException(paramName);
                if (argument.Length == 0) throw new ArgumentException("The value cannot be an empty string.", paramName);
            }
        }

        extension(ArgumentOutOfRangeException)
        {
            public static void ThrowIfNegative<T>(T value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
                where T : IComparable<T>
            {
                if (value.CompareTo(default!) < 0)
                    throw new ArgumentOutOfRangeException(paramName, value, "The value must not be negative.");
            }

            public static void ThrowIfLessThan<T>(T value, T other, [CallerArgumentExpression(nameof(value))] string? paramName = null)
                where T : IComparable<T>
            {
                if (value.CompareTo(other) < 0)
                    throw new ArgumentOutOfRangeException(paramName, value, $"The value must be at least {other}.");
            }

            public static void ThrowIfGreaterThan<T>(T value, T other, [CallerArgumentExpression(nameof(value))] string? paramName = null)
                where T : IComparable<T>
            {
                if (value.CompareTo(other) > 0)
                    throw new ArgumentOutOfRangeException(paramName, value, $"The value must be at most {other}.");
            }
        }

        extension(ObjectDisposedException)
        {
            public static void ThrowIf(bool condition, object instance)
            {
                if (condition) throw new ObjectDisposedException(instance.GetType().FullName);
            }
        }
    }
}
#endif
