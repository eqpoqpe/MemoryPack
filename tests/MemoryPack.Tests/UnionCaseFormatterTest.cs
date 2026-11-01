#if NET11_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using MemoryPack.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Polyester;

namespace MemoryPack.Tests;

public class UnionCaseFormatterTest
{
    [Fact]
    public void ExternalPackageCasesRoundTripThroughGeneratedContext()
    {
        var context = ExternalResultContext.Default;
        var success = new MyAppResult<string, int>(new Success<string>("ok"));
        var failure = new MyAppResult<string, int>(new Failure<int>(42));
        var successBytes = MemoryPackSerializer.Serialize(success, context);
        var failureBytes = MemoryPackSerializer.Serialize(failure, context);
        successBytes[0].Should().Be(0);
        failureBytes[0].Should().Be(1);
        MemoryPackSerializer.Deserialize<MyAppResult<string, int>>(successBytes, context).Value.Should().Be(new Success<string>("ok"));
        MemoryPackSerializer.Deserialize<MyAppResult<string, int>>(failureBytes, context).Value.Should().Be(new Failure<int>(42));
        // The wrappers are handled directly by the union, without global registration
        // or a context formatter for the external case types.
        context.GetTypeInfo(typeof(Success<string>)).Should().BeNull();
        context.GetTypeInfo(typeof(Failure<int>)).Should().BeNull();
    }

    [Fact]
    public void NullSuccessAndDefaultRemainDistinct()
    {
        var context = ExternalResultContext.Default;
        var empty = MemoryPackSerializer.Serialize(default(MyAppResult<string, int>), context);
        var success = MemoryPackSerializer.Serialize(new MyAppResult<string, int>(new Success<string>(null!)), context);
        empty.Should().Equal(new byte[] { 255 });
        success.Should().Equal(new byte[] { 0, 255, 255, 255, 255 });
        MemoryPackSerializer.Deserialize<MyAppResult<string, int>>(empty, context).Value.Should().BeNull();
        MemoryPackSerializer.Deserialize<MyAppResult<string, int>>(success, context).Value.Should().Be(new Success<string>(null!));
    }

    [Fact]
    public void PerUnionFormattersRemainScoped()
    {
        ExternalResultRegistration.Register();
        var context = ExternalResultContext.Default;
        var plain = new MyAppResult<string, int>(new Success<string>("ok"));
        var prefixed = new PrefixedResult<string, int>(new Success<string>("ok"));
        var plainBytes = MemoryPackSerializer.Serialize(plain);
        var prefixedBytes = MemoryPackSerializer.Serialize(prefixed);
        plainBytes.Should().NotEqual(prefixedBytes);
        prefixedBytes[1].Should().Be(2);
        MemoryPackSerializer.Serialize(plain, context).Should().Equal(plainBytes);
        MemoryPackSerializer.Serialize(prefixed, context).Should().Equal(prefixedBytes);
        MemoryPackSerializer.Deserialize<MyAppResult<string, int>>(plainBytes).Value.Should().Be(plain.Value);
        MemoryPackSerializer.Deserialize<PrefixedResult<string, int>>(prefixedBytes, context).Value.Should().Be(prefixed.Value);
        Action invalid = () => MemoryPackSerializer.Deserialize<PrefixedResult<string, int>>(new byte[] { 0, 3 }, context);
        invalid.Should().Throw<MemoryPackSerializationException>();
    }

    [Fact]
    public void MissingPayloadDependencyDoesNotFallBackToGlobalRegistration()
    {
        ExternalResultRegistration.Register();
        var context = MissingResultPayloadContext.Default;
        var value = new MyAppResult<string, int>(new Success<string>("requires string metadata"));
        Action serialize = () => MemoryPackSerializer.Serialize(value, context);
        serialize.Should().Throw<MemoryPackSerializationException>().WithMessage("*System.String*");
    }

    [Fact]
    public void GenericCaseFormattersBindToTheirOwnArguments()
    {
        var (compilation, diagnostics) = CSharpGeneratorRunner.RunGenerator(ResultSource("SourceResult"), languageVersion: LanguageVersion.Preview);
        diagnostics.Should().BeEmpty();
        compilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        var generated = string.Join("\n", compilation.SyntaxTrees.Where(x => x.FilePath.EndsWith("g.cs")).Select(x => x.ToString()));
        generated.Should().Contain("new global::MemoryPack.Tests.SuccessCaseFormatter<TValue>()");
        generated.Should().Contain("new global::MemoryPack.Tests.FailureCaseFormatter<TError>()");
        generated.Should().NotContain("GetCustomAttribute");
        generated.Should().NotContain("MakeGenericType");
    }

    [Theory]
    [InlineData("global::MemoryPack.Formatters.StringFormatter")]
    [InlineData("global::MemoryPack.Tests.AbstractCaseFormatter<>")]
    [InlineData("global::MemoryPack.Tests.PrivateCtorCaseFormatter<>")]
    [InlineData("global::MemoryPack.Tests.WrongArityCaseFormatter<,>")]
    [InlineData("global::System.String[]")]
    public void InvalidCaseFormattersReportDiagnostics(string formatter)
    {
        var source = ResultSource("InvalidResult").Replace("global::MemoryPack.Tests.SuccessCaseFormatter<>", formatter);
        var (_, diagnostics) = CSharpGeneratorRunner.RunGenerator(source, languageVersion: LanguageVersion.Preview);
        diagnostics.Should().ContainSingle(x => x.Id == "MEMPACK055");
        diagnostics.Should().NotContain(x => x.Id == "CS8785");
    }

    [Fact]
    public void CustomCaseMetadataWorksAcrossAssemblies()
    {
        var (compilation, diagnostics) = CSharpGeneratorRunner.RunGenerator(ResultSource("ReferencedResult"), languageVersion: LanguageVersion.Preview);
        diagnostics.Should().BeEmpty();
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        emit.Success.Should().BeTrue(string.Join("\n", emit.Diagnostics));
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        var consumer = compilation.RemoveAllSyntaxTrees().WithAssemblyName("consumer")
            .AddReferences(MetadataReference.CreateFromImage(stream.ToArray()))
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText("""
                using MemoryPack;
                [MemoryPackSerializable<ReferencedResult<string, int>>]
                [MemoryPackSerializable<string>, MemoryPackSerializable<int>]
                partial class Context : MemoryPackSerializerContext;
                """, options));
        CSharpGeneratorDriver.Create(new MemoryPackGenerator()).WithUpdatedParseOptions(options)
            .RunGeneratorsAndUpdateCompilation(consumer, out var generated, out var errors);
        errors.Should().BeEmpty();
        generated.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    static string ResultSource(string name) => $$"""
        using MemoryPack;
        using Polyester;
        [MemoryPackable]
        [MemoryPackUnion(0, typeof(Success<>), FormatterType = typeof(global::MemoryPack.Tests.SuccessCaseFormatter<>))]
        [MemoryPackUnion(1, typeof(Failure<>), FormatterType = typeof(global::MemoryPack.Tests.FailureCaseFormatter<>))]
        public readonly partial union {{name}}<TValue, TError>(Success<TValue>, Failure<TError>);
        """;
}

[MemoryPackable]
[MemoryPackUnion(0, typeof(Success<>), FormatterType = typeof(SuccessCaseFormatter<>))]
[MemoryPackUnion(1, typeof(Failure<>), FormatterType = typeof(FailureCaseFormatter<>))]
public readonly partial union MyAppResult<TValue, TError>(Success<TValue>, Failure<TError>);

[MemoryPackable]
[MemoryPackUnion(0, typeof(Success<>), FormatterType = typeof(PrefixedSuccessCaseFormatter<>))]
[MemoryPackUnion(1, typeof(Failure<>), FormatterType = typeof(FailureCaseFormatter<>))]
public readonly partial union PrefixedResult<TValue, TError>(Success<TValue>, Failure<TError>);

public class SuccessCaseFormatter<T> : MemoryPackFormatter<Success<T>>
{
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref Success<T> value)
        => writer.WriteValue<T>(value.Value);
    public override void Deserialize(ref MemoryPackReader reader, scoped ref Success<T> value)
        => value = new(reader.ReadValue<T>()!);
}

public sealed class FailureCaseFormatter<T> : MemoryPackFormatter<Failure<T>>
{
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref Failure<T> value)
        => writer.WriteValue<T>(value.Error);
    public override void Deserialize(ref MemoryPackReader reader, scoped ref Failure<T> value)
        => value = new(reader.ReadValue<T>()!);
}

public sealed class PrefixedSuccessCaseFormatter<T> : SuccessCaseFormatter<T>
{
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref Success<T> value)
    {
        writer.WriteObjectHeader(2);
        base.Serialize(ref writer, ref value);
    }
    public override void Deserialize(ref MemoryPackReader reader, scoped ref Success<T> value)
    {
        if (!reader.TryReadObjectHeader(out var count) || count != 2)
            throw new MemoryPackSerializationException("Invalid case prefix.");
        base.Deserialize(ref reader, ref value);
    }
}

public abstract class AbstractCaseFormatter<T> : SuccessCaseFormatter<T>;
public sealed class PrivateCtorCaseFormatter<T> : SuccessCaseFormatter<T> { private PrivateCtorCaseFormatter() { } }
public sealed class WrongArityCaseFormatter<T, TOther> : SuccessCaseFormatter<T>;

[MemoryPackSerializable<MyAppResult<string, int>>, MemoryPackSerializable<PrefixedResult<string, int>>]
[MemoryPackSerializable<string>, MemoryPackSerializable<int>]
internal partial class ExternalResultContext : MemoryPackSerializerContext;

[MemoryPackSerializable<MyAppResult<string, int>>, MemoryPackSerializable<PrefixedResult<string, int>>]
[MemoryPackSerializable<string>, MemoryPackSerializable<int>]
internal static partial class ExternalResultRegistration;

[MemoryPackSerializable<MyAppResult<string, int>>]
internal partial class MissingResultPayloadContext : MemoryPackSerializerContext;
#endif
