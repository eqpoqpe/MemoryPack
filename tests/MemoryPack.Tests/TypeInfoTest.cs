using MemoryPack.Formatters;
using Microsoft.CodeAnalysis;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MemoryPack.Tests;

public class TypeInfoTest
{
    [Fact]
    public void TypedAndUntypedMetadataAreStableAndOwned()
    {
        var context = new MetadataTestContext();
        context.TypeInfoModel.Should().BeSameAs(context.GetTypeInfo<TypeInfoModel>());
        context.GetTypeInfo(typeof(TypeInfoModel)).Should().BeSameAs(context.TypeInfoModel);
        context.TypeInfoModel.Context.Should().BeSameAs(context);
        context.TypeInfoModel.Type.Should().Be(typeof(TypeInfoModel));
        context.GetTypeInfo(typeof(ExternalContextValue)).Should().BeNull();
        Action missing = () => context.GetTypeInfo<ExternalContextValue>();
        missing.Should().Throw<MemoryPackSerializationException>().WithMessage("*MemoryPackSerializable*");
        context.GetTypeInfo<List<TypeInfoModel>>().Should().NotBeNull();
        context.GetTypeInfo<string>().Should().NotBeNull();
    }

    [Fact]
    public void ManualMetadataHonorsUnmanagedRootFormatter()
    {
        var info = new MemoryPackTypeInfo<int>(new OffsetIntFormatter());
        var bytes = MemoryPackSerializer.Serialize(7, info);
        bytes.Should().Equal(BitConverter.GetBytes(1007));
        MemoryPackSerializer.Deserialize(bytes, info).Should().Be(7);
        MemoryPackTypeInfo untyped = info;
        MemoryPackSerializer.Serialize(untyped, 7).Should().Equal(bytes);
        MemoryPackSerializer.Deserialize(untyped, bytes).Should().Be(7);
        MemoryPackSerializer.Serialize(7).Should().Equal(BitConverter.GetBytes(7));
    }

    [Fact]
    public void IndependentContextsAndNestedProviderDispatch()
    {
        var first = new FirstExternalContext();
        var second = new SecondExternalContext();
        var model = new TypeInfoEnvelope { Items = [new("value")] };
        var firstBytes = MemoryPackSerializer.Serialize(model, first);
        var secondBytes = MemoryPackSerializer.Serialize(model, second.TypeInfoEnvelope);
        firstBytes.Should().NotEqual(secondBytes);
        MemoryPackSerializer.Deserialize<TypeInfoEnvelope>(firstBytes, first)!.Items![0].Text.Should().Be("value");
        MemoryPackSerializer.Deserialize(secondBytes, second.TypeInfoEnvelope)!.Items![0].Text.Should().Be("value");
        first.GetTypeInfo<ExternalContextValue>().Formatter.Should().NotBeSameAs(second.GetTypeInfo<ExternalContextValue>().Formatter);
        MemoryPackFormatterProvider.IsRegistered<ContextOnlyValue>().Should().BeFalse();
        Parallel.For(0, 100, i =>
        {
            var context = i % 2 == 0 ? (MemoryPackSerializerContext)first : second;
            var bytes = MemoryPackSerializer.Serialize(model, context);
            MemoryPackSerializer.Deserialize<TypeInfoEnvelope>(bytes, context)!.Items![0].Text.Should().Be("value");
        });
    }

    [Fact]
    public void MissingContextDependencyDoesNotFallBackToGlobalProvider()
    {
        MemoryPackFormatterProvider.Register(new ExternalContextFormatter());
        var incomplete = new MissingDependencyContext();
        var value = new TypeInfoEnvelope { Items = [new("missing")] };
        Action write = () => MemoryPackSerializer.Serialize(value, incomplete);
        write.Should().Throw<MemoryPackSerializationException>().WithMessage("*ExternalContextValue*MemoryPackSerializable*");
        var manual = new MemoryPackTypeInfo<TypeInfoEnvelope>(new MemoryPackableFormatter<TypeInfoEnvelope>());
        MemoryPackSerializer.Deserialize(MemoryPackSerializer.Serialize(value, manual), manual)!.Items![0].Text.Should().Be("missing");
    }

    [Fact]
    public void ContextOptionsAndPerCallOptionsAreIsolated()
    {
        var utf8 = new MetadataTestContext(MemoryPackSerializerOptions.Utf8);
        var utf16 = new MetadataTestContext(MemoryPackSerializerOptions.Utf16);
        var value = new TypeInfoModel { Text = "内容", Id = 3 };
        var first = MemoryPackSerializer.Serialize(value, utf8);
        var second = MemoryPackSerializer.Serialize(value, utf16);
        first.Should().NotEqual(second);
        MemoryPackSerializer.Serialize(value, utf8.TypeInfoModel, MemoryPackSerializerOptions.Utf16).Should().Equal(second);
        utf8.Options.Should().BeSameAs(MemoryPackSerializerOptions.Utf8);
        MemoryPackSerializer.Serialize(value, utf8).Should().Equal(first);
        MemoryPackSerializer.Deserialize<TypeInfoModel>(second, utf16)!.Text.Should().Be(value.Text);
        MemoryPackSerializer.Serialize(value).Should().Equal(first);
    }

    [Fact]
    public void SpanSequenceOverwriteAndBufferWriterMatchLegacyBytes()
    {
        var context = MetadataTestContext.Default;
        var value = new TypeInfoModel { Id = 42, Text = "model" };
        var bytes = MemoryPackSerializer.Serialize(value, context.TypeInfoModel);
        bytes.Should().Equal(MemoryPackSerializer.Serialize(value));
        var writer = new ArrayBufferWriter<byte>();
        MemoryPackSerializer.Serialize(writer, value, context);
        writer.WrittenSpan.ToArray().Should().Equal(bytes);
        var sequence = ReadOnlySequenceBuilder.Create(bytes[..2], bytes[2..]);
        MemoryPackSerializer.Deserialize(sequence, context.TypeInfoModel)!.Id.Should().Be(42);
        TypeInfoModel? target = new() { Id = -1 };
        var original = target;
        MemoryPackSerializer.Deserialize(sequence, ref target, context).Should().Be(bytes.Length);
        target.Should().BeSameAs(original);
        target!.Text.Should().Be("model");
        object? untyped = new TypeInfoModel();
        MemoryPackSerializer.Deserialize((MemoryPackTypeInfo)context.TypeInfoModel, bytes, ref untyped).Should().Be(bytes.Length);
        ((TypeInfoModel)untyped!).Id.Should().Be(42);
        MemoryPackSerializer.Serialize(typeof(TypeInfoModel), value, context).Should().Equal(bytes);
        ((TypeInfoModel)MemoryPackSerializer.Deserialize(typeof(TypeInfoModel), sequence, context)!).Id.Should().Be(42);
        MemoryPackSerializer.Deserialize<TypeInfoModel>(new byte[] { 255 }, new MetadataTestContext()).Should().BeNull();
    }

    [Fact]
    public async Task AsyncStreamsCancellationAndStateRecovery()
    {
        var context = MetadataTestContext.Default;
        var value = new TypeInfoModel { Id = 4, Text = new string('x', 70000) };
        using var stream = new MemoryStream();
        await MemoryPackSerializer.SerializeAsync(stream, value, context);
        stream.Position = 0;
        (await MemoryPackSerializer.DeserializeAsync<TypeInfoModel>(stream, context))!.Text.Should().Be(value.Text);
        using var network = new NonMemoryStream(stream.ToArray());
        (await MemoryPackSerializer.DeserializeAsync(network, context.TypeInfoModel))!.Text.Should().Be(value.Text);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Func<Task> cancelWrite = async () => await MemoryPackSerializer.SerializeAsync(stream, value, context, canceled.Token);
        await cancelWrite.Should().ThrowAsync<OperationCanceledException>();
        Func<Task> cancelRead = async () => await MemoryPackSerializer.DeserializeAsync<TypeInfoModel>(stream, context, canceled.Token);
        await cancelRead.Should().ThrowAsync<OperationCanceledException>();
        using var healthy = new MemoryStream();
        await MemoryPackSerializer.SerializeAsync((MemoryPackTypeInfo)context.TypeInfoModel, healthy, value);
        healthy.Position = 0;
        ((TypeInfoModel)(await MemoryPackSerializer.DeserializeAsync((MemoryPackTypeInfo)context.TypeInfoModel, healthy))!).Id.Should().Be(4);
        Action failure = () => MemoryPackSerializer.Deserialize<TypeInfoModel>(new byte[] { 2 }, context);
        failure.Should().Throw<MemoryPackSerializationException>();
        MemoryPackSerializer.Deserialize(MemoryPackSerializer.Serialize(value, context), context.TypeInfoModel)!.Id.Should().Be(4);
    }

    [Fact]
    public async Task AsyncIoFailuresReleaseOperationState()
    {
        var context = MetadataTestContext.Default;
        var value = new TypeInfoModel { Id = 10, Text = "failure" };
        using var failingReader = new FaultingStream(read: true);
        Func<Task> read = async () => await MemoryPackSerializer.DeserializeAsync<TypeInfoModel>(failingReader, context);
        await read.Should().ThrowAsync<IOException>();
        using var failingWriter = new FaultingStream(read: false);
        Func<Task> write = async () => await MemoryPackSerializer.SerializeAsync(failingWriter, value, context);
        await write.Should().ThrowAsync<IOException>();
        using var recovered = new MemoryStream();
        await MemoryPackSerializer.SerializeAsync(recovered, value, context);
        recovered.Position = 0;
        (await MemoryPackSerializer.DeserializeAsync<TypeInfoModel>(recovered, context))!.Id.Should().Be(10);
        MemoryPackSerializer.Deserialize<TypeInfoModel>(MemoryPackSerializer.Serialize(value))!.Id.Should().Be(10);
    }

    [Fact]
    public void ComputedCollectionDependenciesAreIncluded()
    {
        var context = MetadataTestContext.Default;
        var queue = new PriorityQueue<TypeInfoModel, int>();
        queue.Enqueue(new() { Id = 9, Text = "queue" }, 2);
        MemoryPackSerializer.Deserialize<PriorityQueue<TypeInfoModel, int>>(MemoryPackSerializer.Serialize(queue, context), context)!.Dequeue().Id.Should().Be(9);
        ILookup<string, TypeInfoModel> lookup = new[] { new TypeInfoModel { Id = 5, Text = "key" } }.ToLookup(x => x.Text!);
        MemoryPackSerializer.Deserialize<ILookup<string, TypeInfoModel>>(MemoryPackSerializer.Serialize(lookup, context), context)!["key"].Single().Id.Should().Be(5);
        IReadOnlyList<TypeInfoModel> list = new[] { new TypeInfoModel { Id = 8 } };
        MemoryPackSerializer.Deserialize<IReadOnlyList<TypeInfoModel>>(MemoryPackSerializer.Serialize(list, context), context)![0].Id.Should().Be(8);
        context.GetTypeInfo<(TypeInfoModel, int)>().Should().NotBeNull();
        context.GetTypeInfo<IGrouping<string, TypeInfoModel>>().Should().NotBeNull();
    }

    [Fact]
    public void TupleNamesAndEquivalentValueTupleFormsShareMetadata()
    {
        var context = TupleMetadataContext.Default;
        var value = new TupleMetadataModel { Left = ("left", 1), Right = ("right", 2), Values = [("list", 3)] };
        var restored = MemoryPackSerializer.Deserialize<TupleMetadataModel>(MemoryPackSerializer.Serialize(value, context), context)!;
        restored.Left.Should().Be(value.Left);
        restored.Right.Should().Be(value.Right);
        restored.Values![0].Should().Be(value.Values![0]);
        context.GetTypeInfo<(string First, int Number)>().Should().BeSameAs(context.GetTypeInfo<ValueTuple<string, int>>());
        context.GetTypeInfo<List<(string Label, int Code)>>().Should().BeSameAs(context.GetTypeInfo<List<ValueTuple<string, int>>>());
        var named = MemoryPackSerializer.Deserialize(MemoryPackSerializer.Serialize(("root", 4), context.ValueTupleStringInt32), context.ValueTupleStringInt32);
        named.First.Should().Be("root");
        named.Number.Should().Be(4);
    }

    [Fact]
    public void ConflictingClrEquivalentTupleRootsAreDiagnosed()
    {
        const string source = """
            [MemoryPackSerializable(typeof((string First, int Number)), TypeInfoPropertyName="First")]
            [MemoryPackSerializable(typeof(ValueTuple<string,int>), TypeInfoPropertyName="Second")]
            partial class Context : MemoryPackSerializerContext { }
            """;
        var (_, diagnostics) = CSharpGeneratorRunner.RunGenerator(source);
        diagnostics.Should().ContainSingle(x => x.Id == "MEMPACK053");
    }

    [Fact]
    public void NullAndMismatchedInputsAreActionable()
    {
        Action nullInfo = () => new MemoryPackTypeInfo<int>(null!);
        nullInfo.Should().Throw<ArgumentNullException>();
        Action nullContext = () => MemoryPackSerializer.Serialize(1, (MemoryPackSerializerContext)null!);
        nullContext.Should().Throw<ArgumentNullException>();
        Action missingInfo = () => MemoryPackSerializer.Serialize(1, (MemoryPackTypeInfo<int>)null!);
        missingInfo.Should().Throw<ArgumentNullException>();
        Action mismatch = () => MemoryPackSerializer.Serialize((MemoryPackTypeInfo)MetadataTestContext.Default.TypeInfoModel, "wrong");
        mismatch.Should().Throw<MemoryPackSerializationException>().WithMessage("*incompatible*");
        Action incorrectOwner = () => new IncorrectContext().GetTypeInfo<int>();
        incorrectOwner.Should().Throw<MemoryPackSerializationException>().WithMessage("*owned metadata*");
    }

    [Fact]
    public void RefCallbacksMatchLegacyPayloadOrderingAndReplacement()
    {
        RefCallbackModel? legacy = new() { Id = 1 };
        RefCallbackModel.Events.Clear();
        var expected = MemoryPackSerializer.Serialize(legacy);
        var expectedEvents = RefCallbackModel.Events.ToArray();
        legacy!.Id.Should().Be(12);
        RefCallbackModel? contextual = new() { Id = 1 };
        RefCallbackModel.Events.Clear();
        MemoryPackSerializer.Serialize(contextual, CallbackMetadataContext.Default.RefCallbackModel).Should().Equal(expected);
        RefCallbackModel.Events.Should().Equal(expectedEvents);
        contextual!.Id.Should().Be(legacy.Id);
        RefCallbackModel.Events.Clear();
        var oldResult = MemoryPackSerializer.Deserialize<RefCallbackModel>(expected);
        var readEvents = RefCallbackModel.Events.ToArray();
        RefCallbackModel.Events.Clear();
        var newResult = MemoryPackSerializer.Deserialize(expected, CallbackMetadataContext.Default.RefCallbackModel);
        newResult!.Id.Should().Be(oldResult!.Id);
        RefCallbackModel.Events.Should().Equal(readEvents);
    }

    [Theory]
    [InlineData("[MemoryPackSerializable<int>(TypeInfoPropertyName=\"Default\")] partial class C : MemoryPackSerializerContext {}", "MEMPACK053")]
    [InlineData("[MemoryPackSerializable<int>(TypeInfoPropertyName=\"invalid name\")] partial class C : MemoryPackSerializerContext {}", "MEMPACK053")]
    [InlineData("[MemoryPackSerializable<int>] partial class C : MemoryPackSerializerContext { public C() {} }", "MEMPACK050")]
    [InlineData("[MemoryPackSerializable<int>] partial class C : MemoryPackSerializerContext { public int Int32 { get; } }", "MEMPACK053")]
    public void InvalidInstanceDeclarationsAreDiagnosed(string source, string expected)
    {
        var (_, diagnostics) = CSharpGeneratorRunner.RunGenerator(source);
        diagnostics.Should().ContainSingle(x => x.Id == expected);
    }

    [Fact]
    public void NamingCollisionCanBeResolvedAndOutputIsLocal()
    {
        const string source = """
            [MemoryPackSerializable<A.Model>(TypeInfoPropertyName="FirstModel")]
            [MemoryPackSerializable(typeof(B.Model), TypeInfoPropertyName="SecondModel")]
            partial class Context : MemoryPackSerializerContext { }
            namespace A { [MemoryPackable] public partial class Model { } }
            namespace B { [MemoryPackable] public partial class Model { } }
            """;
        var (compilation, diagnostics) = CSharpGeneratorRunner.RunGenerator(source);
        diagnostics.Should().BeEmpty();
        compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        var output = string.Join("\n", compilation.SyntaxTrees.Where(x => x.FilePath.EndsWith("MemoryPackContext.g.cs")));
        output.Should().Contain("FirstModel").And.Contain("SecondModel");
        output.Should().NotContain("MemoryPackFormatterProvider");
        var (_, collision) = CSharpGeneratorRunner.RunGenerator(source.Replace(", TypeInfoPropertyName=\"SecondModel\"", "").Replace("(TypeInfoPropertyName=\"FirstModel\")", ""));
        collision.Should().ContainSingle(x => x.Id == "MEMPACK053");
    }

    [Fact]
    public void MixedInstanceAttributesAcrossPartialDeclarationsAreDeduplicated()
    {
        const string source = """
            [MemoryPackSerializable<Model>] partial class Context : MemoryPackSerializerContext { }
            [MemoryPackSerializable(typeof(List<Model>))] partial class Context { }
            [MemoryPackSerializable<Model>] partial class Context { }
            [MemoryPackable] public partial class Model { public Model Next { get; set; } }
            """;
        var (compilation, diagnostics) = CSharpGeneratorRunner.RunGenerator(source);
        diagnostics.Should().BeEmpty();
        compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        compilation.SyntaxTrees.Count(x => x.FilePath.EndsWith("MemoryPackContext.g.cs")).Should().Be(1);
    }

    [Fact]
    public void RegisterOnlyNoGenerateTypeCannotUseInstanceMetadata()
    {
        const string source = """
            [MemoryPackSerializable<Model>] partial class Context : MemoryPackSerializerContext { }
            [MemoryPackable(GenerateType.NoGenerate)] public partial class Model : IMemoryPackFormatterRegister {
                static void IMemoryPackFormatterRegister.RegisterFormatter() { }
            }
            """;
        var (_, diagnostics) = CSharpGeneratorRunner.RunGenerator(source);
        diagnostics.Should().ContainSingle(x => x.Id == "MEMPACK051" && x.GetMessage().Contains("FormatterType"));
    }

    [Fact]
    public void GeneratedUnionFactoryNameIsReserved()
    {
        const string source = """
            [MemoryPackable] [MemoryPackUnion(0, typeof(Case))] public partial interface IUnion {
                public static MemoryPackFormatter<IUnion> __MemoryPackCreateFormatter() => null;
            }
            [MemoryPackable] public partial class Case : IUnion { }
            """;
        var (_, diagnostics) = CSharpGeneratorRunner.RunGenerator(source);
        diagnostics.Should().ContainSingle(x => x.Id == "MEMPACK054");
    }

    [Fact]
    public void OversizedOriginalRootsAreRejectedBeforeCanonicalization()
    {
        var type = "int";
        for (var i = 0; i < 10; i++) type = $"System.ValueTuple<{type}, {type}>";
        var source = $"[MemoryPackSerializable(typeof({type}))] partial class Context : MemoryPackSerializerContext {{ }}";
        var (_, diagnostics) = CSharpGeneratorRunner.RunGenerator(source);
        diagnostics.Should().ContainSingle(x => x.Id == "MEMPACK051" && x.GetMessage().Contains("512 structural nodes"));
        diagnostics[0].GetMessage().Length.Should().BeLessThan(512);
    }

    [Fact]
    public void DynamicAndObjectGenericArgumentsHaveOneClrIdentity()
    {
        const string source = """
            [MemoryPackSerializable(typeof(List<dynamic>), FormatterType=typeof(MemoryPack.Formatters.ListFormatter<object>))]
            [MemoryPackSerializable(typeof(List<object>), FormatterType=typeof(MemoryPack.Formatters.ListFormatter<object>))]
            partial class Context : MemoryPackSerializerContext { }
            """;
        var (compilation, diagnostics) = CSharpGeneratorRunner.RunGenerator(source);
        diagnostics.Should().BeEmpty();
        compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        var output = string.Join("\n", compilation.SyntaxTrees.Where(x => x.FilePath.EndsWith("MemoryPackContext.g.cs")));
        output.Split("new global::MemoryPack.MemoryPackTypeInfo<").Length.Should().Be(2);
    }

    [Fact]
    public void ClrEquivalentGenericFormatterDeclarationsDoNotConflict()
    {
        const string source = """
            [MemoryPackSerializable(typeof((string First, int Number)), FormatterType=typeof(Formatter<(string Name,int Id)>))]
            [MemoryPackSerializable(typeof(ValueTuple<string,int>), FormatterType=typeof(Formatter<ValueTuple<string,int>>))]
            partial class Context : MemoryPackSerializerContext { }
            public class Formatter<T> : MemoryPackFormatter<T> {
                public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref T value) { }
                public override void Deserialize(ref MemoryPackReader reader, scoped ref T value) { value = default; }
            }
            """;
        var (compilation, diagnostics) = CSharpGeneratorRunner.RunGenerator(source);
        diagnostics.Should().BeEmpty();
        compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    sealed class FaultingStream(bool read) : Stream
    {
        public override bool CanRead => read;
        public override bool CanSeek => false;
        public override bool CanWrite => !read;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("read failure");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(new IOException("read failure"));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException(new IOException("write failure"));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("write failure");
    }

    sealed class IncorrectContext : MemoryPackSerializerContext
    {
        public override MemoryPackTypeInfo? GetTypeInfo(Type type) => new MemoryPackTypeInfo<int>(new UnmanagedFormatter<int>());
    }

    sealed class NonMemoryStream(byte[] data) : Stream
    {
        readonly MemoryStream inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer[..Math.Min(buffer.Length, 1024)], cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}

[MemoryPackSerializable<TypeInfoModel>]
[MemoryPackSerializable<List<TypeInfoModel>>]
[MemoryPackSerializable<PriorityQueue<TypeInfoModel, int>>]
[MemoryPackSerializable<ILookup<string, TypeInfoModel>>]
[MemoryPackSerializable<IReadOnlyList<TypeInfoModel>>]
public partial class MetadataTestContext : MemoryPackSerializerContext;

[MemoryPackSerializable<RefCallbackModel>]
public partial class CallbackMetadataContext : MemoryPackSerializerContext;

[MemoryPackSerializable<TupleMetadataModel>]
[MemoryPackSerializable(typeof((string First, int Number)))]
[MemoryPackSerializable<ValueTuple<string, int>>]
public partial class TupleMetadataContext : MemoryPackSerializerContext;

[MemoryPackSerializable<TypeInfoEnvelope>]
[MemoryPackSerializable<ExternalContextValue>(FormatterType = typeof(FirstExternalFormatter))]
[MemoryPackSerializable<ContextOnlyValue>(FormatterType = typeof(ContextOnlyFormatter))]
public partial class FirstExternalContext : MemoryPackSerializerContext;

[MemoryPackSerializable<TypeInfoEnvelope>]
[MemoryPackSerializable<ExternalContextValue>(FormatterType = typeof(SecondExternalFormatter))]
public partial class SecondExternalContext : MemoryPackSerializerContext;

[MemoryPackSerializable<TypeInfoEnvelope>]
[MemoryPackSerializable<List<ExternalContextValue>>(FormatterType = typeof(ListFormatter<ExternalContextValue>))]
public partial class MissingDependencyContext : MemoryPackSerializerContext;

[MemoryPackable]
public partial class TypeInfoModel { public int Id { get; set; } public string? Text { get; set; } }

[MemoryPackable]
public partial class TupleMetadataModel
{
    public (string First, int Number) Left { get; set; }
    public (string Text, int Id) Right { get; set; }
    public List<(string Label, int Code)>? Values { get; set; }
    public ValueTuple<string, int> Plain { get; set; }
}

[MemoryPackable]
public partial class RefCallbackModel
{
    public static readonly List<string> Events = new();
    public int Id { get; set; }
    [MemoryPackOnSerializing]
    static void BeforeWrite<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, ref RefCallbackModel? value) where TBufferWriter : IBufferWriter<byte>
    { Events.Add("before write"); value = new() { Id = value!.Id + 1 }; }
    [MemoryPackOnSerialized]
    static void AfterWrite<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, ref RefCallbackModel? value) where TBufferWriter : IBufferWriter<byte>
    { Events.Add("after write"); value = new() { Id = value!.Id + 10 }; }
    [MemoryPackOnDeserializing]
    static void BeforeRead(ref MemoryPackReader reader, ref RefCallbackModel? value)
    { Events.Add("before read"); value = new() { Id = 20 }; }
    [MemoryPackOnDeserialized]
    static void AfterRead(ref MemoryPackReader reader, ref RefCallbackModel? value)
    { Events.Add("after read"); value = new() { Id = value!.Id + 100 }; }
}

[MemoryPackable]
public partial class TypeInfoEnvelope { [MemoryPackAllowSerialize] public List<ExternalContextValue>? Items { get; set; } }

public sealed record ContextOnlyValue(string Text);
public sealed class ContextOnlyFormatter : MemoryPackFormatter<ContextOnlyValue>
{
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref ContextOnlyValue? value) => writer.WriteString(value?.Text);
    public override void Deserialize(ref MemoryPackReader reader, scoped ref ContextOnlyValue? value) => value = new(reader.ReadString()!);
}

public sealed class OffsetIntFormatter : MemoryPackFormatter<int>
{
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref int value) => writer.WriteUnmanaged(value + 1000);
    public override void Deserialize(ref MemoryPackReader reader, scoped ref int value) => value = reader.ReadUnmanaged<int>() - 1000;
}

public sealed class FirstExternalFormatter : MemoryPackFormatter<ExternalContextValue>
{
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref ExternalContextValue? value) => writer.WriteString("first:" + value?.Text);
    public override void Deserialize(ref MemoryPackReader reader, scoped ref ExternalContextValue? value) => value = new(reader.ReadString()![6..]);
}
public sealed class SecondExternalFormatter : MemoryPackFormatter<ExternalContextValue>
{
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref ExternalContextValue? value) => writer.WriteString("second:" + value?.Text);
    public override void Deserialize(ref MemoryPackReader reader, scoped ref ExternalContextValue? value) => value = new(reader.ReadString()![7..]);
}
