using MemoryPack.Internal;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace MemoryPack;

public static partial class MemoryPackSerializer
{
    static MemoryPackSerializerContext RequiredContext(MemoryPackSerializerContext context) => context ?? throw new ArgumentNullException(nameof(context));

    static MemoryPackSerializerOptions MetadataOptions(MemoryPackTypeInfo typeInfo, MemoryPackSerializerOptions? options)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return options ?? typeInfo.Context?.Options ?? MemoryPackSerializerOptions.Default;
    }

    public static byte[] Serialize<T>(in T? value, MemoryPackTypeInfo<T> typeInfo, MemoryPackSerializerOptions? options = null)
    {
        var buffer = ReusableLinkedArrayBufferWriterPool.Rent();
        try
        {
            Serialize(buffer, value, typeInfo, options);
            return buffer.ToArrayAndReset();
        }
        finally { ReusableLinkedArrayBufferWriterPool.Return(buffer); }
    }

    public static byte[] Serialize<T>(in T? value, MemoryPackSerializerContext context) => Serialize(value, RequiredContext(context).GetTypeInfo<T>());

    public static void Serialize<T, TBufferWriter>(in TBufferWriter bufferWriter, in T? value, MemoryPackTypeInfo<T> typeInfo, MemoryPackSerializerOptions? options = null)
        where TBufferWriter : IBufferWriter<byte>
    {
        using var state = MemoryPackWriterOptionalStatePool.Rent(MetadataOptions(typeInfo, options));
        state.Context = typeInfo.Context;
        var writer = new MemoryPackWriter<TBufferWriter>(ref Unsafe.AsRef(in bufferWriter), state);
        typeInfo.Formatter.Serialize(ref writer, ref Unsafe.AsRef(in value));
        writer.Flush();
    }

    public static void Serialize<T, TBufferWriter>(in TBufferWriter bufferWriter, in T? value, MemoryPackSerializerContext context)
        where TBufferWriter : IBufferWriter<byte> => Serialize(bufferWriter, value, RequiredContext(context).GetTypeInfo<T>());

    public static T? Deserialize<T>(ReadOnlySpan<byte> buffer, MemoryPackTypeInfo<T> typeInfo, MemoryPackSerializerOptions? options = null)
    {
        T? value = default;
        Deserialize(buffer, ref value, typeInfo, options);
        return value;
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> buffer, MemoryPackSerializerContext context) => Deserialize(buffer, RequiredContext(context).GetTypeInfo<T>());

    public static int Deserialize<T>(ReadOnlySpan<byte> buffer, ref T? value, MemoryPackTypeInfo<T> typeInfo, MemoryPackSerializerOptions? options = null)
    {
        using var state = MemoryPackReaderOptionalStatePool.Rent(MetadataOptions(typeInfo, options));
        state.Context = typeInfo.Context;
        var reader = new MemoryPackReader(buffer, state);
        try { typeInfo.Formatter.Deserialize(ref reader, ref value); return reader.Consumed; }
        finally { reader.Dispose(); }
    }

    public static int Deserialize<T>(ReadOnlySpan<byte> buffer, ref T? value, MemoryPackSerializerContext context) => Deserialize(buffer, ref value, RequiredContext(context).GetTypeInfo<T>());

    public static T? Deserialize<T>(in ReadOnlySequence<byte> buffer, MemoryPackTypeInfo<T> typeInfo, MemoryPackSerializerOptions? options = null)
    {
        T? value = default;
        Deserialize(buffer, ref value, typeInfo, options);
        return value;
    }

    public static T? Deserialize<T>(in ReadOnlySequence<byte> buffer, MemoryPackSerializerContext context) => Deserialize(buffer, RequiredContext(context).GetTypeInfo<T>());

    public static int Deserialize<T>(in ReadOnlySequence<byte> buffer, ref T? value, MemoryPackTypeInfo<T> typeInfo, MemoryPackSerializerOptions? options = null)
    {
        using var state = MemoryPackReaderOptionalStatePool.Rent(MetadataOptions(typeInfo, options));
        state.Context = typeInfo.Context;
        var reader = new MemoryPackReader(buffer, state);
        try { typeInfo.Formatter.Deserialize(ref reader, ref value); return reader.Consumed; }
        finally { reader.Dispose(); }
    }

    public static int Deserialize<T>(in ReadOnlySequence<byte> buffer, ref T? value, MemoryPackSerializerContext context) => Deserialize(buffer, ref value, RequiredContext(context).GetTypeInfo<T>());

    public static async ValueTask SerializeAsync<T>(Stream stream, T? value, MemoryPackTypeInfo<T> typeInfo, MemoryPackSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        var buffer = ReusableLinkedArrayBufferWriterPool.Rent();
        try
        {
            Serialize(buffer, value, typeInfo, options);
            await buffer.WriteToAndResetAsync(stream, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { ReusableLinkedArrayBufferWriterPool.Return(buffer); }
    }

    public static ValueTask SerializeAsync<T>(Stream stream, T? value, MemoryPackSerializerContext context, CancellationToken cancellationToken = default) => SerializeAsync(stream, value, RequiredContext(context).GetTypeInfo<T>(), cancellationToken: cancellationToken);

    public static async ValueTask<T?> DeserializeAsync<T>(Stream stream, MemoryPackTypeInfo<T> typeInfo, MemoryPackSerializerOptions? options = null, CancellationToken cancellationToken = default) =>
        (T?)await DeserializeAsync(typeInfo, stream, options, cancellationToken).ConfigureAwait(false);

    public static ValueTask<T?> DeserializeAsync<T>(Stream stream, MemoryPackSerializerContext context, CancellationToken cancellationToken = default) => DeserializeAsync(stream, RequiredContext(context).GetTypeInfo<T>(), cancellationToken: cancellationToken);

    public static byte[] Serialize(MemoryPackTypeInfo typeInfo, object? value, MemoryPackSerializerOptions? options = null)
    {
        var buffer = ReusableLinkedArrayBufferWriterPool.Rent();
        try { Serialize(typeInfo, buffer, value, options); return buffer.ToArrayAndReset(); }
        finally { ReusableLinkedArrayBufferWriterPool.Return(buffer); }
    }

    public static byte[] Serialize(Type type, object? value, MemoryPackSerializerContext context) => Serialize(RequiredContext(context).GetRequiredTypeInfo(type), value);

    public static void Serialize<TBufferWriter>(MemoryPackTypeInfo typeInfo, in TBufferWriter bufferWriter, object? value, MemoryPackSerializerOptions? options = null)
        where TBufferWriter : IBufferWriter<byte>
    {
        using var state = MemoryPackWriterOptionalStatePool.Rent(MetadataOptions(typeInfo, options));
        state.Context = typeInfo.Context;
        ValidateMetadataValue(typeInfo, value);
        var writer = new MemoryPackWriter<TBufferWriter>(ref Unsafe.AsRef(in bufferWriter), state);
        typeInfo.Formatter.Serialize(ref writer, ref value);
        writer.Flush();
    }

    public static void Serialize<TBufferWriter>(Type type, in TBufferWriter bufferWriter, object? value, MemoryPackSerializerContext context)
        where TBufferWriter : IBufferWriter<byte> => Serialize(RequiredContext(context).GetRequiredTypeInfo(type), bufferWriter, value);

    public static object? Deserialize(MemoryPackTypeInfo typeInfo, ReadOnlySpan<byte> buffer, MemoryPackSerializerOptions? options = null)
    {
        object? value = null;
        Deserialize(typeInfo, buffer, ref value, options);
        return value;
    }

    public static object? Deserialize(Type type, ReadOnlySpan<byte> buffer, MemoryPackSerializerContext context) => Deserialize(RequiredContext(context).GetRequiredTypeInfo(type), buffer);

    public static int Deserialize(MemoryPackTypeInfo typeInfo, ReadOnlySpan<byte> buffer, ref object? value, MemoryPackSerializerOptions? options = null)
    {
        using var state = MemoryPackReaderOptionalStatePool.Rent(MetadataOptions(typeInfo, options));
        state.Context = typeInfo.Context;
        ValidateMetadataValue(typeInfo, value);
        var reader = new MemoryPackReader(buffer, state);
        try { typeInfo.Formatter.Deserialize(ref reader, ref value); return reader.Consumed; }
        finally { reader.Dispose(); }
    }

    public static int Deserialize(Type type, ReadOnlySpan<byte> buffer, ref object? value, MemoryPackSerializerContext context) => Deserialize(RequiredContext(context).GetRequiredTypeInfo(type), buffer, ref value);

    public static object? Deserialize(MemoryPackTypeInfo typeInfo, in ReadOnlySequence<byte> buffer, MemoryPackSerializerOptions? options = null)
    {
        object? value = null;
        Deserialize(typeInfo, buffer, ref value, options);
        return value;
    }

    public static object? Deserialize(Type type, in ReadOnlySequence<byte> buffer, MemoryPackSerializerContext context) => Deserialize(RequiredContext(context).GetRequiredTypeInfo(type), buffer);

    public static int Deserialize(MemoryPackTypeInfo typeInfo, in ReadOnlySequence<byte> buffer, ref object? value, MemoryPackSerializerOptions? options = null)
    {
        using var state = MemoryPackReaderOptionalStatePool.Rent(MetadataOptions(typeInfo, options));
        state.Context = typeInfo.Context;
        ValidateMetadataValue(typeInfo, value);
        var reader = new MemoryPackReader(buffer, state);
        try { typeInfo.Formatter.Deserialize(ref reader, ref value); return reader.Consumed; }
        finally { reader.Dispose(); }
    }

    public static int Deserialize(Type type, in ReadOnlySequence<byte> buffer, ref object? value, MemoryPackSerializerContext context) => Deserialize(RequiredContext(context).GetRequiredTypeInfo(type), buffer, ref value);

    public static async ValueTask SerializeAsync(MemoryPackTypeInfo typeInfo, Stream stream, object? value, MemoryPackSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        var buffer = ReusableLinkedArrayBufferWriterPool.Rent();
        try
        {
            Serialize(typeInfo, buffer, value, options);
            await buffer.WriteToAndResetAsync(stream, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { ReusableLinkedArrayBufferWriterPool.Return(buffer); }
    }

    public static ValueTask SerializeAsync(Type type, Stream stream, object? value, MemoryPackSerializerContext context, CancellationToken cancellationToken = default) => SerializeAsync(RequiredContext(context).GetRequiredTypeInfo(type), stream, value, cancellationToken: cancellationToken);

    public static async ValueTask<object?> DeserializeAsync(MemoryPackTypeInfo typeInfo, Stream stream, MemoryPackSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        MetadataOptions(typeInfo, options);
        cancellationToken.ThrowIfCancellationRequested();
        if (stream is MemoryStream memoryStream && memoryStream.TryGetBuffer(out var segment))
        {
            object? value = null;
            var count = Deserialize(typeInfo, segment.AsSpan(checked((int)memoryStream.Position)), ref value, options);
            memoryStream.Seek(count, SeekOrigin.Current);
            return value;
        }
        var builder = ReusableReadOnlySequenceBuilderPool.Rent();
        try
        {
            var buffer = ArrayPool<byte>.Shared.Rent(65536);
            var offset = 0;
            while (true)
            {
                if (offset == buffer.Length)
                {
                    builder.Add(buffer, returnToPool: true);
                    buffer = ArrayPool<byte>.Shared.Rent(MathEx.NewArrayCapacity(buffer.Length));
                    offset = 0;
                }
                int read;
                try { read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false); }
                catch { ArrayPool<byte>.Shared.Return(buffer); throw; }
                offset += read;
                if (read != 0) continue;
                builder.Add(buffer.AsMemory(0, offset), returnToPool: true);
                return builder.TryGetSingleMemory(out var memory)
                    ? Deserialize(typeInfo, memory.Span, options)
                    : Deserialize(typeInfo, builder.Build(), options);
            }
        }
        finally { ReusableReadOnlySequenceBuilderPool.Return(builder); }
    }

    public static ValueTask<object?> DeserializeAsync(Type type, Stream stream, MemoryPackSerializerContext context, CancellationToken cancellationToken = default) => DeserializeAsync(RequiredContext(context).GetRequiredTypeInfo(type), stream, cancellationToken: cancellationToken);

    static void ValidateMetadataValue(MemoryPackTypeInfo typeInfo, object? value)
    {
        if (value != null && !typeInfo.Type.IsInstanceOfType(value))
            throw new MemoryPackSerializationException($"Value of type {value.GetType().FullName} is incompatible with metadata for {typeInfo.Type.FullName}.");
    }
}
