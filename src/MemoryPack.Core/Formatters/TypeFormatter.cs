using MemoryPack.Internal;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace MemoryPack.Formatters;

public sealed partial class TypeFormatter : MemoryPackFormatter<Type>
{
    static readonly ConcurrentDictionary<string, Type> registeredTypes = new(StringComparer.Ordinal);

    /// <summary>Registers an allowed type name for deserialization without runtime type discovery.</summary>
    public static void RegisterType<T>() => RegisterType(typeof(T));

    public static void RegisterType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var fullName = type.AssemblyQualifiedName;
        if (fullName == null) throw new ArgumentException("The type must have an assembly-qualified name.", nameof(type));
        var name = ShortTypeNameRegex().Replace(fullName, "");
        var registered = registeredTypes.GetOrAdd(name, type);
        if (registered != type)
        {
            MemoryPackSerializationException.ThrowMessage($"Serialized type name '{name}' is already registered for {registered}.");
        }
    }
    // Remove Version, Culture, PublicKeyToken from AssemblyQualifiedName.
    // Result will be "TypeName, Assembly"
    // see:http://msdn.microsoft.com/en-us/library/w3f99sx1.aspx

#if NET7_0_OR_GREATER

    [GeneratedRegex(@", Version=\d+.\d+.\d+.\d+, Culture=[\w-]+, PublicKeyToken=(?:null|[a-f0-9]{16})")]
    private static partial Regex ShortTypeNameRegex();

#else

    static readonly Regex _shortTypeNameRegex = new Regex(@", Version=\d+.\d+.\d+.\d+, Culture=[\w-]+, PublicKeyToken=(?:null|[a-f0-9]{16})", RegexOptions.Compiled);
    static Regex ShortTypeNameRegex() => _shortTypeNameRegex;

#endif

    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref Type? value)
    {
        var full = value?.AssemblyQualifiedName;
        if (full == null)
        {
            writer.WriteNullCollectionHeader();
            return;
        }

        var shortName = ShortTypeNameRegex().Replace(full, "");
        writer.WriteString(shortName);
    }

    public override void Deserialize(ref MemoryPackReader reader, scoped ref Type? value)
    {
        var typeName = reader.ReadString();
        if (typeName == null)
        {
            value = null;
            return;
        }

        if (registeredTypes.TryGetValue(typeName, out value)) return;
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            value = ResolveTypeWithReflection(typeName);
            return;
        }
        MemoryPackSerializationException.ThrowMessage($"Serialized type name '{typeName}' is not registered. Register allowed types with TypeFormatter.RegisterType<T>() before deserialization.");
    }

    [RequiresUnreferencedCode("Serialized type names cannot be determined statically.")]
    static Type ResolveTypeWithReflection(string name) => Type.GetType(name, throwOnError: true)!;
}
