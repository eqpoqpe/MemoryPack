using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using MemoryPack;

namespace ZeroNativeAot;

internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        var isAot = IsNativeAot();
        Console.WriteLine($"MemoryPack smoke tests ({(isAot ? "NativeAOT" : "JIT")})");
        if (args.Contains("--require-aot") && !isAot)
        {
            Console.Error.WriteLine("FAIL: --require-aot requires the published native executable.");
            return 1;
        }

        (string Name, Action Test)[] tests =
        [
            ("Primitives and unmanaged structs", Primitives),
            ("Generated objects and string encodings", GeneratedObjects),
            ("Closed generic models", Generics),
            ("Nested collections", Collections),
            ("Union dispatch", Unions),
            ("Version tolerance", VersionTolerance),
            ("Buffer writer", BufferWriter),
            ("Segmented sequence", SegmentedSequence),
            ("Overwrite deserialization", Overwrite),
            ("Null and empty values", NullAndEmpty),
            ("Truncated input rejection", TruncatedInput),
        ];

        var failures = 0;
        foreach (var (name, test) in tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS: {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL: {name}: {exception}");
            }
        }

        try
        {
            await StreamRoundTrip();
            Console.WriteLine("PASS: Async stream");
        }
        catch (Exception exception)
        {
            failures++;
            Console.Error.WriteLine($"FAIL: Async stream: {exception}");
        }

        Console.WriteLine($"{tests.Length + 1 - failures}/{tests.Length + 1} tests passed.");
        return failures == 0 ? 0 : 1;
    }

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "The empty assembly location is intentionally used to distinguish this native publish from its managed build.")]
    static bool IsNativeAot()
    {
        // PublishAot also disables dynamic-code feature flags in managed builds.
        return !RuntimeFeature.IsDynamicCodeSupported && !RuntimeFeature.IsDynamicCodeCompiled
            && typeof(Program).Assembly.Location.Length == 0;
    }

    static void Primitives()
    {
        Require(MemoryPackSerializer.Deserialize<int>(MemoryPackSerializer.Serialize(42)) == 42, "Integer changed.");
        var point = new Point(1.25f, -2.5f, 3.75f);
        Require(MemoryPackSerializer.Deserialize<Point>(MemoryPackSerializer.Serialize(point)) == point, "Struct changed.");
        int[] numbers = [int.MinValue, 0, int.MaxValue];
        Require(MemoryPackSerializer.Deserialize<int[]>(MemoryPackSerializer.Serialize(numbers))!.SequenceEqual(numbers), "Array changed.");
    }

    static void GeneratedObjects()
    {
        var value = CreateItem();
        foreach (var options in new[] { MemoryPackSerializerOptions.Utf8, MemoryPackSerializerOptions.Utf16 })
        {
            var bytes = MemoryPackSerializer.Serialize(value, options);
            AssertItem(value, MemoryPackSerializer.Deserialize<Item>(bytes, options));
        }

        var fixedSize = new FixedSizeModel { Id = 123, Value = 4.5 };
        var restored = MemoryPackSerializer.Deserialize<FixedSizeModel>(MemoryPackSerializer.Serialize(fixedSize));
        Require(restored is { Id: 123, Value: 4.5 }, "Fixed-size generated object changed.");
    }

    static void Generics()
    {
        var number = new Box<int> { Value = 123 };
        Require(MemoryPackSerializer.Deserialize<Box<int>>(MemoryPackSerializer.Serialize(number))?.Value == 123, "Generic value type changed.");
        var text = new Box<string> { Value = "泛型 🧪" };
        Require(MemoryPackSerializer.Deserialize<Box<string>>(MemoryPackSerializer.Serialize(text))?.Value == text.Value, "Generic reference type changed.");
        var nested = new Box<Item> { Value = CreateItem() };
        AssertItem(nested.Value!, MemoryPackSerializer.Deserialize<Box<Item>>(MemoryPackSerializer.Serialize(nested))?.Value);
    }

    static void Collections()
    {
        var item = CreateItem();
        var value = new CollectionModel
        {
            Items = [item, null],
            Lookup = new() { ["first"] = new Box<Item> { Value = item }, ["empty"] = new Box<Item>() },
            Names = ["", null, "集合"],
        };
        var restored = MemoryPackSerializer.Deserialize<CollectionModel>(MemoryPackSerializer.Serialize(value));
        Require(restored?.Items is { Count: 2 } && restored.Items[1] is null, "List shape changed.");
        AssertItem(item, restored!.Items![0]);
        Require(restored.Lookup is { Count: 2 } && restored.Lookup["empty"].Value is null, "Dictionary shape changed.");
        AssertItem(item, restored.Lookup!["first"].Value);
        Require(restored.Names!.SequenceEqual(value.Names), "String array changed.");
    }

    static void Unions()
    {
        IEvent first = new CreatedEvent { Id = 7, Name = "created" };
        var restoredFirst = MemoryPackSerializer.Deserialize<IEvent>(MemoryPackSerializer.Serialize(first));
        Require(restoredFirst is CreatedEvent { Id: 7, Name: "created" }, "First union case changed.");
        IEvent second = new DeletedEvent { Id = 8, Reason = "deleted" };
        var restoredSecond = MemoryPackSerializer.Deserialize<IEvent>(MemoryPackSerializer.Serialize(second));
        Require(restoredSecond is DeletedEvent { Id: 8, Reason: "deleted" }, "Extended union tag changed.");
    }

    static void VersionTolerance()
    {
        var oldValue = new ModelV1 { Id = 99 };
        var upgraded = MemoryPackSerializer.Deserialize<ModelV2>(MemoryPackSerializer.Serialize(oldValue));
        Require(upgraded is { Id: 99, Name: null }, "Missing member did not use its default.");
        var newValue = new ModelV2 { Id = 100, Name = "new member" };
        var downgraded = MemoryPackSerializer.Deserialize<ModelV1>(MemoryPackSerializer.Serialize(newValue));
        Require(downgraded is { Id: 100 }, "Unknown member was not skipped.");
    }

    static void BufferWriter()
    {
        var item = CreateItem();
        var buffer = new ArrayBufferWriter<byte>();
        MemoryPackSerializer.Serialize(buffer, item);
        Require(buffer.WrittenSpan.SequenceEqual(MemoryPackSerializer.Serialize(item)), "Buffer writer produced different bytes.");
        AssertItem(item, MemoryPackSerializer.Deserialize<Item>(buffer.WrittenSpan));
    }

    static void SegmentedSequence()
    {
        var item = CreateItem();
        var bytes = MemoryPackSerializer.Serialize(item);
        // One-byte segments force headers, unmanaged values, and strings across boundaries.
        var first = new Segment(bytes.AsMemory(0, 1));
        var last = first;
        for (var i = 1; i < bytes.Length; i++)
        {
            last = last.Append(bytes.AsMemory(i, 1));
        }
        var sequence = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        AssertItem(item, MemoryPackSerializer.Deserialize<Item>(sequence));
    }

    static void Overwrite()
    {
        var item = CreateItem();
        Item? target = new Item { Id = -1, Name = "old" };
        var original = target;
        var bytes = MemoryPackSerializer.Serialize(item);
        var consumed = MemoryPackSerializer.Deserialize(bytes, ref target);
        Require(consumed == bytes.Length, "Incorrect consumed byte count.");
        Require(ReferenceEquals(original, target), "Overwrite replaced the existing object.");
        AssertItem(item, target);
    }

    static void NullAndEmpty()
    {
        Require(MemoryPackSerializer.Deserialize<Item>(MemoryPackSerializer.Serialize<Item>(null)) is null, "Null object changed.");
        Require(MemoryPackSerializer.Deserialize<IEvent>(MemoryPackSerializer.Serialize<IEvent>(null)) is null, "Null union changed.");
        Require(MemoryPackSerializer.Deserialize<string>(MemoryPackSerializer.Serialize<string>(null)) is null, "Null string changed.");
        Require(MemoryPackSerializer.Deserialize<string>(MemoryPackSerializer.Serialize("")) == "", "Empty string changed.");
        Require(MemoryPackSerializer.Deserialize<int[]>(MemoryPackSerializer.Serialize<int[]>(null)) is null, "Null array changed.");
        Require(MemoryPackSerializer.Deserialize<int[]>(MemoryPackSerializer.Serialize(Array.Empty<int>())) is { Length: 0 }, "Empty array changed.");
        var empty = MemoryPackSerializer.Deserialize<CollectionModel>(MemoryPackSerializer.Serialize(new CollectionModel { Items = [], Lookup = [], Names = [] }));
        Require(empty is { Items.Count: 0, Lookup.Count: 0, Names.Length: 0 }, "Empty collections changed.");
        var absent = MemoryPackSerializer.Deserialize<CollectionModel>(MemoryPackSerializer.Serialize(new CollectionModel()));
        Require(absent is { Items: null, Lookup: null, Names: null }, "Null collections changed.");
    }

    static void TruncatedInput()
    {
        var bytes = MemoryPackSerializer.Serialize(CreateItem());
        try
        {
            MemoryPackSerializer.Deserialize<Item>(bytes.AsSpan(0, bytes.Length - 1));
        }
        catch (MemoryPackSerializationException)
        {
            return;
        }
        throw new InvalidOperationException("Truncated input was accepted.");
    }

    static async Task StreamRoundTrip()
    {
        var item = CreateItem();
        using var stream = new MemoryStream();
        await MemoryPackSerializer.SerializeAsync(stream, item);
        stream.Position = 0;
        AssertItem(item, await MemoryPackSerializer.DeserializeAsync<Item>(stream));
        Require(stream.Position == stream.Length, "Stream was not fully consumed.");
    }

    static Item CreateItem() => new() { Id = 42, Name = "MemoryPack 原生 🧪", Position = new Point(1, 2, 3) };

    static void AssertItem(Item expected, Item? actual)
    {
        Require(actual is not null, "Object became null.");
        Require(actual!.Id == expected.Id && actual.Name == expected.Name && actual.Position == expected.Position, "Object members changed.");
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
