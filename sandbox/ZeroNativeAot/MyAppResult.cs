using MemoryPack;
using Polyester;

namespace ZeroNativeAot;

// These case types come from Polyester and require no changes to that package.
[MemoryPackable]
[MemoryPackUnion(0, typeof(Success<>), FormatterType = typeof(SuccessFormatter<>))]
[MemoryPackUnion(1, typeof(Failure<>), FormatterType = typeof(FailureFormatter<>))]
public readonly partial union MyAppResult<TValue, TError>(Success<TValue>, Failure<TError>);

public sealed class SuccessFormatter<T> : MemoryPackFormatter<Success<T>>
{
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref Success<T> value)
        => writer.WriteValue<T>(value.Value);
    public override void Deserialize(ref MemoryPackReader reader, scoped ref Success<T> value)
        => value = new(reader.ReadValue<T>()!);
}

public sealed class FailureFormatter<T> : MemoryPackFormatter<Failure<T>>
{
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref Failure<T> value)
        => writer.WriteValue<T>(value.Error);
    public override void Deserialize(ref MemoryPackReader reader, scoped ref Failure<T> value)
        => value = new(reader.ReadValue<T>()!);
}

[MemoryPackSerializable<MyAppResult<string, int>>]
[MemoryPackSerializable<string>, MemoryPackSerializable<int>]
internal partial class ResultContext : MemoryPackSerializerContext;

[MemoryPackSerializable<MyAppResult<string, int>>]
[MemoryPackSerializable<string>, MemoryPackSerializable<int>]
internal static partial class ResultRegistration;

internal static class ExternalResultSmoke
{
    public static void Run()
    {
        var context = ResultContext.Default;
        MyAppResult<string, int>[] values =
        [
            default,
            new(new Success<string>("ok")),
            new(new Success<string>(null!)),
            new(new Failure<int>(42))
        ];
        // Use instance metadata before any static registration.
        foreach (var value in values)
        {
            var bytes = MemoryPackSerializer.Serialize(value, context);
            var restored = MemoryPackSerializer.Deserialize<MyAppResult<string, int>>(bytes, context);
            if (!Equals(value.Value, restored.Value))
                throw new InvalidOperationException("External result case did not round trip through its context.");
        }
        ResultRegistration.Register();
        foreach (var value in values)
        {
            var bytes = MemoryPackSerializer.Serialize(value);
            if (!bytes.SequenceEqual(MemoryPackSerializer.Serialize(value, context)))
                throw new InvalidOperationException("Result context changed the wire format.");
            var restored = MemoryPackSerializer.Deserialize<MyAppResult<string, int>>(bytes);
            if (!Equals(value.Value, restored.Value))
                throw new InvalidOperationException("External result case did not round trip with static registration.");
        }
    }
}
