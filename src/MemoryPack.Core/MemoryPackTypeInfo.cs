namespace MemoryPack;

/// <summary>Immutable serialization metadata for a concrete type.</summary>
public abstract class MemoryPackTypeInfo
{
    public Type Type { get; }
    public IMemoryPackFormatter Formatter { get; }
    public MemoryPackSerializerContext? Context { get; }

    private protected MemoryPackTypeInfo(
        Type type,
        IMemoryPackFormatter formatter,
        MemoryPackSerializerContext? context
    )
    {
        Type = type;
        Formatter = formatter;
        Context = context;
    }
}

/// <summary>Serialization metadata backed by an explicitly supplied formatter.</summary>
public sealed class MemoryPackTypeInfo<T>(
    MemoryPackFormatter<T> formatter,
    MemoryPackSerializerContext? context = null
)
    : MemoryPackTypeInfo(
        typeof(T),
        formatter ?? throw new ArgumentNullException(nameof(formatter)),
        context
    )
{
    public new MemoryPackFormatter<T> Formatter { get; } = formatter;
}
