using MemoryPack;
using MemoryPack.Formatters;

namespace ZeroNativeAot;

[MemoryPackable]
public partial class NamedTupleModel
{
    public (string First, int Number) Left { get; set; }
    public (string Text, int Id) Right { get; set; }
    public List<(string Label, int Code)>? Values { get; set; }
}

[MemoryPackSerializable<OverrideModel>]
[MemoryPackSerializable<ExternalValue>(FormatterType = typeof(FirstValueFormatter))]
[MemoryPackSerializable<int>(FormatterType = typeof(OffsetIntFormatter))]
internal partial class FirstSerializerContext : MemoryPackSerializerContext;

[MemoryPackSerializable<OverrideModel>]
[MemoryPackSerializable<ExternalValue>(FormatterType = typeof(SecondValueFormatter))]
internal partial class SecondSerializerContext : MemoryPackSerializerContext;

[MemoryPackSerializable<OverrideModel>]
[MemoryPackSerializable<List<ExternalValue>>(FormatterType = typeof(ListFormatter<ExternalValue>))]
internal partial class MissingSerializerContext : MemoryPackSerializerContext;

public sealed class FirstValueFormatter : MemoryPackFormatter<ExternalValue>
{
    public override void Serialize<TBufferWriter>(
        ref MemoryPackWriter<TBufferWriter> writer,
        scoped ref ExternalValue? value
    ) => writer.WriteString("first:" + value?.Text);

    public override void Deserialize(
        ref MemoryPackReader reader,
        scoped ref ExternalValue? value
    ) => value = new(reader.ReadString()![6..]);
}

public sealed class SecondValueFormatter : MemoryPackFormatter<ExternalValue>
{
    public override void Serialize<TBufferWriter>(
        ref MemoryPackWriter<TBufferWriter> writer,
        scoped ref ExternalValue? value
    ) => writer.WriteString("second:" + value?.Text);

    public override void Deserialize(
        ref MemoryPackReader reader,
        scoped ref ExternalValue? value
    ) => value = new(reader.ReadString()![7..]);
}

public sealed class OffsetIntFormatter : MemoryPackFormatter<int>
{
    public override void Serialize<TBufferWriter>(
        ref MemoryPackWriter<TBufferWriter> writer,
        scoped ref int value
    ) => writer.WriteUnmanaged(value + 1000);

    public override void Deserialize(ref MemoryPackReader reader, scoped ref int value) =>
        value = reader.ReadUnmanaged<int>() - 1000;
}
