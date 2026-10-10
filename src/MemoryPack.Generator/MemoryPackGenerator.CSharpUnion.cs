using Microsoft.CodeAnalysis;

namespace MemoryPack.Generator;

public partial class TypeMeta
{
    string EmitCSharpUnionSerializeBody()
    {
        var access = IsValueType ? "value.Value" : "value?.Value";
        var cases = UnionTags.Select((x, index) => $$"""
                case {{UnionPatternType(x.Type).FullyQualifiedToString()}} case{{index}}:
                    writer.WriteUnionHeader({{x.Tag}});
                    writer.WriteValue<{{x.Type.FullyQualifiedToString()}}>(case{{index}});
                    break;
""").NewLine();
        return $$"""
            var unionValue = {{access}};
            if (unionValue is null)
            {
                writer.WriteNullUnionHeader();
{{OnSerialized.Select(x => "                " + x.Emit()).NewLine()}}
                return;
            }
            switch (unionValue)
            {
{{cases}}
                default:
                    MemoryPackSerializationException.ThrowNotFoundInUnionType(unionValue.GetType(), typeof({{TypeName}}));
                    break;
            }
""";
    }

    string EmitCSharpUnionDeserializeBody()
    {
        var cases = UnionTags.Select(x => $$"""
                case {{x.Tag}}:
                    value = new {{TypeName}}(reader.ReadValue<{{x.Type.FullyQualifiedToString()}}>());
                    break;
""").NewLine();
        return $$"""
            if (!reader.TryReadUnionHeader(out var tag))
            {
                value = default;
{{OnDeserialized.Select(x => "                " + x.Emit()).NewLine()}}
                return;
            }
            switch (tag)
            {
{{cases}}
                default:
                    MemoryPackSerializationException.ThrowInvalidTag(tag, typeof({{TypeName}}));
                    break;
            }
""";
    }
}
