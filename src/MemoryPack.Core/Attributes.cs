namespace MemoryPack;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class MemoryPackableAttribute : Attribute
{
    public GenerateType GenerateType { get; }
    public SerializeLayout SerializeLayout { get; }

    // ctor parameter is parsed in MemoryPackGenerator.Parser TypeMeta for detect which ctor used in MemoryPack.Generator.
    // if modify ctor, be careful.

    /// <summary>
    /// [generateType, (VersionTolerant or CircularReference) ? SerializeLayout.Explicit : SerializeLayout.Sequential]
    /// </summary>
    /// <param name="generateType"></param>
    public MemoryPackableAttribute(GenerateType generateType = GenerateType.Object)
    {
        this.GenerateType = generateType;
        this.SerializeLayout = (generateType == GenerateType.VersionTolerant || generateType == GenerateType.CircularReference)
            ? SerializeLayout.Explicit
            : SerializeLayout.Sequential;
    }

    /// <summary>
    /// [GenerateType.Object, serializeLayout]
    /// </summary>
    public MemoryPackableAttribute(SerializeLayout serializeLayout)
    {
        this.GenerateType = GenerateType.Object;
        this.SerializeLayout = serializeLayout;
    }

    public MemoryPackableAttribute(GenerateType generateType, SerializeLayout serializeLayout)
    {
        this.GenerateType = generateType;
        this.SerializeLayout = serializeLayout;
    }
}

public enum GenerateType
{
    Object,
    VersionTolerant,
    CircularReference,
    Collection,
    NoGenerate
}

/// <summary>Declares a root in a source-generated, static formatter registration context.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class MemoryPackSerializableAttribute<T> : Attribute
{
    public Type Type { get; } = typeof(T);

    /// <summary>Optional concrete formatter with an accessible parameterless constructor.</summary>
    public Type? FormatterType { get; set; }

    /// <summary>Optional root metadata property name in an instance serialization context.</summary>
    public string? TypeInfoPropertyName { get; set; }
}

/// <summary>Declares a root using a runtime type constant, including closed generic types.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class MemoryPackSerializableAttribute(Type type) : Attribute
{
    public Type Type { get; } = type;
    public Type? FormatterType { get; set; }
    public string? TypeInfoPropertyName { get; set; }
}

public enum SerializeLayout
{
    Sequential, // default
    Explicit
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, AllowMultiple = true, Inherited = false)]
public sealed class MemoryPackUnionAttribute(ushort tag, Type type) : Attribute
{
    public ushort Tag { get; } = tag;
    public Type Type { get; } = type;
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MemoryPackUnionFormatterAttribute(Type type) : Attribute
{
    public Type Type { get; } = type;
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class MemoryPackAllowSerializeAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class MemoryPackOrderAttribute(int order) : Attribute
{
    public int Order { get; } = order;
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public abstract class MemoryPackCustomFormatterAttribute<T> : Attribute
{
    public abstract IMemoryPackFormatter<T> GetFormatter();
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public abstract class MemoryPackCustomFormatterAttribute<TFormatter, T> : Attribute
    where TFormatter : IMemoryPackFormatter<T>
{
    public abstract TFormatter GetFormatter();
}


// similar naming as System.Text.Json attribtues
// https://docs.microsoft.com/en-us/dotnet/api/system.text.json.serialization.jsonattribute

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class MemoryPackIgnoreAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class MemoryPackIncludeAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
public sealed class MemoryPackConstructorAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class MemoryPackOnSerializingAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class MemoryPackOnSerializedAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class MemoryPackOnDeserializingAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class MemoryPackOnDeserializedAttribute : Attribute
{
}

// Others

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class GenerateTypeScriptAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class SuppressDefaultInitializationAttribute : Attribute;
