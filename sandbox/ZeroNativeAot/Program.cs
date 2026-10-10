using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using MemoryPack;

namespace ZeroNativeAot;

internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        Parallel.For(0, 32, _ => AppMemoryPackContext.Register());
        var isAot = IsNativeAot();
        Console.WriteLine($"MemoryPack smoke tests ({(isAot ? "NativeAOT" : "JIT")})");
        if (args.Contains("--require-aot") && !isAot)
        {
            Console.Error.WriteLine(
                "FAIL: --require-aot requires the published native executable."
            );
            return 1;
        }
        if (args.Contains("--deserialize-first-model"))
        {
            DeserializeFirstModel();
            Console.WriteLine("PASS: isolated deserialize-first model");
            return 0;
        }
        if (args.Contains("--deserialize-first-union"))
        {
            DeserializeFirstUnion();
            Console.WriteLine("PASS: isolated deserialize-first union");
            return 0;
        }

        (string Name, Action Test)[] tests =
        [
            ("Deserialize before construction, with null first", DeserializeFirst),
            ("Primitives and unmanaged structs", Primitives),
            ("Generated objects and string encodings", GeneratedObjects),
            ("Closed generic models", Generics),
            ("Nested collections", Collections),
            ("Union dispatch", Unions),
            ("C# 15 unions and closed hierarchies", CSharp15Smoke.Run),
            ("Version tolerance", VersionTolerance),
            ("Buffer writer", BufferWriter),
            ("Segmented sequence", SegmentedSequence),
            ("Overwrite deserialization", Overwrite),
            ("Null and empty values", NullAndEmpty),
            ("Truncated input rejection", TruncatedInput),
            ("Recursive and circular dependency graphs", RecursiveGraphs),
            ("Collection roots and managed nullable structs", ContextRoots),
            ("Closed external generic union", ExternalUnion),
            ("Direct custom attribute construction", CustomAttributes),
            ("Missing registration and recovery", MissingRegistration),
            ("Bounded System.Type mapping", TypeMapping),
            ("Explicit formatter precedence", ExplicitFormatters),
            ("Baseline wire fixtures", BaselineWireFixtures),
            ("Instance context metadata and root formatters", InstanceContexts),
            ("Instance context isolation and options", ContextIsolation),
            ("Computed collection formatter dependencies", ContextCollections),
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
            await InstanceStreamRoundTrip();
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

    static void InstanceContexts()
    {
        var context = new AppSerializerContext();
        Require(
            ReferenceEquals(context.Item, context.GetTypeInfo(typeof(Item)))
                && context.Item.Context == context,
            "Metadata identity/ownership changed."
        );
        Require(
            context.GetTypeInfo(typeof(ExternalValue)) == null,
            "Unknown metadata was accepted."
        );
        Require(
            MemoryPackSerializer.Deserialize(new byte[] { 255 }, context.Item) == null,
            "Instance null-first model failed."
        );
        Require(
            MemoryPackSerializer.Deserialize<IEvent>(new byte[] { 255 }, context) == null,
            "Instance null-first union failed."
        );
        var item = CreateItem();
        var bytes = MemoryPackSerializer.Serialize(item, context.Item);
        Require(
            bytes.SequenceEqual(MemoryPackSerializer.Serialize(item)),
            "Instance context changed default bytes."
        );
        AssertItem(item, MemoryPackSerializer.Deserialize<Item>(bytes, context));
        AssertItem(
            item,
            (Item?)MemoryPackSerializer.Deserialize((MemoryPackTypeInfo)context.Item, bytes)
        );
        var buffer = new ArrayBufferWriter<byte>();
        MemoryPackSerializer.Serialize(buffer, item, context);
        Require(buffer.WrittenSpan.SequenceEqual(bytes), "Context buffer writer changed bytes.");
        var first = new Segment(bytes.AsMemory(0, 2));
        var last = first.Append(bytes.AsMemory(2));
        var sequence = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        Item? target = new();
        var original = target;
        Require(
            MemoryPackSerializer.Deserialize(sequence, ref target, context.Item) == bytes.Length
                && ReferenceEquals(target, original),
            "Context sequence overwrite failed."
        );
        AssertItem(item, target);
        var circle = new CircularNode { Id = 5 };
        circle.Next = circle;
        var restored = MemoryPackSerializer.Deserialize<CircularNode>(
            MemoryPackSerializer.Serialize(circle, context),
            context
        );
        Require(ReferenceEquals(restored, restored!.Next), "Context circular identity failed.");
        IReply<int> reply = new Reply<int> { Value = 19 };
        Require(
            MemoryPackSerializer.Deserialize<IReply<int>>(
                MemoryPackSerializer.Serialize(reply, context),
                context
            )
                is Reply<int> { Value: 19 },
            "Context external union failed."
        );
        var manual = new MemoryPackTypeInfo<int>(new OffsetIntFormatter());
        Require(
            MemoryPackSerializer.Serialize(7, manual).SequenceEqual(BitConverter.GetBytes(1007)),
            "Manual unmanaged root formatter ignored."
        );
        Require(
            MemoryPackSerializer.Deserialize(
                MemoryPackSerializer.Serialize((MemoryPackTypeInfo)manual, 7),
                manual
            ) == 7,
            "Untyped scalar metadata failed."
        );
        var tuples = new NamedTupleModel
        {
            Left = ("left", 1),
            Right = ("right", 2),
            Values = [("list", 3)],
        };
        var restoredTuples = MemoryPackSerializer.Deserialize<NamedTupleModel>(
            MemoryPackSerializer.Serialize(tuples, context),
            context
        )!;
        Require(
            restoredTuples.Left == tuples.Left
                && restoredTuples.Right == tuples.Right
                && restoredTuples.Values![0] == tuples.Values[0],
            "Named tuple metadata identity failed."
        );
        var named = MemoryPackSerializer.Deserialize(
            MemoryPackSerializer.Serialize(("name", 4), context.NameAndId),
            context.NameAndId
        );
        Require(named.Name == "name" && named.Id == 4, "Named tuple root annotations changed.");
    }

    static void ContextIsolation()
    {
        var first = new FirstSerializerContext();
        var second = new SecondSerializerContext();
        var value = new OverrideModel { Items = [new("context")] };
        var a = MemoryPackSerializer.Serialize(value, first);
        var b = MemoryPackSerializer.Serialize(value, second);
        Require(!a.SequenceEqual(b), "Independent context mappings were merged.");
        Parallel.For(
            0,
            32,
            i =>
            {
                var context = i % 2 == 0 ? (MemoryPackSerializerContext)first : second;
                var bytes = MemoryPackSerializer.Serialize(value, context);
                Require(
                    MemoryPackSerializer.Deserialize<OverrideModel>(bytes, context)?.Items?[0].Text
                        == "context",
                    "Concurrent contexts leaked formatter state."
                );
            }
        );
        Require(
            MemoryPackSerializer.Serialize(7, first).SequenceEqual(BitConverter.GetBytes(1007)),
            "Instance scalar root override ignored."
        );
        Require(
            MemoryPackSerializer.Serialize(7).SequenceEqual(BitConverter.GetBytes(7)),
            "Instance override leaked globally."
        );
        try
        {
            MemoryPackSerializer.Serialize(value, new MissingSerializerContext());
        }
        catch (MemoryPackSerializationException exception)
        {
            Require(
                exception.Message.Contains(nameof(ExternalValue)),
                "Missing context dependency error is unclear."
            );
            var utf8 = new AppSerializerContext(MemoryPackSerializerOptions.Utf8);
            var utf16 = new AppSerializerContext(MemoryPackSerializerOptions.Utf16);
            var item = CreateItem();
            var bytes = MemoryPackSerializer.Serialize(item, utf8);
            Require(
                !bytes.SequenceEqual(MemoryPackSerializer.Serialize(item, utf16)),
                "Context string options ignored."
            );
            AssertItem(item, MemoryPackSerializer.Deserialize<Item>(bytes, utf8));
            return;
        }
        throw new InvalidOperationException("Missing context dependency used global fallback.");
    }

    static void ContextCollections()
    {
        var context = AppSerializerContext.Default;
        var queue = new PriorityQueue<Item, int>();
        queue.Enqueue(CreateItem(), 7);
        var bytes = MemoryPackSerializer.Serialize(queue, context);
        AssertItem(
            CreateItem(),
            MemoryPackSerializer.Deserialize<PriorityQueue<Item, int>>(bytes, context)!.Dequeue()
        );
        AssertItem(
            CreateItem(),
            MemoryPackSerializer
                .Deserialize<PriorityQueue<Item, int>>(MemoryPackSerializer.Serialize(queue))!
                .Dequeue()
        );
        ILookup<string, Item> lookup = new[] { CreateItem() }.ToLookup(x => "key");
        var lookupBytes = MemoryPackSerializer.Serialize(lookup, context);
        AssertItem(
            CreateItem(),
            MemoryPackSerializer
                .Deserialize<ILookup<string, Item>>(lookupBytes, context)!["key"]
                .Single()
        );
        AssertItem(
            CreateItem(),
            MemoryPackSerializer
                .Deserialize<ILookup<string, Item>>(MemoryPackSerializer.Serialize(lookup))!["key"]
                .Single()
        );
        IReadOnlyList<Item> values = new[] { CreateItem() };
        AssertItem(
            CreateItem(),
            MemoryPackSerializer.Deserialize<IReadOnlyList<Item>>(
                MemoryPackSerializer.Serialize(values, context),
                context
            )![0]
        );
    }

    static async Task InstanceStreamRoundTrip()
    {
        var context = AppSerializerContext.Default;
        var item = CreateItem();
        using var stream = new MemoryStream();
        await MemoryPackSerializer.SerializeAsync(stream, item, context.Item);
        stream.Position = 0;
        AssertItem(item, await MemoryPackSerializer.DeserializeAsync<Item>(stream, context));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await MemoryPackSerializer.SerializeAsync(stream, item, context, canceled.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException("Context cancellation was ignored.");
    }

    static void DeserializeFirst()
    {
        DeserializeFirstModel();
        DeserializeFirstUnion();
    }

    static void DeserializeFirstModel()
    {
        Require(
            MemoryPackSerializer.Deserialize<FixedSizeModel>([255]) == null,
            "First null model failed."
        );
        byte[] fixedBytes = [2, 123, 0, 0, 0, 0, 0, 0, 0, 0, 0, 18, 64];
        Require(
            MemoryPackSerializer.Deserialize<FixedSizeModel>(fixedBytes) is { Id: 123, Value: 4.5 },
            "Deserialize-first model failed."
        );
        Require(
            MemoryPackSerializer
                .Serialize(new FixedSizeModel { Id = 123, Value = 4.5 })
                .SequenceEqual(fixedBytes),
            "Fixed model baseline bytes changed."
        );
    }

    static void DeserializeFirstUnion()
    {
        Require(
            MemoryPackSerializer.Deserialize<IEvent>(new byte[] { 255 }) == null,
            "First null union failed."
        );
        byte[] unionBytes = [0, 2, 7, 0, 0, 0, 255, 255, 255, 255];
        Require(
            MemoryPackSerializer.Deserialize<IEvent>(unionBytes)
                is CreatedEvent { Id: 7, Name: null },
            "Deserialize-first union failed."
        );
        Require(
            MemoryPackSerializer
                .Serialize<IEvent>(new CreatedEvent { Id = 7 })
                .SequenceEqual(unionBytes),
            "Union baseline bytes changed."
        );
    }

    static void BaselineWireFixtures()
    {
        var integers = new[] { int.MinValue, 0, int.MaxValue };
        var arrayBytes = Convert.FromHexString("030000000000008000000000FFFFFF7F");
        Require(
            MemoryPackSerializer.Serialize(integers).SequenceEqual(arrayBytes),
            "Unmanaged array baseline bytes changed."
        );
        Require(
            MemoryPackSerializer.Deserialize<int[]>(arrayBytes)!.SequenceEqual(integers),
            "Unmanaged array baseline read failed."
        );
        var fixedBytes = Convert.FromHexString("02070000000000000000000CC0");
        var fixedModel = new FixedSizeModel { Id = 7, Value = -3.5 };
        Require(
            MemoryPackSerializer.Serialize(fixedModel).SequenceEqual(fixedBytes),
            "Fixed model baseline bytes changed."
        );
        Require(
            MemoryPackSerializer.Deserialize<FixedSizeModel>(fixedBytes) is { Id: 7, Value: -3.5 },
            "Fixed model baseline read failed."
        );
        var modelArray = Convert.FromHexString("0100000002070000000000000000000CC0");
        Require(
            MemoryPackSerializer.Serialize(new[] { fixedModel }).SequenceEqual(modelArray),
            "Fixed model array baseline bytes changed."
        );
        Require(
            MemoryPackSerializer.Deserialize<FixedSizeModel[]>(modelArray)![0].Value == -3.5,
            "Fixed model array baseline read failed."
        );
        foreach (
            var (options, hex) in new[]
            {
                (MemoryPackSerializerOptions.Utf8, "0102F9FFFFFF02000000E5908DE5AD972A000000"),
                (MemoryPackSerializerOptions.Utf16, "0102020000000D54575B2A000000"),
            }
        )
        {
            NamedValue? named = new NamedValue("名字", 42);
            var bytes = Convert.FromHexString(hex);
            Require(
                MemoryPackSerializer.Serialize(named, options).SequenceEqual(bytes),
                "String encoding baseline bytes changed."
            );
            Require(
                MemoryPackSerializer.Deserialize<NamedValue?>(bytes, options) == named,
                "String encoding baseline read failed."
            );
        }
        var unionBytes = Convert.FromHexString("000113000000");
        Require(
            MemoryPackSerializer
                .Serialize<IReply<int>>(new Reply<int> { Value = 19 })
                .SequenceEqual(unionBytes),
            "Generic union baseline bytes changed."
        );
        Require(
            MemoryPackSerializer.Deserialize<IReply<int>>(unionBytes) is Reply<int> { Value: 19 },
            "Generic union baseline read failed."
        );
    }

    static void RecursiveGraphs()
    {
        var node = new RecursiveNode { Name = "root", Children = [new() { Name = "child" }] };
        Require(
            MemoryPackSerializer
                .Deserialize<RecursiveNode>(MemoryPackSerializer.Serialize(node))
                ?.Children?[0].Name == "child",
            "Recursive graph failed."
        );
        var circle = new CircularNode { Id = 5 };
        circle.Next = circle;
        var circleBytes = MemoryPackSerializer.Serialize(circle);
        Require(
            Convert.ToHexString(circleBytes) == "0204020005000000FA00",
            "Circular model baseline bytes changed."
        );
        var restored = MemoryPackSerializer.Deserialize<CircularNode>(circleBytes);
        Require(ReferenceEquals(restored, restored!.Next), "Circular reference identity changed.");
    }

    static void ContextRoots()
    {
        List<Box<Item>> values = [new() { Value = CreateItem() }];
        AssertItem(
            values[0].Value!,
            MemoryPackSerializer
                .Deserialize<List<Box<Item>>>(MemoryPackSerializer.Serialize(values))![0]
                .Value
        );
        NamedValue? named = new NamedValue("nullable", 19);
        Require(
            MemoryPackSerializer.Deserialize<NamedValue?>(MemoryPackSerializer.Serialize(named))
                == named,
            "Managed nullable struct failed."
        );
        Require(
            MemoryPackSerializer.Deserialize<NamedValue?>(
                MemoryPackSerializer.Serialize<NamedValue?>(null)
            ) == null,
            "Empty managed nullable struct failed."
        );
        var array = new[]
        {
            new FixedSizeModel { Id = 12, Value = 2 },
        };
        Require(
            MemoryPackSerializer
                .Deserialize<FixedSizeModel[]>(MemoryPackSerializer.Serialize(array))![0]
                .Id == 12,
            "Fixed-size model array failed."
        );
    }

    static void ExternalUnion()
    {
        IReply<int> reply = new Reply<int> { Value = 42 };
        Require(
            MemoryPackSerializer.Deserialize<IReply<int>>(MemoryPackSerializer.Serialize(reply))
                is Reply<int> { Value: 42 },
            "External generic union failed."
        );
        reply = new Rejected<int> { Reason = "no" };
        Require(
            MemoryPackSerializer.Deserialize<IReply<int>>(MemoryPackSerializer.Serialize(reply))
                is Rejected<int> { Reason: "no" },
            "External union second case failed."
        );
    }

    static void CustomAttributes()
    {
        var model = new CustomModel { Text = "attribute" };
        Require(
            MemoryPackSerializer
                .Deserialize<CustomModel>(MemoryPackSerializer.Serialize(model))
                ?.Text == model.Text,
            "Custom member formatter failed."
        );
    }

    static void MissingRegistration()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            try
            {
                MemoryPackSerializer.Deserialize<ExternalValue>([255, 255, 255, 255]);
            }
            catch (MemoryPackSerializationException exception)
            {
                Require(
                    exception.Message.Contains(nameof(ExternalValue))
                        && exception.Message.Contains("Register"),
                    "Missing registration error is not actionable."
                );
                Require(
                    !MemoryPackFormatterProvider.IsRegistered<ExternalValue>(),
                    "Missing registration poisoned the provider."
                );
                RecoveryContext.Register();
                var value = new ExternalValue("recovered");
                Require(
                    MemoryPackSerializer.Deserialize<ExternalValue>(
                        MemoryPackSerializer.Serialize(value)
                    ) == value,
                    "Registration after a miss did not recover."
                );
                return;
            }
            throw new InvalidOperationException("Unregistered type was accepted.");
        }
        RecoveryContext.Register();
    }

    static void TypeMapping()
    {
        MemoryPack.Formatters.TypeFormatter.RegisterType<Item>();
        Require(
            MemoryPackSerializer.Deserialize<Type>(MemoryPackSerializer.Serialize(typeof(Item)))
                == typeof(Item),
            "Registered type mapping failed."
        );
        Require(
            MemoryPackSerializer.Deserialize<Type>(MemoryPackSerializer.Serialize<Type>(null))
                == null,
            "Null type mapping failed."
        );
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            try
            {
                MemoryPackSerializer.Deserialize<Type>(
                    MemoryPackSerializer.Serialize(typeof(Box<int>))
                );
            }
            catch (MemoryPackSerializationException exception)
            {
                Require(
                    exception.Message.Contains("RegisterType"),
                    "Unknown type name error is not actionable."
                );
                return;
            }
            throw new InvalidOperationException("Unknown type name was accepted.");
        }
    }

    static void ExplicitFormatters()
    {
        Parallel.For(0, 32, _ => OverrideContext.Register());
        MemoryPackSerializer.Serialize(typeof(string), "override");
        Require(CountingStringFormatter.Calls > 0, "Explicit builtin formatter was ignored.");
        MemoryPackSerializer.Serialize(new OverrideModel { Items = [] });
        Require(
            CountingListFormatter.Calls > 0,
            "Explicit nested collection formatter was ignored."
        );
    }

    [UnconditionalSuppressMessage(
        "SingleFile",
        "IL3000",
        Justification = "The empty assembly location is intentionally used to distinguish this native publish from its managed build."
    )]
    static bool IsNativeAot()
    {
        // PublishAot also disables dynamic-code feature flags in managed builds.
        return !RuntimeFeature.IsDynamicCodeSupported
            && !RuntimeFeature.IsDynamicCodeCompiled
            && typeof(Program).Assembly.Location.Length == 0;
    }

    static void Primitives()
    {
        Require(
            MemoryPackSerializer.Deserialize<int>(MemoryPackSerializer.Serialize(42)) == 42,
            "Integer changed."
        );
        var point = new Point(1.25f, -2.5f, 3.75f);
        Require(
            MemoryPackSerializer.Deserialize<Point>(MemoryPackSerializer.Serialize(point)) == point,
            "Struct changed."
        );
        int[] numbers = [int.MinValue, 0, int.MaxValue];
        Require(
            MemoryPackSerializer
                .Deserialize<int[]>(MemoryPackSerializer.Serialize(numbers))!
                .SequenceEqual(numbers),
            "Array changed."
        );
    }

    static void GeneratedObjects()
    {
        var value = CreateItem();
        foreach (
            var options in new[]
            {
                MemoryPackSerializerOptions.Utf8,
                MemoryPackSerializerOptions.Utf16,
            }
        )
        {
            var bytes = MemoryPackSerializer.Serialize(value, options);
            AssertItem(value, MemoryPackSerializer.Deserialize<Item>(bytes, options));
        }

        var fixedSize = new FixedSizeModel { Id = 123, Value = 4.5 };
        var restored = MemoryPackSerializer.Deserialize<FixedSizeModel>(
            MemoryPackSerializer.Serialize(fixedSize)
        );
        Require(restored is { Id: 123, Value: 4.5 }, "Fixed-size generated object changed.");
    }

    static void Generics()
    {
        var number = new Box<int> { Value = 123 };
        Require(
            MemoryPackSerializer
                .Deserialize<Box<int>>(MemoryPackSerializer.Serialize(number))
                ?.Value == 123,
            "Generic value type changed."
        );
        var text = new Box<string> { Value = "泛型 🧪" };
        Require(
            MemoryPackSerializer
                .Deserialize<Box<string>>(MemoryPackSerializer.Serialize(text))
                ?.Value == text.Value,
            "Generic reference type changed."
        );
        var nested = new Box<Item> { Value = CreateItem() };
        AssertItem(
            nested.Value!,
            MemoryPackSerializer
                .Deserialize<Box<Item>>(MemoryPackSerializer.Serialize(nested))
                ?.Value
        );
    }

    static void Collections()
    {
        var item = CreateItem();
        var value = new CollectionModel
        {
            Items = [item, null],
            Lookup = new()
            {
                ["first"] = new Box<Item> { Value = item },
                ["empty"] = new Box<Item>(),
            },
            Names = ["", null, "集合"],
        };
        var restored = MemoryPackSerializer.Deserialize<CollectionModel>(
            MemoryPackSerializer.Serialize(value)
        );
        Require(
            restored?.Items is { Count: 2 } && restored.Items[1] is null,
            "List shape changed."
        );
        AssertItem(item, restored!.Items![0]);
        Require(
            restored.Lookup is { Count: 2 } && restored.Lookup["empty"].Value is null,
            "Dictionary shape changed."
        );
        AssertItem(item, restored.Lookup!["first"].Value);
        Require(restored.Names!.SequenceEqual(value.Names), "String array changed.");
    }

    static void Unions()
    {
        IEvent first = new CreatedEvent { Id = 7, Name = "created" };
        var restoredFirst = MemoryPackSerializer.Deserialize<IEvent>(
            MemoryPackSerializer.Serialize(first)
        );
        Require(
            restoredFirst is CreatedEvent { Id: 7, Name: "created" },
            "First union case changed."
        );
        IEvent second = new DeletedEvent { Id = 8, Reason = "deleted" };
        var restoredSecond = MemoryPackSerializer.Deserialize<IEvent>(
            MemoryPackSerializer.Serialize(second)
        );
        Require(
            restoredSecond is DeletedEvent { Id: 8, Reason: "deleted" },
            "Extended union tag changed."
        );
    }

    static void VersionTolerance()
    {
        var oldValue = new ModelV1 { Id = 99 };
        var upgraded = MemoryPackSerializer.Deserialize<ModelV2>(
            MemoryPackSerializer.Serialize(oldValue)
        );
        Require(upgraded is { Id: 99, Name: null }, "Missing member did not use its default.");
        var newValue = new ModelV2 { Id = 100, Name = "new member" };
        var downgraded = MemoryPackSerializer.Deserialize<ModelV1>(
            MemoryPackSerializer.Serialize(newValue)
        );
        Require(downgraded is { Id: 100 }, "Unknown member was not skipped.");
    }

    static void BufferWriter()
    {
        var item = CreateItem();
        var buffer = new ArrayBufferWriter<byte>();
        MemoryPackSerializer.Serialize(buffer, item);
        Require(
            buffer.WrittenSpan.SequenceEqual(MemoryPackSerializer.Serialize(item)),
            "Buffer writer produced different bytes."
        );
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
        Require(
            MemoryPackSerializer.Deserialize<Item>(MemoryPackSerializer.Serialize<Item>(null))
                is null,
            "Null object changed."
        );
        Require(
            MemoryPackSerializer.Deserialize<IEvent>(MemoryPackSerializer.Serialize<IEvent>(null))
                is null,
            "Null union changed."
        );
        Require(
            MemoryPackSerializer.Deserialize<string>(MemoryPackSerializer.Serialize<string>(null))
                is null,
            "Null string changed."
        );
        Require(
            MemoryPackSerializer.Deserialize<string>(MemoryPackSerializer.Serialize("")) == "",
            "Empty string changed."
        );
        Require(
            MemoryPackSerializer.Deserialize<int[]>(MemoryPackSerializer.Serialize<int[]>(null))
                is null,
            "Null array changed."
        );
        Require(
            MemoryPackSerializer.Deserialize<int[]>(
                MemoryPackSerializer.Serialize(Array.Empty<int>())
            )
                is { Length: 0 },
            "Empty array changed."
        );
        var empty = MemoryPackSerializer.Deserialize<CollectionModel>(
            MemoryPackSerializer.Serialize(
                new CollectionModel
                {
                    Items = [],
                    Lookup = [],
                    Names = [],
                }
            )
        );
        Require(
            empty is { Items.Count: 0, Lookup.Count: 0, Names.Length: 0 },
            "Empty collections changed."
        );
        var absent = MemoryPackSerializer.Deserialize<CollectionModel>(
            MemoryPackSerializer.Serialize(new CollectionModel())
        );
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

    static Item CreateItem() =>
        new()
        {
            Id = 42,
            Name = "MemoryPack 原生 🧪",
            Position = new Point(1, 2, 3),
        };

    static void AssertItem(Item expected, Item? actual)
    {
        Require(actual is not null, "Object became null.");
        Require(
            actual!.Id == expected.Id
                && actual.Name == expected.Name
                && actual.Position == expected.Position,
            "Object members changed."
        );
    }

    static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
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
