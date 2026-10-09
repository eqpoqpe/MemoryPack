namespace MemoryPack;

/// <summary>Provides an independent, closed set of serialization formatters.</summary>
public abstract class MemoryPackSerializerContext(MemoryPackSerializerOptions? options = null)
{
    public MemoryPackSerializerOptions Options { get; } = options ?? MemoryPackSerializerOptions.Default;


    /// <summary>Returns stable metadata for a known type, or null for an unknown type.</summary>
    public abstract MemoryPackTypeInfo? GetTypeInfo(Type type);

    public MemoryPackTypeInfo<T> GetTypeInfo<T>()
    {
        var metadata = GetRequiredTypeInfo(typeof(T));
        if (metadata is MemoryPackTypeInfo<T> typed) return typed;
        throw new MemoryPackSerializationException($"Context {GetType().FullName} returned incompatible metadata for {typeof(T).FullName}.");
    }

    internal MemoryPackTypeInfo GetRequiredTypeInfo(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var metadata = GetTypeInfo(type);
        if (metadata == null)
            throw new MemoryPackSerializationException($"Type {type.FullName} is not included in context {GetType().FullName}. Add a [MemoryPackSerializable] declaration for this type and its formatter dependencies.");
        if (metadata.Type != type || !ReferenceEquals(metadata.Context, this))
            throw new MemoryPackSerializationException($"Context {GetType().FullName} returned incompatible or incorrectly owned metadata for {type.FullName}.");
        return metadata;
    }
}
