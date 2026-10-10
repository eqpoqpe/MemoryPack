#if NET11_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using MemoryPack.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MemoryPack.Tests;

public class CSharp15UnionTest
{
    [Fact]
    public void PrimitiveCasesRoundTripWithExplicitTags()
    {
        Scalar number = new(42);
        Scalar text = new("hello");
        var numberBytes = MemoryPackSerializer.Serialize(number);
        numberBytes[0].Should().Be(7);
        MemoryPackSerializer.Deserialize<Scalar>(numberBytes).Value.Should().Be(42);
        MemoryPackSerializer.Deserialize<Scalar>(MemoryPackSerializer.Serialize(text)).Value.Should().Be("hello");
    }

    [Fact]
    public void DefaultAndNullableUnionRemainDistinct()
    {
        var bytes = MemoryPackSerializer.Serialize(default(Scalar));
        bytes.Should().Equal(new byte[] { 255 });
        MemoryPackSerializer.Deserialize<Scalar>(bytes).Value.Should().BeNull();
        Scalar? absent = null;
        Scalar? empty = default(Scalar);
        var absentBytes = MemoryPackSerializer.Serialize(absent);
        var emptyBytes = MemoryPackSerializer.Serialize(empty);
        absentBytes.Should().NotEqual(emptyBytes);
        MemoryPackSerializer.Deserialize<Scalar?>(absentBytes).HasValue.Should().BeFalse();
        MemoryPackSerializer.Deserialize<Scalar?>(emptyBytes).HasValue.Should().BeTrue();
        var fromNullCase = new Scalar((string)null!);
        MemoryPackSerializer.Serialize(fromNullCase).Should().Equal(bytes);
    }

    [Fact]
    public void UnknownAndTruncatedPayloadsThrow()
    {
        Action unknown = () => MemoryPackSerializer.Deserialize<Scalar>(new byte[] { 99 });
        Action truncated = () => MemoryPackSerializer.Deserialize<Scalar>(new byte[] { 7 });
        unknown.Should().Throw<MemoryPackSerializationException>();
        truncated.Should().Throw<MemoryPackSerializationException>();
    }

    [Fact]
    public void CasesAreIndependentOfDeclarationOrder()
    {
        var bytes = MemoryPackSerializer.Serialize(new Scalar(123));
        MemoryPackSerializer.Deserialize<ReorderedScalar>(bytes).Value.Should().Be(123);
        MemoryPackSerializer.Serialize(new ReorderedScalar("text")).Should().Equal(MemoryPackSerializer.Serialize(new Scalar("text")));
    }

    [Fact]
    public void GenericCasesAndArraysRoundTrip()
    {
        var value = new GenericResult<int>(new UnionSuccess<int>(123));
        var bytes = MemoryPackSerializer.Serialize(value);
        bytes[0].Should().Be(250); // Wide tag marker.
        ((UnionSuccess<int>)MemoryPackSerializer.Deserialize<GenericResult<int>>(bytes).Value!).Value.Should().Be(123);
        var array = new ArrayResult(new int[] { 1, 2, 3 });
        ((int[])MemoryPackSerializer.Deserialize<ArrayResult>(MemoryPackSerializer.Serialize(array)).Value!).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void ClosedHierarchyUsesExistingUnionFormat()
    {
        ClosedResult value = new ClosedSuccess(123);
        var bytes = MemoryPackSerializer.Serialize(value);
        bytes[0].Should().Be(0);
        MemoryPackSerializer.Deserialize<ClosedResult>(bytes).Should().Be(new ClosedSuccess(123));
        ClosedResult failure = new ClosedFailure("failure");
        MemoryPackSerializer.Deserialize<ClosedResult>(MemoryPackSerializer.Serialize(failure)).Should().Be(failure);
    }

    [Fact]
    public void ContextRegistersUnionCasesAndContainerMembers()
    {
        CSharp15Context.Register();
        var value = new UnionContainer { Value = new(new UnionSuccess<int>(321)) };
        var result = MemoryPackSerializer.Deserialize<UnionContainer>(MemoryPackSerializer.Serialize(value));
        ((UnionSuccess<int>)result!.Value.Value!).Value.Should().Be(321);
        MemoryPackFormatterProvider.IsRegistered<GenericResult<int>>().Should().BeTrue();
        MemoryPackFormatterProvider.IsRegistered<UnionSuccess<int>>().Should().BeTrue();
    }

    [Fact]
    public void InstanceContextIncludesEveryCaseDependency()
    {
        var context = new CSharp15SerializerContext();
        var value = new GenericResult<int>(new UnionSuccess<int>(321));
        var bytes = MemoryPackSerializer.Serialize(value, context);
        ((UnionSuccess<int>)MemoryPackSerializer.Deserialize<GenericResult<int>>(bytes, context).Value!).Value.Should().Be(321);
        context.GetTypeInfo<UnionSuccess<int>>().Should().NotBeNull();
        var array = new ArrayResult(new int[] { 1, 2 });
        ((int[])MemoryPackSerializer.Deserialize<ArrayResult>(MemoryPackSerializer.Serialize(array, context), context).Value!).Should().Equal(1, 2);
    }

    [Fact]
    public void NullableCaseAndNestedUnionRoundTrip()
    {
        var value = new NullableCase(42);
        MemoryPackSerializer.Deserialize<NullableCase>(MemoryPackSerializer.Serialize(value)).Value.Should().Be(42);
        MemoryPackSerializer.Deserialize<NullableCase>(MemoryPackSerializer.Serialize(new NullableCase((int?)null))).Value.Should().BeNull();
        var nested = new UnionScope<int>.Nested("nested");
        MemoryPackSerializer.Deserialize<UnionScope<int>.Nested>(MemoryPackSerializer.Serialize(nested)).Value.Should().Be("nested");
    }

    [Theory]
    [InlineData("[MemoryPackable] public partial union U(int, string);", "MEMPACK055")]
    [InlineData("[MemoryPackable, MemoryPackUnion(0, typeof(int))] public partial union U(int, string);", "MEMPACK055")]
    [InlineData("[MemoryPackable, MemoryPackUnion(0, typeof(int)), MemoryPackUnion(1, typeof(bool))] public partial union U(int, string);", "MEMPACK055")]
    [InlineData("[MemoryPackable, MemoryPackUnion(0, typeof(int)), MemoryPackUnion(0, typeof(string))] public partial union U(int, string);", "MEMPACK012")]
    [InlineData("[MemoryPackable, MemoryPackUnion(0, typeof(int)), MemoryPackUnion(1, typeof(int?))] public partial union U(int, int?);", "MEMPACK055")]
    [InlineData("[MemoryPackable, MemoryPackUnion(0, typeof(object)), MemoryPackUnion(1, typeof(string))] public partial union U(object, string);", "MEMPACK055")]
    [InlineData("[MemoryPackable(GenerateType.VersionTolerant), MemoryPackUnion(0, typeof(int)), MemoryPackUnion(1, typeof(string))] public partial union U(int, string);", "MEMPACK055")]
    public void InvalidContractsReportDiagnostics(string source, string expected)
    {
        var (_, diagnostics) = CSharpGeneratorRunner.RunGenerator(source, languageVersion: LanguageVersion.Preview);
        diagnostics.Should().Contain(x => x.Id == expected);
    }

    [Fact]
    public void GeneratedCodeCompilesForUnionAndClosedHierarchy()
    {
        const string source = """
            [MemoryPackable, MemoryPackUnion(0, typeof(int)), MemoryPackUnion(1, typeof(string))]
            public partial union U(int, string);
            [MemoryPackable, MemoryPackUnion(0, typeof(A)), MemoryPackUnion(1, typeof(B))]
            public closed partial record Base;
            [MemoryPackable] public sealed partial record A(int Value) : Base;
            [MemoryPackable] public sealed partial record B(string Value) : Base;
            [MemoryPackSerializable<U>, MemoryPackSerializable<Base>] static partial class Context;
            """;
        var (compilation, diagnostics) = CSharpGeneratorRunner.RunGenerator(source, languageVersion: LanguageVersion.Preview);
        diagnostics.Should().BeEmpty();
        compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    [Fact]
    public void ReferencedUnionMetadataSuppliesContextCaseDependencies()
    {
        const string source = """
            [MemoryPackable, MemoryPackUnion(0, typeof(Ok<>)), MemoryPackUnion(1, typeof(Error))]
            public partial union Result<T>(Ok<T>, Error);
            [MemoryPackable] public sealed partial record Ok<T>(T Value);
            [MemoryPackable] public sealed partial record Error(string Message);
            """;
        var (compilation, diagnostics) = CSharpGeneratorRunner.RunGenerator(source, languageVersion: LanguageVersion.Preview);
        diagnostics.Should().BeEmpty();
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        emit.Success.Should().BeTrue(string.Join("\n", emit.Diagnostics));
        var options = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: new[] { "NET7_0_OR_GREATER" });
        var consumer = compilation.RemoveAllSyntaxTrees().WithAssemblyName("consumer")
            .AddReferences(MetadataReference.CreateFromImage(stream.ToArray()))
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText("using MemoryPack; [MemoryPackSerializable<Result<int>>] partial class Context : MemoryPackSerializerContext;", options));
        var driver = CSharpGeneratorDriver.Create(new MemoryPackGenerator()).WithUpdatedParseOptions(options);
        driver.RunGeneratorsAndUpdateCompilation(consumer, out var generated, out var errors);
        errors.Should().BeEmpty();
        generated.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        var output = string.Join("\n", generated.SyntaxTrees.Select(x => x.ToString()));
        output.Should().Contain("global::Ok<int>");
        output.Should().Contain("global::Error");
        output.Should().Contain("__MemoryPackCreateFormatter()");
    }

    [Fact]
    public void EditingACaseInvalidatesAnUnchangedUnionDeclaration()
    {
        var (compilation, _) = CSharpGeneratorRunner.RunGenerator("", languageVersion: LanguageVersion.Preview);
        var options = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: new[] { "NET7_0_OR_GREATER" });
        var root = CSharpSyntaxTree.ParseText("using MemoryPack; [MemoryPackable, MemoryPackUnion(0, typeof(Case)), MemoryPackUnion(1, typeof(int))] public partial union Root(Case, int);", options);
        var originalCase = CSharpSyntaxTree.ParseText("using MemoryPack; [MemoryPackable] public sealed partial record Case(string Value);", options);
        var original = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(root, originalCase);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new MemoryPackGenerator()).WithUpdatedParseOptions(options);
        driver = driver.RunGeneratorsAndUpdateCompilation(original, out _, out var firstDiagnostics);
        firstDiagnostics.Should().BeEmpty();
        var changedCase = CSharpSyntaxTree.ParseText("public sealed partial record Case(string Value);", options);
        driver.RunGeneratorsAndUpdateCompilation(original.ReplaceSyntaxTree(originalCase, changedCase), out _, out var diagnostics);
        diagnostics.Should().Contain(x => x.Id == "MEMPACK055");
    }
}

[MemoryPackable, MemoryPackUnion(7, typeof(int)), MemoryPackUnion(300, typeof(string))]
public partial union Scalar(int, string);

[MemoryPackable, MemoryPackUnion(300, typeof(string)), MemoryPackUnion(7, typeof(int))]
public partial union ReorderedScalar(string, int);

[MemoryPackable, MemoryPackUnion(300, typeof(UnionSuccess<>)), MemoryPackUnion(0, typeof(UnionFailure))]
public partial union GenericResult<T>(UnionSuccess<T>, UnionFailure);

[MemoryPackable]
public sealed partial record UnionSuccess<T>(T Value);

[MemoryPackable]
public sealed partial record UnionFailure(string Message);

[MemoryPackable, MemoryPackUnion(0, typeof(int[])), MemoryPackUnion(1, typeof(string))]
public partial union ArrayResult(int[], string);

[MemoryPackable, MemoryPackUnion(0, typeof(ClosedSuccess)), MemoryPackUnion(1, typeof(ClosedFailure))]
public closed partial record ClosedResult;

[MemoryPackable]
public sealed partial record ClosedSuccess(int Value) : ClosedResult;

[MemoryPackable]
public sealed partial record ClosedFailure(string Message) : ClosedResult;

[MemoryPackable]
public partial class UnionContainer { public GenericResult<int> Value { get; set; } }

[MemoryPackSerializable<UnionContainer>, MemoryPackSerializable<Scalar>, MemoryPackSerializable<ClosedResult>]
internal static partial class CSharp15Context;

[MemoryPackSerializable<GenericResult<int>>, MemoryPackSerializable<ArrayResult>, MemoryPackSerializable<ClosedResult>]
internal partial class CSharp15SerializerContext : MemoryPackSerializerContext;

[MemoryPackable, MemoryPackUnion(0, typeof(int?)), MemoryPackUnion(1, typeof(string))]
public partial union NullableCase(int?, string);

public partial class UnionScope<T>
{
    [MemoryPackable, MemoryPackUnion(0, typeof(int)), MemoryPackUnion(1, typeof(string))]
    public partial union Nested(int, string);
}
#endif
