using MemoryPack;

namespace ZeroNativeAot;

internal static class CSharp15Smoke
{
    public static void Run()
    {
        CSharp15Registration.Register();
        var context = CSharp15SerializerContext.Default;
        var value = new CSharp15Result<int>(new CSharp15Success<int>(42));
        var bytes = MemoryPackSerializer.Serialize(value, context);
        var restored = MemoryPackSerializer.Deserialize<CSharp15Result<int>>(bytes, context);
        if (restored.Value is not CSharp15Success<int> { Value: 42 })
            throw new InvalidOperationException("C# union case did not round trip.");
        if (!bytes.SequenceEqual(MemoryPackSerializer.Serialize(value)))
            throw new InvalidOperationException("Union context changed the wire format.");
        var empty = MemoryPackSerializer.Deserialize<CSharp15Result<int>>(MemoryPackSerializer.Serialize(default(CSharp15Result<int>), context), context);
        if (empty.Value is not null) throw new InvalidOperationException("Default union was not empty.");
        CSharp15State state = new CSharp15Open(0.5f);
        if (MemoryPackSerializer.Deserialize<CSharp15State>(MemoryPackSerializer.Serialize(state, context), context) != state)
            throw new InvalidOperationException("Closed hierarchy did not round trip.");
    }
}

[MemoryPackable, MemoryPackUnion(300, typeof(CSharp15Success<>)), MemoryPackUnion(0, typeof(CSharp15Failure))]
internal readonly partial union CSharp15Result<T>(CSharp15Success<T>, CSharp15Failure);

[MemoryPackable]
internal sealed partial record CSharp15Success<T>(T Value);

[MemoryPackable]
internal sealed partial record CSharp15Failure(string Message);

[MemoryPackable, MemoryPackUnion(0, typeof(CSharp15Open)), MemoryPackUnion(1, typeof(CSharp15Closed))]
internal closed partial record CSharp15State;

[MemoryPackable]
internal sealed partial record CSharp15Open(float Percent) : CSharp15State;

[MemoryPackable]
internal sealed partial record CSharp15Closed : CSharp15State;

[MemoryPackSerializable<CSharp15Result<int>>, MemoryPackSerializable<CSharp15State>]
internal static partial class CSharp15Registration;

[MemoryPackSerializable<CSharp15Result<int>>, MemoryPackSerializable<CSharp15State>]
internal partial class CSharp15SerializerContext : MemoryPackSerializerContext;
