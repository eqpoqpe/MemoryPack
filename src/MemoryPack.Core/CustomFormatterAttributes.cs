using MemoryPack.Compression;
using MemoryPack.Formatters;

namespace MemoryPack;

public sealed class Utf8StringFormatterAttribute
    : MemoryPackCustomFormatterAttribute<Utf8StringFormatter, string>
{
    public override Utf8StringFormatter GetFormatter()
    {
        return Utf8StringFormatter.Default;
    }
}

public sealed class Utf16StringFormatterAttribute
    : MemoryPackCustomFormatterAttribute<Utf16StringFormatter, string>
{
    public override Utf16StringFormatter GetFormatter()
    {
        return Utf16StringFormatter.Default;
    }
}

public sealed class OrdinalIgnoreCaseStringDictionaryFormatter<TValue>
    : MemoryPackCustomFormatterAttribute<
        DictionaryFormatter<string, TValue?>,
        Dictionary<string, TValue?>
    >
{
    static readonly DictionaryFormatter<string, TValue?> formatter = new(
        StringComparer.OrdinalIgnoreCase
    );

    public override DictionaryFormatter<string, TValue?> GetFormatter()
    {
        return formatter;
    }
}

public sealed class InternStringFormatterAttribute
    : MemoryPackCustomFormatterAttribute<InternStringFormatter, string>
{
    public override InternStringFormatter GetFormatter()
    {
        return InternStringFormatter.Default;
    }
}

public sealed class BitPackFormatterAttribute
    : MemoryPackCustomFormatterAttribute<BitPackFormatter, bool[]>
{
    public override BitPackFormatter GetFormatter()
    {
        return BitPackFormatter.Default;
    }
}

public sealed class BrotliFormatterAttribute(
    System.IO.Compression.CompressionLevel compressionLevel =
        System.IO.Compression.CompressionLevel.Fastest,
    int window = BrotliUtils.WindowBits_Default,
    int decompressionSizeLimit = BrotliFormatter.DefaultDecompssionSizeLimit
) : MemoryPackCustomFormatterAttribute<BrotliFormatter, byte[]>
{
    public System.IO.Compression.CompressionLevel CompressionLevel { get; } = compressionLevel;
    public int Window { get; } = window;
    public int DecompressionSizeLimit { get; } = decompressionSizeLimit;

    public override BrotliFormatter GetFormatter()
    {
        return new BrotliFormatter(CompressionLevel, Window, DecompressionSizeLimit);
    }
}

public sealed class BrotliFormatterAttribute<T>(
    System.IO.Compression.CompressionLevel compressionLevel =
        System.IO.Compression.CompressionLevel.Fastest,
    int window = BrotliUtils.WindowBits_Default
) : MemoryPackCustomFormatterAttribute<BrotliFormatter<T>, T>
{
    public System.IO.Compression.CompressionLevel CompressionLevel { get; } = compressionLevel;
    public int Window { get; } = window;

    public override BrotliFormatter<T> GetFormatter()
    {
        return new BrotliFormatter<T>(CompressionLevel, Window);
    }
}

public sealed class BrotliStringFormatterAttribute(
    System.IO.Compression.CompressionLevel compressionLevel =
        System.IO.Compression.CompressionLevel.Fastest,
    int window = BrotliUtils.WindowBits_Default,
    int decompressionSizeLimit = BrotliFormatter.DefaultDecompssionSizeLimit
) : MemoryPackCustomFormatterAttribute<BrotliStringFormatter, string>
{
    public System.IO.Compression.CompressionLevel CompressionLevel { get; } = compressionLevel;
    public int Window { get; } = window;
    public int DecompressionSizeLimit { get; } = decompressionSizeLimit;

    public override BrotliStringFormatter GetFormatter()
    {
        return new BrotliStringFormatter(CompressionLevel, Window, DecompressionSizeLimit);
    }
}

public sealed class MemoryPoolFormatterAttribute<T>
    : MemoryPackCustomFormatterAttribute<MemoryPoolFormatter<T>, Memory<T?>>
{
    static readonly MemoryPoolFormatter<T> formatter = new();

    public override MemoryPoolFormatter<T> GetFormatter()
    {
        return formatter;
    }
}

public sealed class ReadOnlyMemoryPoolFormatterAttribute<T>
    : MemoryPackCustomFormatterAttribute<ReadOnlyMemoryPoolFormatter<T>, ReadOnlyMemory<T?>>
{
    static readonly ReadOnlyMemoryPoolFormatter<T> formatter = new();

    public override ReadOnlyMemoryPoolFormatter<T> GetFormatter()
    {
        return formatter;
    }
}
