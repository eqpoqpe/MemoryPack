using Microsoft.CodeAnalysis;

namespace MemoryPack.Generator;

public partial class TypeMeta
{
    string EmitCSharpUnionFormatterFields()
    {
        return UnionTags.Where(x => UnionCaseFormatters.ContainsKey(x.Type)).Select(x =>
            $"        static readonly global::MemoryPack.MemoryPackFormatter<{x.Type.FullyQualifiedToString()}> __caseFormatter{x.Tag} = new {UnionCaseFormatters[x.Type].FullyQualifiedToString()}();").NewLine();
    }

    string EmitCSharpUnionSerializeBody()
    {
        var access = IsValueType ? "value.Value" : "value?.Value";
        var cases = UnionTags.Select((x, index) =>
        {
            var write = UnionCaseFormatters.ContainsKey(x.Type)
                ? $"var caseValue{index} = ({x.Type.FullyQualifiedToString()})case{index};\n                    __caseFormatter{x.Tag}.Serialize(ref writer, ref caseValue{index});"
                : $"writer.WriteValue<{x.Type.FullyQualifiedToString()}>(case{index});";
            return $$"""
                case {{UnionPatternType(x.Type).FullyQualifiedToString()}} case{{index}}:
                    writer.WriteUnionHeader({{x.Tag}});
                    {{write}}
                    break;
""";
        }).NewLine();
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
        var cases = UnionTags.Select(x => UnionCaseFormatters.ContainsKey(x.Type) ? $$"""
                case {{x.Tag}}:
                {
                    {{x.Type.FullyQualifiedToString()}} caseValue = default!;
                    __caseFormatter{{x.Tag}}.Deserialize(ref reader, ref caseValue);
                    value = new {{TypeName}}(caseValue);
                    break;
                }
""" : $$"""
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
