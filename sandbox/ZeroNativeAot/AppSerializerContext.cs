using MemoryPack;

namespace ZeroNativeAot;

[MemoryPackSerializable<Item>]
[MemoryPackSerializable<FixedSizeModel>]
[MemoryPackSerializable<Box<int>>]
[MemoryPackSerializable<Box<string>>]
[MemoryPackSerializable<Box<Item>>]
[MemoryPackSerializable<CollectionModel>]
[MemoryPackSerializable<IEvent>]
[MemoryPackSerializable<ModelV1>]
[MemoryPackSerializable<ModelV2>]
[MemoryPackSerializable<RecursiveNode>]
[MemoryPackSerializable<CircularNode>]
[MemoryPackSerializable<NamedValue?>]
[MemoryPackSerializable<CustomModel>]
[MemoryPackSerializable<IReply<int>>]
[MemoryPackSerializable<List<Box<Item>>>]
[MemoryPackSerializable<FixedSizeModel[]>]
[MemoryPackSerializable<PriorityQueue<Item, int>>]
[MemoryPackSerializable<ILookup<string, Item>>]
[MemoryPackSerializable<IReadOnlyList<Item>>]
[MemoryPackSerializable<NamedTupleModel>]
[MemoryPackSerializable(typeof((string Name, int Id)), TypeInfoPropertyName = "NameAndId")]
internal partial class AppSerializerContext : MemoryPackSerializerContext;
