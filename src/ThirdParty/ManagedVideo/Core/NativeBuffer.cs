using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ManagedCodecs;

/// <summary>
/// A zeroed, fixed-size buffer outside the GC heap, indexed like an array. <see cref="Dispose"/> gives the memory straight
/// back to the allocator, so the large per-picture buffers of a decoder leave no garbage for a later full GC to find.
/// After disposal the buffer is empty: indexing it throws instead of reading freed memory. Spans and refs taken from
/// it before then must not outlive it; the owner disposes it only once nothing decodes into it.
/// </summary>
/// <summary>A native buffer seen as raw bytes, whatever its element type (a frame's planes of 8- or 16-bit samples).</summary>
internal interface INativeBytes
{
    /// <summary>The length in bytes.</summary>
    int ByteLength { get; }

    /// <summary>The elements as bytes, in memory order.</summary>
    Span<byte> Bytes { get; }
}

internal sealed unsafe class NativeBuffer<T> : INativeBytes, IDisposable where T : unmanaged
{
    /// <summary>A shared empty buffer; disposing it does nothing.</summary>
    public static readonly NativeBuffer<T> Empty = new(0);

    private T* _pointer;
    private int _length;

    public NativeBuffer(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length == 0)
        {
            GC.SuppressFinalize(this);
            return;
        }

        _pointer = (T*)NativeMemory.AllocZeroed((nuint)length, (nuint)sizeof(T));
        _length = length;
    }

    // A safety net for an owner that was never disposed: the memory still goes back, only later.
    ~NativeBuffer() => Free();

    public int Length => _length;

    public Span<T> Span => new(_pointer, _length);

    public int ByteLength => _length * sizeof(T);

    public Span<byte> Bytes => MemoryMarshal.AsBytes(Span);

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)index >= (uint)_length)
            {
                ThrowIndex();
            }

            return ref _pointer[index];
        }
    }

    /// <summary>The first element, as <c>MemoryMarshal.GetArrayDataReference</c> gives an array's (not to be read when empty).</summary>
    public ref T DataReference => ref *_pointer;

    public Span<T> AsSpan() => Span;

    public Span<T> AsSpan(int start) => Span[start..];

    public Span<T> AsSpan(int start, int length) => Span.Slice(start, length);

    public void Clear() => Span.Clear();

    public static implicit operator Span<T>(NativeBuffer<T> buffer) => buffer.Span;

    public static implicit operator ReadOnlySpan<T>(NativeBuffer<T> buffer) => buffer.Span;

    public void Fill(T value) => Span.Fill(value);

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    private void Free()
    {
        var pointer = _pointer;
        _pointer = null;
        _length = 0;
        if (pointer is not null)
        {
            NativeMemory.Free(pointer);
        }
    }

    [DoesNotReturn]
    private static void ThrowIndex() => throw new IndexOutOfRangeException();
}

/// <summary>Helpers for groups of <see cref="NativeBuffer{T}"/>.</summary>
internal static class NativeBuffers
{
    public static NativeBuffer<T>[] Pair<T>(int length) where T : unmanaged => [new(length), new(length)];

    public static void Dispose<T>(NativeBuffer<T>[] buffers) where T : unmanaged
    {
        foreach (var buffer in buffers)
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Asks the C allocator to hand freed pages back to the system. glibc keeps freed blocks below its (growing) mmap
    /// threshold in its arenas; other allocators return large blocks on free and need nothing here.
    /// </summary>
    public static void TrimAllocator()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            _ = MallocTrim(0);
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            // musl and other C libraries without malloc_trim unmap large blocks on free.
        }
    }

    [DllImport("libc", EntryPoint = "malloc_trim")]
    private static extern int MallocTrim(nuint pad);
}
