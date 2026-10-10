using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

namespace MemoryPack.Generator;

public partial class MemoryPackGenerator
{
    static readonly DiagnosticDescriptor InvalidSerializationContext = new(
        "MEMPACK050", "Invalid serialization context", "Serialization context '{0}' must be a non-generic, top-level static partial class without a Register method, or a non-abstract partial class deriving directly from MemoryPackSerializerContext without constructors or reserved members", "MemoryPack", DiagnosticSeverity.Error, true);
    static readonly DiagnosticDescriptor UnsupportedContextType = new(
        "MEMPACK051", "Unsupported context dependency", "Serialization context '{0}' cannot register '{1}': {2}", "MemoryPack", DiagnosticSeverity.Error, true);
    static readonly DiagnosticDescriptor InvalidTypeInfoProperty = new(
        "MEMPACK053", "Invalid metadata property", "Serialization context '{0}' cannot generate metadata property '{1}': {2}. Specify a unique TypeInfoPropertyName on the root attribute", "MemoryPack", DiagnosticSeverity.Error, true);

    void RegisterSerializationContexts(IncrementalGeneratorInitializationContext context)
    {
        var contexts = context.SyntaxProvider.ForAttributeWithMetadataName(
            "MemoryPack.MemoryPackSerializableAttribute",
            static (node, _) => node is TypeDeclarationSyntax,
            static (ctx, _) => (INamedTypeSymbol)ctx.TargetSymbol);
        var genericContexts = context.SyntaxProvider.ForAttributeWithMetadataName(
            "MemoryPack.MemoryPackSerializableAttribute`1",
            static (node, _) => node is TypeDeclarationSyntax,
            static (ctx, _) => (INamedTypeSymbol)ctx.TargetSymbol);
        context.RegisterSourceOutput(contexts.Collect().Combine(genericContexts.Collect()).Combine(context.CompilationProvider), static (ctx, source) =>
        {
            foreach (var symbol in source.Left.Left.Concat(source.Left.Right).Distinct(SymbolEqualityComparer.Default).OfType<INamedTypeSymbol>())
                GenerateSerializationContext(ctx, symbol, source.Right);
        });
    }

    static void GenerateSerializationContext(SourceProductionContext context, INamedTypeSymbol symbol, Compilation compilation)
    {
        var location = symbol.Locations.FirstOrDefault();
        var instanceContext = !symbol.IsStatic && symbol.BaseType?.ToDisplayString() == "MemoryPack.MemoryPackSerializerContext";
        if ((!symbol.IsStatic && !instanceContext) || symbol.TypeKind != TypeKind.Class || symbol.IsGenericType || symbol.ContainingType != null
            || symbol.GetMembers("Register").Length != 0 || instanceContext && (symbol.IsAbstract
                || symbol.InstanceConstructors.Any(x => !x.IsImplicitlyDeclared)
                || new[] { "Default", "Options", "GetTypeInfo", "__memoryPackTypeInfos" }.Any(x => symbol.GetMembers(x).Length != 0))
            || symbol.DeclaringSyntaxReferences.Any(x => x.GetSyntax() is not ClassDeclarationSyntax declaration || !declaration.Modifiers.Any(SyntaxKind.PartialKeyword)))
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidSerializationContext, location, symbol.Name));
            return;
        }

        var references = new ReferenceSymbols(compilation);
        var hasError = false;
        bool CheckStructuralLimit(ITypeSymbol type)
        {
            var nodes = new Stack<ITypeSymbol>();
            nodes.Push(type);
            var count = 0;
            while (nodes.Count != 0)
            {
                var node = nodes.Pop();
                if (++count > 512)
                {
                    var display = type is INamedTypeSymbol named ? named.OriginalDefinition.ToDisplayString() : type.TypeKind.ToString();
                    context.ReportDiagnostic(Diagnostic.Create(UnsupportedContextType, location, symbol.Name, display,
                        "a dependency type exceeds 512 structural nodes; expanding generic arguments are unsupported"));
                    hasError = true;
                    return false;
                }
                if (node is IArrayTypeSymbol array) nodes.Push(array.ElementType);
                else if (node is INamedTypeSymbol named)
                {
                    foreach (var argument in named.TypeArguments) nodes.Push(argument);
                    if (named.ContainingType != null) nodes.Push(named.ContainingType);
                }
            }
            return true;
        }
        var canonicalTypes = new Dictionary<ITypeSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
        ITypeSymbol CanonicalType(ITypeSymbol type)
        {
            if (canonicalTypes.TryGetValue(type, out var cached)) return cached;
            ITypeSymbol canonical;
            if (type is IDynamicTypeSymbol) canonical = compilation.GetSpecialType(SpecialType.System_Object);
            else if (type is IArrayTypeSymbol array) canonical = compilation.CreateArrayTypeSymbol(CanonicalType(array.ElementType), array.Rank);
            else if (type is INamedTypeSymbol named && !named.IsUnboundGenericType)
            {
                if (named.IsTupleType && named.TupleUnderlyingType is { } underlying) canonical = CanonicalType(underlying);
                else
                {
                    var definition = named.ContainingType == null ? named.OriginalDefinition :
                        ((INamedTypeSymbol)CanonicalType(named.ContainingType)).GetTypeMembers(named.Name, named.Arity).First();
                    canonical = definition.Arity == 0 ? definition : definition.Construct(named.TypeArguments.Select(CanonicalType).ToArray());
                }
            }
            else canonical = type;
            canonical = canonical.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
            canonicalTypes[type] = canonical;
            return canonical;
        }
        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var registrations = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var formatterExpressions = new Dictionary<ITypeSymbol, string>(SymbolEqualityComparer.Default);
        var explicitFormatters = new Dictionary<ITypeSymbol, INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var roots = symbol.GetAttributes().Where(x => x.AttributeClass is { } attribute
            && attribute.ContainingNamespace.ToDisplayString() == "MemoryPack"
            && attribute.MetadataName is "MemoryPackSerializableAttribute" or "MemoryPackSerializableAttribute`1").ToArray();
        ITypeSymbol? GetRoot(AttributeData attribute)
        {
            var type = attribute.AttributeClass!.Arity == 1 ? attribute.AttributeClass.TypeArguments[0] : attribute.ConstructorArguments.FirstOrDefault().Value as ITypeSymbol;
            if (type != null && !CheckStructuralLimit(type)) return type;
            return type == null ? null : CanonicalType(type);
        }
        var unionFormatters = compilation.GetSymbolsWithName(static _ => true, SymbolFilter.Type)
            .OfType<INamedTypeSymbol>().Where(x => x.ContainsAttribute(references.MemoryPackUnionFormatterAttribute)).ToArray();

        void Error(ITypeSymbol type, string reason)
        {
            context.ReportDiagnostic(Diagnostic.Create(UnsupportedContextType, location, symbol.Name, type.ToDisplayString(), reason));
            hasError = true;
        }

        foreach (var attribute in roots)
        {
            if (GetRoot(attribute) is not { } root) continue;
            if (hasError) break;
            var formatter = attribute.NamedArguments.FirstOrDefault(x => x.Key == "FormatterType").Value.Value as INamedTypeSymbol;
            if (formatter == null) continue;
            if (!CheckStructuralLimit(formatter)) break;
            formatter = (INamedTypeSymbol)CanonicalType(formatter);
            if (explicitFormatters.TryGetValue(root, out var existing) && !SymbolEqualityComparer.Default.Equals(existing, formatter))
            {
                Error(root, "conflicting explicit formatter declarations");
            }
            explicitFormatters[root] = formatter;
        }

        bool IsOpen(ITypeSymbol type) => type is ITypeParameterSymbol
            || type is IArrayTypeSymbol array && IsOpen(array.ElementType)
            || type is INamedTypeSymbol named && (named.IsUnboundGenericType || named.TypeArguments.Any(IsOpen) || named.ContainingType != null && IsOpen(named.ContainingType));

        bool HasDirectFactory(INamedTypeSymbol type) => type.GetMembers("__MemoryPackCreateFormatter").OfType<IMethodSymbol>().Any(x => x.IsStatic && x.Parameters.Length == 0
            && compilation.IsSymbolAccessibleWithin(x, symbol) && x.ReturnType is INamedTypeSymbol result
            && result.OriginalDefinition.ToDisplayString() == "MemoryPack.MemoryPackFormatter<T>" && SymbolEqualityComparer.Default.Equals(result.TypeArguments[0], type));

        void AddFormatter(ITypeSymbol type, string formatter)
        {
            formatterExpressions[type] = $"new {formatter}()";
            registrations[type.FullyQualifiedToString()] = $"global::MemoryPack.MemoryPackFormatterProvider.Register(new {formatter}());";
        }

        var walkDepth = 0;
        void Visit(ITypeSymbol type)
        {
            if (hasError || visited.Contains(type)) return;
            if (!CheckStructuralLimit(type)) return;
            type = CanonicalType(type);
            if (visited.Contains(type)) return;
            // Expanding generic recursion can create infinitely many distinct closed types.
            // Bound the active walk without limiting the number of independent roots.
            if (walkDepth == 128)
            {
                Error(type, "the dependency graph exceeds 128 levels; infinitely expanding generic recursion and excessively deep graphs are unsupported");
                return;
            }
            walkDepth++;
            try { VisitCore(type); }
            finally { walkDepth--; }
        }

        void VisitCore(ITypeSymbol type)
        {
            if (!visited.Add(type)) return;
            if (IsOpen(type)) { Error(type, "open generic types are not supported; specify a closed constructed type"); return; }
            if (type.IsRefLikeType || type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer || !compilation.IsSymbolAccessibleWithin(type, symbol))
            { Error(type, "the type cannot be referenced by this context"); return; }

            if (explicitFormatters.TryGetValue(type, out var explicitFormatter))
            {
                var formatterBase = compilation.GetTypeByMetadataName("MemoryPack.MemoryPackFormatter`1")!.Construct(type);
                if (IsOpen(explicitFormatter) || explicitFormatter.IsAbstract || !compilation.IsSymbolAccessibleWithin(explicitFormatter, symbol)
                    || !explicitFormatter.GetAllBaseTypes().Any(x => SymbolEqualityComparer.Default.Equals(CanonicalType(x), formatterBase))
                    || !explicitFormatter.InstanceConstructors.Any(x => x.Parameters.Length == 0 && compilation.IsSymbolAccessibleWithin(x, symbol)))
                { Error(type, "FormatterType must be an accessible concrete MemoryPackFormatter<T> with a parameterless constructor"); return; }
                AddFormatter(type, explicitFormatter.FullyQualifiedToString());
                return;
            }

            if (type is INamedTypeSymbol unionRoot)
            {
                var matches = unionFormatters.Where(x =>
                {
                    var target = x.GetAttribute(references.MemoryPackUnionFormatterAttribute)!.ConstructorArguments[0].Value as INamedTypeSymbol;
                    return target != null && (SymbolEqualityComparer.Default.Equals(target, unionRoot)
                        || target.IsUnboundGenericType && SymbolEqualityComparer.Default.Equals(target.OriginalDefinition, unionRoot.OriginalDefinition));
                }).ToArray();
                if (matches.Length > 1) { Error(type, "multiple external union formatters target this type"); return; }
                if (matches.Length == 1)
                {
                    var unionFormatter = matches[0];
                    if (unionFormatter.IsGenericType)
                    {
                        if (unionFormatter.Arity != unionRoot.TypeArguments.Length)
                        { Error(type, "external union formatter generic arity does not match the root"); return; }
                        unionFormatter = unionFormatter.Construct(unionRoot.TypeArguments.ToArray());
                    }
                    if (!compilation.IsSymbolAccessibleWithin(unionFormatter, symbol))
                    { Error(type, "external union formatter is inaccessible"); return; }
                    AddFormatter(type, unionFormatter.FullyQualifiedToString());
                    foreach (var attribute in unionFormatter.GetAttributes().Where(x => SymbolEqualityComparer.Default.Equals(x.AttributeClass, references.MemoryPackUnionAttribute)))
                    {
                        var implementation = (INamedTypeSymbol)attribute.ConstructorArguments[1].Value!;
                        if (implementation.IsUnboundGenericType && implementation.Arity == unionRoot.TypeArguments.Length)
                            implementation = implementation.OriginalDefinition.Construct(unionRoot.TypeArguments.ToArray());
                        Visit(implementation);
                    }
                    return;
                }
            }
            if (type is INamedTypeSymbol model && model.ContainsAttribute(references.MemoryPackableAttribute))
            {
                var meta = new TypeMeta(model, references);
                if (meta.GenerateType == GenerateType.NoGenerate)
                {
                    if (instanceContext)
                    {
                        if (model.AllInterfaces.Any(x => x.EqualsUnconstructedGenericType(references.IMemoryPackable)))
                            formatterExpressions[type] = $"new global::MemoryPack.Formatters.MemoryPackableFormatter<{type.FullyQualifiedToString()}>()";
                        else if (HasDirectFactory(model)) formatterExpressions[type] = $"{type.FullyQualifiedToString()}.__MemoryPackCreateFormatter()";
                        else
                        { Error(type, "NoGenerate metadata requires an explicit FormatterType; global registration alone cannot create instance metadata"); return; }
                        return; // Handwritten serialization/factory dependencies must be declared explicitly.
                    }
                    if (!model.AllInterfaces.Any(x => x.OriginalDefinition.ToDisplayString() == "MemoryPack.IMemoryPackFormatterRegister"))
                    { Error(type, "NoGenerate types require an explicit FormatterType or IMemoryPackFormatterRegister implementation"); return; }
                }
                else if (meta.IsUnion)
                {
                    if (instanceContext && model.DeclaringSyntaxReferences.Length == 0 && !HasDirectFactory(model))
                    { Error(type, "this referenced union has no generated formatter factory; specify FormatterType and declare its case roots"); return; }
                    formatterExpressions[type] = $"{type.FullyQualifiedToString()}.__MemoryPackCreateFormatter()";
                }
                else if (meta.GenerateType == GenerateType.Collection)
                {
                    var (kind, collection) = TypeMeta.ParseCollectionKind(model, references);
                    var collectionFormatter = kind switch { CollectionKind.Set => "GenericSetFormatter", CollectionKind.Dictionary => "GenericDictionaryFormatter", _ => "GenericCollectionFormatter" };
                    formatterExpressions[type] = $"new global::MemoryPack.Formatters.{collectionFormatter}<{type.FullyQualifiedToString()}, {string.Join(", ", collection!.TypeArguments.Select(x => x.FullyQualifiedToString()))}>()";
                }
                else formatterExpressions[type] = $"new global::MemoryPack.Formatters.MemoryPackableFormatter<{type.FullyQualifiedToString()}>()";
                registrations[type.FullyQualifiedToString()] = $"global::MemoryPack.MemoryPackFormatterProvider.Register<{type.FullyQualifiedToString()}>();";
                if (meta.IsUnion)
                {
                    foreach (var (_, unionType) in meta.UnionTags)
                    {
                        var closed = unionType is INamedTypeSymbol { IsUnboundGenericType: true } named && model.TypeArguments.Length == named.Arity
                            ? named.OriginalDefinition.Construct(model.TypeArguments.ToArray()) : unionType;
                        Visit(closed);
                    }
                }
                else
                {
                    foreach (var member in meta.Members.Where(x => x.Kind != MemberKind.CustomFormatter && x.Kind != MemberKind.Blank)) Visit(member.MemberType);
                    if (meta.GenerateType == GenerateType.Collection)
                    {
                        var (_, collection) = TypeMeta.ParseCollectionKind(model, references);
                        if (collection != null) foreach (var argument in collection.TypeArguments) Visit(argument);
                    }
                }
                return;
            }

            if (type.IsUnmanagedType)
            {
                AddFormatter(type, $"global::MemoryPack.Formatters.UnmanagedFormatter<{type.FullyQualifiedToString()}>");
                return;
            }
            var formatter = references.KnownTypes.GetNonDefaultFormatterName(type);
            if (formatter != null)
            {
                AddFormatter(type, formatter);
                if (type is IArrayTypeSymbol array) Visit(array.ElementType);
                else if (type is INamedTypeSymbol generic)
                {
                    foreach (var argument in generic.TypeArguments) Visit(argument);
                    var definition = generic.ConstructUnboundGenericType().ToDisplayString();
                    if (definition == "System.Collections.Generic.PriorityQueue<,>")
                        Visit(compilation.GetTypeByMetadataName("System.ValueTuple`2")!.Construct(generic.TypeArguments.ToArray()));
                    else if (definition == "System.Linq.ILookup<,>")
                        Visit(compilation.GetTypeByMetadataName("System.Linq.IGrouping`2")!.Construct(generic.TypeArguments.ToArray()));
                    else if (definition == "System.Linq.IGrouping<,>")
                        Visit(compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1")!.Construct(generic.TypeArguments[1]));
                    else if (definition is "System.Collections.Generic.IEnumerable<>" or "System.Collections.Generic.ICollection<>" or "System.Collections.Generic.IList<>" or "System.Collections.Generic.IReadOnlyList<>" or "System.Collections.Generic.IReadOnlyCollection<>")
                        Visit(compilation.GetTypeByMetadataName("System.Collections.Generic.List`1")!.Construct(generic.TypeArguments[0]));
                    else if (definition is "System.Collections.Generic.ISet<>" or "System.Collections.Generic.IReadOnlySet<>")
                        Visit(compilation.GetTypeByMetadataName("System.Collections.Generic.HashSet`1")!.Construct(generic.TypeArguments[0]));
                }
                return;
            }
            var builtinFormatter = type.SpecialType == SpecialType.System_String ? "StringFormatter" : type.ToDisplayString() switch
            {
                "System.Version" => "VersionFormatter", "System.Uri" => "UriFormatter", "System.TimeZoneInfo" => "TimeZoneInfoFormatter",
                "System.Numerics.BigInteger" => "BigIntegerFormatter", "System.Collections.BitArray" => "BitArrayFormatter", "System.Text.StringBuilder" => "StringBuilderFormatter",
                "System.Type" => "TypeFormatter", "System.Globalization.CultureInfo" => "CultureInfoFormatter", _ => null
            };
            if (builtinFormatter != null) { AddFormatter(type, "global::MemoryPack.Formatters." + builtinFormatter); return; }
            Error(type, "no statically known formatter; supply FormatterType on a MemoryPackSerializable declaration");
        }

        foreach (var root in roots)
        {
            if (hasError) break;
            if (GetRoot(root) is { } type) Visit(type);
            else
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedContextType, location, symbol.Name, "null", "a root must specify a type"));
                hasError = true;
            }
        }
        if (hasError) return;

        if (instanceContext)
        {
            EmitInstanceSerializationContext(context, symbol, formatterExpressions, roots, GetRoot);
            return;
        }

        var output = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
        if (!symbol.ContainingNamespace.IsGlobalNamespace) output.Append("namespace ").Append(symbol.ContainingNamespace.ToDisplayString()).AppendLine(" {");
        output.Append("static partial class @").Append(symbol.Name).AppendLine(" {");
        output.AppendLine("    static readonly object __memoryPackRegistrationLock = new object();");
        output.AppendLine("    static bool __memoryPackRegistered;");
        output.AppendLine("    public static void Register() {");
        output.AppendLine("        lock (__memoryPackRegistrationLock) {");
        output.AppendLine("            if (__memoryPackRegistered) return;");
        foreach (var entry in registrations)
        {
            if (explicitFormatters.Keys.Any(x => x.FullyQualifiedToString() == entry.Key))
            {
                output.Append("            ").AppendLine(entry.Value);
                continue;
            }
            output.Append("            if (!global::MemoryPack.MemoryPackFormatterProvider.IsRegistered<").Append(entry.Key).AppendLine(">()) {");
            output.Append("                ").AppendLine(entry.Value);
            output.AppendLine("            }");
        }
        output.AppendLine("            __memoryPackRegistered = true;");
        output.AppendLine("        }\n    }\n}");
        if (!symbol.ContainingNamespace.IsGlobalNamespace) output.AppendLine("}");
        context.AddSource(symbol.ToDisplayString().Replace('<', '_').Replace('>', '_') + ".MemoryPackContext.g.cs", output.ToString());
    }

    static void EmitInstanceSerializationContext(SourceProductionContext context, INamedTypeSymbol symbol,
        Dictionary<ITypeSymbol, string> formatters, AttributeData[] roots, Func<AttributeData, ITypeSymbol?> getRoot)
    {
        string Name(ITypeSymbol type) => type switch
        {
            IArrayTypeSymbol array => Name(array.ElementType) + (array.Rank == 1 ? "Array" : array.Rank + "DArray"),
            INamedTypeSymbol named => (named.ContainingType == null ? "" : Name(named.ContainingType)) + named.Name + string.Concat(named.TypeArguments.Select(Name)),
            _ => type.Name
        };
        var properties = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
        var propertyTypes = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
        var namesByType = new Dictionary<ITypeSymbol, string>(SymbolEqualityComparer.Default);
        var invalid = false;
        foreach (var root in roots)
        {
            var type = getRoot(root)!;
            var property = root.NamedArguments.FirstOrDefault(x => x.Key == "TypeInfoPropertyName").Value.Value as string ?? Name(type);
            var reason = !SyntaxFacts.IsValidIdentifier(property) ? "the name is not a valid C# identifier" :
                property is "Default" or "Options" or "GetTypeInfo" or "GetRequiredTypeInfo" or "GetType" or "Equals" or "GetHashCode" or "ToString" or "MemberwiseClone" or "Finalize" or "Register" or "__memoryPackTypeInfos" || property == symbol.Name || symbol.GetMembers(property).Length != 0 ? "the name is reserved or already declared" :
                properties.TryGetValue(property, out var other) && !SymbolEqualityComparer.Default.Equals(type, other) ? "different roots have the same property name" :
                namesByType.TryGetValue(type, out var existing) && existing != property ? "the same root has conflicting property names" : null;
            if (reason != null)
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidTypeInfoProperty, root.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? symbol.Locations.FirstOrDefault(), symbol.Name, property, reason));
                invalid = true;
                continue;
            }
            properties[property] = type;
            if (!propertyTypes.ContainsKey(property))
                propertyTypes[property] = root.AttributeClass!.Arity == 1 ? root.AttributeClass.TypeArguments[0] : (ITypeSymbol)root.ConstructorArguments[0].Value!;
            namesByType[type] = property;
        }
        if (invalid) return;
        var output = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
        if (!symbol.ContainingNamespace.IsGlobalNamespace) output.Append("namespace ").Append(symbol.ContainingNamespace.ToDisplayString()).AppendLine(" {");
        output.Append("partial class @").Append(symbol.Name).AppendLine(" {");
        output.Append("    public static ").Append(symbol.FullyQualifiedToString()).AppendLine(" Default { get; } = new();");
        output.AppendLine("    readonly global::System.Collections.Generic.Dictionary<global::System.Type, global::MemoryPack.MemoryPackTypeInfo> __memoryPackTypeInfos;");
        output.Append("    public @").Append(symbol.Name).AppendLine("() : this(null) { }");
        output.Append("    public @").Append(symbol.Name).AppendLine("(global::MemoryPack.MemoryPackSerializerOptions? options) : base(options) {");
        output.AppendLine("        // Built-in collection formatters differ only in nullable annotations; the CLR types are identical.");
        output.AppendLine("        __memoryPackTypeInfos = new global::System.Collections.Generic.Dictionary<global::System.Type, global::MemoryPack.MemoryPackTypeInfo> {");
        foreach (var entry in formatters.OrderBy(x => x.Key.FullyQualifiedToString(), StringComparer.Ordinal))
            output.Append("            { typeof(").Append(entry.Key.FullyQualifiedToString()).Append("), new global::MemoryPack.MemoryPackTypeInfo<").Append(entry.Key.FullyQualifiedToString())
                .Append(">((global::MemoryPack.MemoryPackFormatter<").Append(entry.Key.FullyQualifiedToString()).Append(">)(object)(").Append(entry.Value).AppendLine("), this) },");
        output.AppendLine("        };\n    }");
        foreach (var property in properties.OrderBy(x => x.Key, StringComparer.Ordinal))
            output.Append("    public global::MemoryPack.MemoryPackTypeInfo<").Append(propertyTypes[property.Key].FullyQualifiedToString()).Append("> @").Append(property.Key).Append(" => GetTypeInfo<").Append(propertyTypes[property.Key].FullyQualifiedToString()).AppendLine(">();");
        output.AppendLine("    public override global::MemoryPack.MemoryPackTypeInfo? GetTypeInfo(global::System.Type type) {\n        global::System.ArgumentNullException.ThrowIfNull(type);\n        return __memoryPackTypeInfos.TryGetValue(type, out var metadata) ? metadata : null;\n    }\n}");
        if (!symbol.ContainingNamespace.IsGlobalNamespace) output.AppendLine("}");
        context.AddSource(symbol.ToDisplayString() + ".MemoryPackContext.g.cs", output.ToString());
    }
}
