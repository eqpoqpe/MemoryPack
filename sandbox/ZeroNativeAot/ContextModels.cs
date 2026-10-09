using MemoryPack;
using MemoryPack.Formatters;

namespace ZeroNativeAot;

[MemoryPackable]
public partial class RecursiveNode
{
    public string? Name { get; set; }
    public List<RecursiveNode>? Children { get; set; }
}

[MemoryPackable(GenerateType.CircularReference)]
public partial class CircularNode
{
    [MemoryPackOrder(0)]
    public int Id { get; set; }

    [MemoryPackOrder(1)]
    public CircularNode? Next { get; set; }
}

[MemoryPackable]
public partial record struct NamedValue(string? Name, int Id);

[MemoryPackable]
public partial class CustomModel
{
    [MemoryPackInclude]
    [ArgumentFormatter(((byte)7), TestKind.Second, typeof(Item), [1, 2], null, Prefix = "custom")]
    private string? text;

    [MemoryPackIgnore]
    public string? Text
    {
        get => text;
        set => text = value;
    }
}

public enum TestKind
{
    First,
    Second,
}

public sealed class ArgumentFormatterAttribute(
    object number,
    TestKind kind,
    Type type,
    int[] numbers,
    string? optional = null
) : MemoryPackCustomFormatterAttribute<string>
{
    readonly object number = number;
    readonly TestKind kind = kind;
    readonly Type type = type;
    readonly int[] numbers = numbers;
    readonly string? optional = optional;
    public string Prefix { get; set; } = "";

    public override IMemoryPackFormatter<string> GetFormatter()
    {
        if (
            !(
                number is byte and 7
                && kind == TestKind.Second
                && type == typeof(Item)
                && numbers.SequenceEqual([1, 2])
                && optional == null
                && Prefix == "custom"
            )
        )
            throw new InvalidOperationException("Attribute construction changed its arguments.");
        return new StringFormatter();
    }
}

[MemoryPackable(GenerateType.NoGenerate)]
public partial interface IReply<T>;

[MemoryPackUnionFormatter(typeof(IReply<>))]
[MemoryPackUnion(0, typeof(Reply<>))]
[MemoryPackUnion(1, typeof(Rejected<>))]
public partial class ReplyFormatter<T>;

[MemoryPackable]
public partial class Reply<T> : IReply<T>
{
    public T? Value { get; set; }
}

[MemoryPackable]
public partial class Rejected<T> : IReply<T>
{
    public string? Reason { get; set; }
}

public sealed record ExternalValue(string Text);

public sealed class ExternalFormatter : MemoryPackFormatter<ExternalValue>
{
    public override void Serialize<TBufferWriter>(
        ref MemoryPackWriter<TBufferWriter> writer,
        scoped ref ExternalValue? value
    ) => writer.WriteString(value?.Text);

    public override void Deserialize(
        ref MemoryPackReader reader,
        scoped ref ExternalValue? value
    ) => value = new(reader.ReadString()!);
}

[MemoryPackSerializable(typeof(ExternalValue), FormatterType = typeof(ExternalFormatter))]
internal static partial class RecoveryContext;

public sealed class CountingStringFormatter : MemoryPackFormatter<string>
{
    public static int Calls { get; set; }

    public override void Serialize<TBufferWriter>(
        ref MemoryPackWriter<TBufferWriter> writer,
        scoped ref string? value
    )
    {
        Calls++;
        writer.WriteString(value);
    }

    public override void Deserialize(ref MemoryPackReader reader, scoped ref string? value) =>
        value = reader.ReadString();
}

public sealed class CountingListFormatter : MemoryPackFormatter<List<ExternalValue?>>
{
    readonly ListFormatter<ExternalValue> inner = new();

    public static int Calls { get; set; }

    public override void Serialize<TBufferWriter>(
        ref MemoryPackWriter<TBufferWriter> writer,
        scoped ref List<ExternalValue?>? value
    )
    {
        Calls++;
        inner.Serialize(ref writer, ref value);
    }

    public override void Deserialize(
        ref MemoryPackReader reader,
        scoped ref List<ExternalValue?>? value
    ) => inner.Deserialize(ref reader, ref value);
}

[MemoryPackable]
public partial class OverrideModel
{
    [MemoryPackAllowSerialize]
    public List<ExternalValue>? Items { get; set; }
}

[MemoryPackSerializable<OverrideModel>]
[MemoryPackSerializable(typeof(string), FormatterType = typeof(CountingStringFormatter))]
[MemoryPackSerializable<List<ExternalValue>>(FormatterType = typeof(CountingListFormatter))]
internal static partial class OverrideContext;
