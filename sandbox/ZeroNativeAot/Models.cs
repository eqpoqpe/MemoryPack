using MemoryPack;

namespace ZeroNativeAot;

[MemoryPackable]
public partial record struct Point(float X, float Y, float Z);

[MemoryPackable]
public partial class Item
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public Point Position { get; set; }
}

[MemoryPackable]
public partial class FixedSizeModel
{
    public int Id { get; set; }
    public double Value { get; set; }
}

[MemoryPackable]
public partial class Box<T>
{
    public T? Value { get; set; }
}

[MemoryPackable]
public partial class CollectionModel
{
    public List<Item?>? Items { get; set; }
    public Dictionary<string, Box<Item>>? Lookup { get; set; }
    public string?[]? Names { get; set; }
}

[MemoryPackable]
[MemoryPackUnion(0, typeof(CreatedEvent))]
[MemoryPackUnion(300, typeof(DeletedEvent))]
public partial interface IEvent
{
    int Id { get; }
}

[MemoryPackable]
public partial class CreatedEvent : IEvent
{
    public int Id { get; set; }
    public string? Name { get; set; }
}

[MemoryPackable]
public partial class DeletedEvent : IEvent
{
    public int Id { get; set; }
    public string? Reason { get; set; }
}

[MemoryPackable(GenerateType.VersionTolerant)]
public partial class ModelV1
{
    [MemoryPackOrder(0)]
    public int Id { get; set; }
}

[MemoryPackable(GenerateType.VersionTolerant)]
public partial class ModelV2
{
    [MemoryPackOrder(0)]
    public int Id { get; set; }

    [MemoryPackOrder(1)]
    public string? Name { get; set; }
}
