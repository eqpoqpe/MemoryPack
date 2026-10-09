using MemoryPack.Formatters;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MemoryPack.Tests;

public class SerializationContextTest
{
    [Fact]
    public void ConcurrentRegistrationAndRecursiveGraph()
    {
        Parallel.For(0, 32, _ => TestSerializationContext.Register());
        var value = new ContextBox<ContextNode> { Value = new ContextNode { Name = "root", Children = [new() { Name = "child" }] } };
        var restored = MemoryPackSerializer.Deserialize<ContextBox<ContextNode>>(MemoryPackSerializer.Serialize(value));
        restored!.Value!.Children![0].Name.Should().Be("child");
        MemoryPackFormatterProvider.IsRegistered<List<ContextNode>>().Should().BeTrue();
        MemoryPackFormatterProvider.IsRegistered<ContextNode>().Should().BeTrue();
    }

    [Fact]
    public void CustomFormatterIsAnOpaqueDependency()
    {
        TestSerializationContext.Register();
        var value = new ContextCustomModel { Value = new ExternalContextValue("custom") };
        MemoryPackSerializer.Deserialize<ContextCustomModel>(MemoryPackSerializer.Serialize(value))!.Value.Text.Should().Be("custom");
        MemoryPackSerializer.Deserialize<ExternalContextValue>(MemoryPackSerializer.Serialize(value.Value))!.Text.Should().Be("custom");
    }

    [Fact]
    public void TypeRegistrationRejectsUnnamedTypes()
    {
        Action register = () => TypeFormatter.RegisterType(typeof(ContextBox<>).GetGenericArguments()[0]);
        register.Should().Throw<ArgumentException>().WithParameterName("type");
    }

    [Fact]
    public void NonGenericErrorCacheDoesNotPoisonGenericRegistration()
    {
        var value = new RecoveryValue("recovered");
        Action nonGeneric = () => MemoryPackSerializer.Serialize(typeof(RecoveryValue), value);
        nonGeneric.Should().Throw<MemoryPackSerializationException>();
        Action generic = () => MemoryPackSerializer.Serialize(value);
        generic.Should().Throw<MemoryPackSerializationException>();
        MemoryPackFormatterProvider.IsRegistered<RecoveryValue>().Should().BeFalse();
        MemoryPackFormatterProvider.Register(new RecoveryFormatter());
        MemoryPackSerializer.Deserialize<RecoveryValue>(MemoryPackSerializer.Serialize(value)).Should().Be(value);
        MemoryPackSerializer.Deserialize(typeof(RecoveryValue), MemoryPackSerializer.Serialize(typeof(RecoveryValue), value)).Should().Be(value);
    }

    [Fact]
    public void ContextOutputIsDeterministicAndPartialDeclarationsAreDeduplicated()
    {
        const string source = """
            [MemoryPackSerializable(typeof(List<Model>))] static partial class Context { }
            [MemoryPackSerializable(typeof(Model))] static partial class Context { }
            [MemoryPackable] public partial class Model { public Model Next { get; set; } }
            """;
        var first = Generate(source);
        var second = Generate(source);
        first.Should().Be(second);
        first.Should().Contain("Register<global::Model>()");
        first.Should().Contain("ListFormatter<global::Model>");
    }

    [Fact]
    public void GenericAndTypeofAttributesShareOneRegistrationGraph()
    {
        const string source = """
            [MemoryPackSerializable<List<Model>>] static partial class Context { }
            [MemoryPackSerializable(typeof(Model))] static partial class Context { }
            [MemoryPackSerializable<Model>] static partial class Context { }
            [MemoryPackable] public partial class Model { public int Id { get; set; } }
            """;
        var output = Generate(source);
        output.Should().Contain("ListFormatter<global::Model>");
        output.Split("Register<global::Model>()").Length.Should().Be(2);
    }

    [Fact]
    public void NoGenerateExternalGenericUnionRegistersItsClosedGraph()
    {
        const string source = """
            [MemoryPackSerializable<IUnion<int>>] static partial class Context { }
            [MemoryPackable(GenerateType.NoGenerate)] public partial interface IUnion<T> { }
            [MemoryPackUnionFormatter(typeof(IUnion<>))]
            [MemoryPackUnion(0, typeof(UnionCase<>))]
            public partial class UnionFormatter<T> { }
            [MemoryPackable] public partial class UnionCase<T> : IUnion<T> { public T Value { get; set; } }
            """;
        var output = Generate(source);
        output.Should().Contain("new global::UnionFormatter<int>()");
        output.Should().Contain("Register<global::UnionCase<int>>()");
    }

    [Fact]
    public void CircularSerializationUsesASeparateGenericBody()
    {
        const string source = """
            [MemoryPackSerializable<Ring>] static partial class Context { }
            [MemoryPackable(GenerateType.CircularReference)] public partial class Ring {
                [MemoryPackOrder(0)] public int Id { get; set; }
                [MemoryPackOrder(1)] public Ring Next { get; set; }
            }
            """;
        var output = Generate(source, contextOnly: false);
        output.Should().Contain("__MemoryPackSerialize(ref writer, ref value)");
        output.Should().Contain("MethodImplOptions.NoInlining");
    }

    [Fact]
    public void ExpandingGenericRecursionProducesADiagnostic()
    {
        const string source = """
            [MemoryPackSerializable<Expanding<int>>] static partial class Context { }
            [MemoryPackable] public partial class Expanding<T> { public Expanding<List<T>> Next { get; set; } }
            """;
        var (_, diagnostics) = CSharpGeneratorRunner.RunGenerator(source);
        diagnostics.Should().ContainSingle(x => x.Id == "MEMPACK051" && x.GetMessage().Contains("128 levels"));
    }

    [Fact]
    public void ExponentiallyExpandingGenericArgumentsProduceABoundedDiagnostic()
    {
        const string source = """
            [MemoryPackSerializable<Expanding<int>>] static partial class Context { }
            [MemoryPackable] public partial class Expanding<T> { public Expanding<(T, T)> Next { get; set; } }
            """;
        var (_, diagnostics) = CSharpGeneratorRunner.RunGenerator(source);
        diagnostics.Should().ContainSingle(x => x.Id == "MEMPACK051" && x.GetMessage().Contains("512 structural nodes"));
        diagnostics[0].GetMessage().Length.Should().BeLessThan(512);
    }

    [Fact]
    public void BranchingExpansionStopsAfterItsFirstGraphDiagnostic()
    {
        const string source = """
            [MemoryPackSerializable<Expanding<int>>] static partial class Context { }
            [MemoryPackable] public partial class Expanding<T> {
                public Expanding<List<T>> Left { get; set; }
                public Expanding<Lazy<T>> Right { get; set; }
            }
            """;
        var (_, diagnostics) = CSharpGeneratorRunner.RunGenerator(source);
        diagnostics.Should().ContainSingle(x => x.Id == "MEMPACK051");
    }

    [Theory]
    [InlineData("[MemoryPackSerializable(typeof(int))] partial class Context {}", "MEMPACK050")]
    [InlineData("[MemoryPackSerializable(typeof(int))] static partial class Context<T> {}", "MEMPACK050")]
    [InlineData("[MemoryPackSerializable(typeof(List<>))] static partial class Context {}", "MEMPACK051")]
    [InlineData("[MemoryPackSerializable(typeof(object))] static partial class Context {}", "MEMPACK051")]
    [InlineData("[MemoryPackSerializable(null)] static partial class Context {}", "MEMPACK051")]
    [InlineData("[MemoryPackSerializable(typeof(string), FormatterType=typeof(MemoryPack.Formatters.UnmanagedFormatter<int>))] static partial class Context {}", "MEMPACK051")]
    public void InvalidContextReportsDiagnostic(string source, string expected)
    {
        var (_, diagnostics) = CSharpGeneratorRunner.RunGenerator(source);
        diagnostics.Should().ContainSingle(x => x.Id == expected);
    }

    [Fact]
    public void ExplicitFormattersOverrideBuiltinsAndAutomaticRegistrations()
    {
        const string source = """
            [MemoryPackSerializable(typeof(string), FormatterType=typeof(MemoryPack.Formatters.StringFormatter))]
            [MemoryPackSerializable(typeof(List<Model>), FormatterType=typeof(MemoryPack.Formatters.ListFormatter<Model>))]
            [MemoryPackSerializable(typeof(Container))]
            static partial class Context { }
            [MemoryPackable] public partial class Container { public List<Model> Items { get; set; } }
            [MemoryPackable] public partial class Model { public int Value { get; set; } }
            """;
        var output = Generate(source);
        output.Should().Contain("            global::MemoryPack.MemoryPackFormatterProvider.Register(new global::MemoryPack.Formatters.StringFormatter());");
        output.Should().NotContain("IsRegistered<string>()");
        output.Should().NotContain("IsRegistered<global::System.Collections.Generic.List<global::Model>>()");
    }

    [Fact]
    public void NullAndBoxedAttributeArgumentsPreserveSelectedConstructor()
    {
        const string source = """
            [MemoryPackable] public partial class Model {
                [Special((object)null, (byte)7, (short)8)] public string Value { get; set; }
            }
            public class SpecialAttribute : MemoryPackCustomFormatterAttribute<string> {
                public SpecialAttribute(object first, object second, object third) { }
                public SpecialAttribute(string first, byte second, short third) { }
                public override IMemoryPackFormatter<string> GetFormatter() => new MemoryPack.Formatters.StringFormatter();
            }
            """;
        var (_, diagnostics) = CSharpGeneratorRunner.RunGenerator(source);
        diagnostics.Should().BeEmpty();
        var output = Generate(source, contextOnly: false);
        output.Should().Contain("(object)((byte)(7))");
        output.Should().Contain("(object)((short)(8))");
        output.Should().NotContain("GetCustomAttribute");
    }

    static string Generate(string source, bool contextOnly = true)
    {
        var (compilation, diagnostics) = CSharpGeneratorRunner.RunGenerator(source);
        diagnostics.Should().BeEmpty();
        compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        return string.Join("\n", compilation.SyntaxTrees.Where(x => contextOnly ? x.FilePath.EndsWith("MemoryPackContext.g.cs") : x.FilePath.EndsWith("g.cs")).Select(x => x.ToString()));
    }
}

[MemoryPackSerializable(typeof(ContextBox<ContextNode>))]
[MemoryPackSerializable(typeof(ContextCustomModel))]
[MemoryPackSerializable(typeof(ExternalContextValue), FormatterType = typeof(ExternalContextFormatter))]
internal static partial class TestSerializationContext;

[MemoryPackable]
public partial class ContextBox<T> { public T? Value { get; set; } }

[MemoryPackable]
public partial class ContextNode
{
    public string? Name { get; set; }
    public List<ContextNode>? Children { get; set; }
}

[MemoryPackable]
public partial class ContextCustomModel
{
    [ExternalContext]
    public ExternalContextValue Value { get; set; } = new("");
}

public sealed record ExternalContextValue(string Text);

public sealed class ExternalContextAttribute : MemoryPackCustomFormatterAttribute<ExternalContextValue>
{
    public override IMemoryPackFormatter<ExternalContextValue> GetFormatter() => new ExternalContextFormatter();
}

public sealed class ExternalContextFormatter : MemoryPackFormatter<ExternalContextValue>
{
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref ExternalContextValue? value) => writer.WriteString(value?.Text);
    public override void Deserialize(ref MemoryPackReader reader, scoped ref ExternalContextValue? value) => value = new(reader.ReadString()!);
}

public sealed record RecoveryValue(string Text);

public sealed class RecoveryFormatter : MemoryPackFormatter<RecoveryValue>
{
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref RecoveryValue? value) => writer.WriteString(value?.Text);
    public override void Deserialize(ref MemoryPackReader reader, scoped ref RecoveryValue? value) => value = new(reader.ReadString()!);
}
