using System.Globalization;
using MemoryPack.Internal;

namespace MemoryPack.Formatters;

public sealed class CultureInfoFormatter : MemoryPackFormatter<CultureInfo>
{
    // treat as a string(Name).

    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref CultureInfo? value)
    {
        writer.WriteString(value?.Name);
    }

    public override void Deserialize(ref MemoryPackReader reader, scoped ref CultureInfo? value)
    {
        var str = reader.ReadString();
        if (str == null)
        {
            value = null;
        }
        else
        {
            value = CultureInfo.GetCultureInfo(str);
        }
    }
}