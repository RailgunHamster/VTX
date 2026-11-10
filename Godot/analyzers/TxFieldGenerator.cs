using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

#pragma warning disable RS1035 // Allow scanning GodotProjectDir for Assets-as-Code (VTX.Res) generation

namespace Godot.SourceGenerators
{
    /// <summary>
    /// 1. [TxField] / [TxInvariant]：为 partial class / record 生成事务属性、ITxObject 不变量校验、快照 / Diff / JSON。
    ///    支持嵌套类型、泛型类型与继承链（派生类的不变量校验会先调用基类的校验）。
    /// 2. VTX.Res：扫描 Godot 项目目录生成强类型资源路径常量（internal，避免多个程序集之间冲突）。
    /// 可通过 MSBuild 属性 GodotDisabledSourceGenerators 包含 "TxField" / "VtxRes" 分别关闭。
    /// </summary>
    [Generator]
    public sealed class TxFieldGenerator : ISourceGenerator
    {
        public static readonly DiagnosticDescriptor RuleUnsupportedDeclaration = new DiagnosticDescriptor(
            id: "VTX011",
            title: "不受支持的 [TxField] / [TxInvariant] 声明",
            messageFormat: "{0}",
            category: "VTX",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private const string InvariantMethodName = "__VtxValidateInvariants";

        private sealed class Receiver : ISyntaxReceiver
        {
            public readonly List<FieldDeclarationSyntax> Fields = new List<FieldDeclarationSyntax>();
            public readonly List<MethodDeclarationSyntax> Methods = new List<MethodDeclarationSyntax>();

            public void OnVisitSyntaxNode(SyntaxNode node)
            {
                if (node is FieldDeclarationSyntax f && f.AttributeLists.Count > 0)
                    Fields.Add(f);
                else if (node is MethodDeclarationSyntax m && m.AttributeLists.Count > 0)
                    Methods.Add(m);
            }
        }

        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new Receiver());
        }

        public void Execute(GeneratorExecutionContext context)
        {
            if (context.IsGodotToolsProject())
                return;

            if (!context.IsGodotSourceGeneratorDisabled("TxField") && context.SyntaxReceiver is Receiver receiver)
                GenerateTxFieldClasses(context, receiver);

            if (!context.IsGodotSourceGeneratorDisabled("VtxRes"))
                GenerateAssetRegistry(context);
        }

        // =====================================================================
        // [TxField] / [TxInvariant]
        // =====================================================================

        private sealed class FieldSpec
        {
            public IFieldSymbol Symbol = null!;
            public Accessibility Access;
            public string FieldName = null!;
            public string PropertyName = null!;
            public string TypeFullName = null!;
            public string ShortTypeName = null!;
        }

        private sealed class TypeSpec
        {
            public INamedTypeSymbol Symbol = null!;
            public readonly List<FieldSpec> Fields = new List<FieldSpec>();
            public readonly List<string> Invariants = new List<string>();
        }

        private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
            .WithMiscellaneousOptions(SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        private static void Report(GeneratorExecutionContext context, Location location, string message) =>
            context.ReportDiagnostic(Diagnostic.Create(RuleUnsupportedDeclaration, location, message));

        private static void GenerateTxFieldClasses(GeneratorExecutionContext context, Receiver receiver)
        {
            // 以完全限定名（含泛型参数）为键；旧版 RS1024 会误报以符号为键的字典
            var types = new Dictionary<string, TypeSpec>(StringComparer.Ordinal);
            var compilation = context.Compilation;

            TypeSpec GetSpec(INamedTypeSymbol t)
            {
                string key = t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (!types.TryGetValue(key, out var spec))
                {
                    spec = new TypeSpec { Symbol = t };
                    types[key] = spec;
                }
                return spec;
            }

            foreach (var fieldDecl in receiver.Fields)
            {
                var model = compilation.GetSemanticModel(fieldDecl.SyntaxTree);
                foreach (var declarator in fieldDecl.Declaration.Variables)
                {
                    if (model.GetDeclaredSymbol(declarator, context.CancellationToken) is not IFieldSymbol field)
                        continue;
                    var txAttr = field.GetAttributes().FirstOrDefault(a => IsVtxAttribute(a, "TxFieldAttribute"));
                    if (txAttr == null)
                        continue;

                    var location = declarator.Identifier.GetLocation();
                    var type = field.ContainingType;
                    string? problem = CheckContainingType(type, "[TxField]");
                    if (problem == null && (field.IsStatic || field.IsConst))
                        problem = $"静态字段或常量 '{field.Name}' 不支持 [TxField]：回滚记录依附于对象实例。";
                    if (problem == null && field.IsReadOnly)
                        problem = $"readonly 字段 '{field.Name}' 不支持 [TxField]：生成的属性需要写入它。";
                    if (problem != null)
                    {
                        Report(context, location, problem);
                        continue;
                    }

                    string? customName = txAttr.ConstructorArguments.Length > 0 ? txAttr.ConstructorArguments[0].Value as string : null;
                    string propName = !string.IsNullOrWhiteSpace(customName) ? customName! : ComputePropertyName(field.Name);
                    if (!SyntaxFacts.IsValidIdentifier(propName) || SyntaxFacts.GetKeywordKind(propName) != SyntaxKind.None)
                    {
                        Report(context, location, $"[TxField] 字段 '{field.Name}' 的属性名 '{propName}' 不是合法的 C# 标识符。");
                        continue;
                    }

                    var spec = GetSpec(type);
                    bool clash = type.GetMembers(propName).Any(m => !SymbolEqualityComparer.Default.Equals(m, field))
                                 || spec.Fields.Any(f => f.PropertyName == propName)
                                 || propName == type.Name;
                    if (clash)
                    {
                        Report(context, location, $"[TxField] 字段 '{field.Name}' 要生成的属性名 '{propName}' 与已有成员冲突，请用 [TxField(\"其它名字\")] 指定属性名。");
                        continue;
                    }

                    spec.Fields.Add(new FieldSpec
                    {
                        Symbol = field,
                        Access = EffectiveAccessibility(field.Type),
                        FieldName = EscapeIdentifier(field.Name),
                        PropertyName = propName,
                        TypeFullName = field.Type.ToDisplayString(TypeFormat),
                        ShortTypeName = field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    });
                }
            }

            foreach (var methodDecl in receiver.Methods)
            {
                var model = compilation.GetSemanticModel(methodDecl.SyntaxTree);
                if (model.GetDeclaredSymbol(methodDecl, context.CancellationToken) is not IMethodSymbol method)
                    continue;
                if (!method.GetAttributes().Any(a => IsVtxAttribute(a, "TxInvariantAttribute")))
                    continue;

                var location = methodDecl.Identifier.GetLocation();
                string? problem = CheckContainingType(method.ContainingType, "[TxInvariant]");
                if (problem == null && (method.IsStatic || method.Parameters.Length > 0 || method.ReturnType.SpecialType != SpecialType.System_Boolean || method.IsGenericMethod))
                    problem = $"[TxInvariant] 方法 '{method.Name}' 必须是无参数、非泛型、返回 bool 的实例方法（返回 false 表示不变量被破坏，例如 => Tx.Require(Gold >= 0, \"金币不能为负\")）。";
                if (problem != null)
                {
                    Report(context, location, problem);
                    continue;
                }

                var spec = GetSpec(method.ContainingType);
                if (!spec.Invariants.Contains(method.Name))
                    spec.Invariants.Add(method.Name);
            }

            foreach (var spec in types.Values)
            {
                if (spec.Fields.Count == 0 && spec.Invariants.Count == 0)
                    continue;
                string source = EmitType(spec);
                string hint = spec.Symbol.ToDisplayString().SanitizeQualifiedNameForUniqueHint() + "_TxFields.generated.cs";
                context.AddSource(hint, SourceText.From(source, Encoding.UTF8));
            }
        }

        /// <summary>返回 null 表示可以为该类型生成代码，否则返回问题描述。</summary>
        private static string? CheckContainingType(INamedTypeSymbol type, string what)
        {
            if (type.TypeKind != TypeKind.Class)
                return $"{what} 只能用于 class / record class（'{type.Name}' 是 {type.TypeKind}：结构体按值复制，回滚会写到副本上）。";

            for (var t = type; t != null; t = t.ContainingType)
            {
                bool isPartial = t.DeclaringSyntaxReferences
                    .Select(r => r.GetSyntax())
                    .OfType<TypeDeclarationSyntax>()
                    .Any(d => d.Modifiers.Any(SyntaxKind.PartialKeyword));
                if (!isPartial)
                    return $"使用 {what} 的类型及其所有外层类型都必须声明为 partial（'{t.Name}' 不是 partial）。";
            }
            return null;
        }

        /// <summary>祖先类是否也会生成 VTX 代码（有 [TxField] 字段或 [TxInvariant] 方法）。</summary>
        private static void InspectAncestors(INamedTypeSymbol type, out bool ancestorGenerated, out bool ancestorHasFields)
        {
            ancestorGenerated = false;
            ancestorHasFields = false;
            for (var b = type.BaseType; b != null; b = b.BaseType)
            {
                // 源码中的祖先若本身不满足生成条件（例如不是 partial），它不会有生成的成员
                if (b.DeclaringSyntaxReferences.Length > 0 && CheckContainingType(b, string.Empty) != null)
                    continue;
                foreach (var member in b.GetMembers())
                {
                    if (member is IFieldSymbol f && !f.IsStatic && f.GetAttributes().Any(a => IsVtxAttribute(a, "TxFieldAttribute")))
                    {
                        ancestorGenerated = true;
                        ancestorHasFields = true;
                    }
                    else if (member is IMethodSymbol m && m.GetAttributes().Any(a => IsVtxAttribute(a, "TxInvariantAttribute")))
                    {
                        ancestorGenerated = true;
                    }
                }
            }
        }

        private static string GetTypeKeyword(INamedTypeSymbol t)
        {
            var syntax = t.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (syntax == null)
                return t.TypeKind == TypeKind.Struct ? "struct" : t.TypeKind == TypeKind.Interface ? "interface" : "class";
            var keyword = syntax.Keyword;
            string text = keyword.Text;
            if (text == "record")
            {
                var next = keyword.GetNextToken();
                if (next.IsKind(SyntaxKind.ClassKeyword) || next.IsKind(SyntaxKind.StructKeyword))
                    text += " " + next.Text;
            }
            return text;
        }

        private static string TypeHeader(INamedTypeSymbol t)
        {
            string typeParams = t.TypeParameters.Length > 0 ? "<" + string.Join(", ", t.TypeParameters.Select(p => p.Name)) + ">" : string.Empty;
            return $"partial {GetTypeKeyword(t)} {t.Name}{typeParams}";
        }

        private static string EmitType(TypeSpec spec)
        {
            var type = spec.Symbol;
            InspectAncestors(type, out bool ancestorGenerated, out bool ancestorHasFields);
            string newMod = ancestorHasFields ? "new " : string.Empty;

            var containers = new List<INamedTypeSymbol>();
            for (var c = type.ContainingType; c != null; c = c.ContainingType)
                containers.Insert(0, c);

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#nullable enable");
            sb.AppendLine("#pragma warning disable CS0108, CS0114");
            string ns = type.ContainingNamespace.IsGlobalNamespace ? string.Empty : type.ContainingNamespace.ToDisplayString();
            if (ns.Length > 0)
            {
                sb.Append("namespace ").AppendLine(ns);
                sb.AppendLine("{");
            }

            foreach (var c in containers)
            {
                sb.AppendLine(TypeHeader(c));
                sb.AppendLine("{");
            }

            sb.Append(TypeHeader(type));
            if (!ancestorGenerated)
                sb.Append(" : global::VTX.ITxObject");
            sb.AppendLine();
            sb.AppendLine("{");

            // 1. 不变量校验（继承链：派生类先校验基类）
            sb.AppendLine("    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
            sb.AppendLine("    [global::VTX.Decides]");
            if (ancestorGenerated)
                sb.Append("    protected override bool ").Append(InvariantMethodName).AppendLine("()");
            else if (type.IsSealed)
                sb.Append("    private bool ").Append(InvariantMethodName).AppendLine("()");
            else
                sb.Append("    protected virtual bool ").Append(InvariantMethodName).AppendLine("()");
            sb.AppendLine("    {");
            if (ancestorGenerated)
                sb.Append("        if (!base.").Append(InvariantMethodName).AppendLine("()) return false;");
            foreach (var inv in spec.Invariants)
                sb.Append("        if (!this.").Append(inv).AppendLine("()) return false;");
            sb.AppendLine("        return true;");
            sb.AppendLine("    }");
            sb.AppendLine();
            if (!ancestorGenerated)
            {
                sb.AppendLine("    [global::VTX.Decides]");
                sb.Append("    bool global::VTX.ITxObject.ValidateTxInvariants() => this.").Append(InvariantMethodName).AppendLine("();");
                sb.AppendLine();
            }

            if (spec.Fields.Count > 0)
                EmitFieldMembers(sb, spec, newMod);

            sb.AppendLine("}");
            for (int i = 0; i < containers.Count; i++)
                sb.AppendLine("}");
            if (ns.Length > 0)
                sb.AppendLine("}");
            return sb.ToString();
        }

        private static void EmitFieldMembers(StringBuilder sb, TypeSpec spec, string newMod)
        {
            var type = spec.Symbol;
            var fields = spec.Fields;
            string snapshotName = type.Name + "TxSnapshot";
            // 快照类型及其方法的可见性取所有字段类型可见性的最小值，避免 CS0050/CS0051/CS0053
            var snapshotAccess = fields.Select(f => f.Access).Aggregate(Accessibility.Public, MinAccessibility);
            string snapAcc = AccessibilityKeyword(snapshotAccess);
            string schemaSig = type.Name + "|" + string.Join("|", fields.Select(f => $"{f.PropertyName}:{f.ShortTypeName}"));

            sb.Append("    public ").Append(newMod).Append("const string TxSchemaSignature = ").Append(ToLiteral(schemaSig)).AppendLine(";");
            sb.AppendLine();

            foreach (var field in fields)
            {
                sb.Append("    ").Append(AccessibilityKeyword(field.Access)).Append(' ').Append(field.TypeFullName).Append(' ').Append(field.PropertyName).AppendLine();
                sb.AppendLine("    {");
                sb.Append("        [global::VTX.Pure] get => this.").Append(field.FieldName).AppendLine(";");
                sb.AppendLine("        [global::VTX.Transacts] set");
                sb.AppendLine("        {");
                sb.Append("            var __prev = this.").Append(field.FieldName).AppendLine(";");
                sb.Append("            if (!global::System.Collections.Generic.EqualityComparer<").Append(field.TypeFullName).AppendLine(">.Default.Equals(__prev, value))");
                sb.AppendLine("            {");
                sb.AppendLine("                if (global::VTX.Tx.IsActive)");
                sb.AppendLine("                {");
                sb.Append("                    global::VTX.Tx.Record(this, __prev, static (__owner, __old) => __owner.").Append(field.FieldName).AppendLine(" = __old);");
                sb.AppendLine("                }");
                sb.Append("                this.").Append(field.FieldName).AppendLine(" = value;");
                sb.AppendLine("            }");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
                sb.AppendLine();
            }

            // 快照（只包含本类型声明的 [TxField]；基类字段由基类自己的快照方法负责）
            sb.Append("    ").Append(snapAcc).Append(" readonly record struct ").Append(snapshotName).AppendLine("(");
            for (int i = 0; i < fields.Count; i++)
            {
                sb.Append("        ").Append(fields[i].TypeFullName).Append(' ').Append(fields[i].PropertyName);
                sb.AppendLine(i + 1 < fields.Count ? "," : ");");
            }
            sb.AppendLine();

            sb.Append("    [global::VTX.Pure] ").Append(snapAcc).Append(' ').Append(newMod).Append(snapshotName).AppendLine(" CaptureTxSnapshot() => new(");
            for (int i = 0; i < fields.Count; i++)
            {
                sb.Append("        this.").Append(fields[i].FieldName);
                sb.AppendLine(i + 1 < fields.Count ? "," : ");");
            }
            sb.AppendLine();

            sb.AppendLine("    [global::VTX.Transacts]");
            sb.Append("    ").Append(snapAcc).Append(' ').Append(newMod).Append("void RestoreTxSnapshot(").Append(snapshotName).AppendLine(" snapshot)");
            sb.AppendLine("    {");
            foreach (var f in fields)
                sb.Append("        this.").Append(f.PropertyName).Append(" = snapshot.").Append(f.PropertyName).AppendLine(";");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine("    [global::VTX.Pure]");
            sb.Append("    ").Append(snapAcc).Append(' ').Append(newMod).AppendLine("string ToTxJson() => global::System.Text.Json.JsonSerializer.Serialize(this.CaptureTxSnapshot());");
            sb.AppendLine();

            sb.AppendLine("    [global::VTX.Decides]");
            sb.Append("    ").Append(snapAcc).Append(' ').Append(newMod).AppendLine("void FromTxJson(string json)");
            sb.AppendLine("    {");
            sb.Append("        var __snap = global::System.Text.Json.JsonSerializer.Deserialize<").Append(snapshotName).AppendLine(">(json);");
            sb.AppendLine("        this.RestoreTxSnapshot(__snap);");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine("    [global::VTX.Pure]");
            sb.Append("    ").Append(snapAcc).Append(' ').Append(newMod).Append("string DiffTxState(").Append(snapshotName).AppendLine(" before)");
            sb.AppendLine("    {");
            sb.AppendLine("        var __sb = new global::System.Text.StringBuilder();");
            foreach (var f in fields)
            {
                sb.Append("        if (!global::System.Collections.Generic.EqualityComparer<").Append(f.TypeFullName)
                  .Append(">.Default.Equals(before.").Append(f.PropertyName).Append(", this.").Append(f.FieldName).AppendLine("))");
                sb.AppendLine("        {");
                sb.AppendLine("            if (__sb.Length > 0) __sb.Append(\", \");");
                sb.Append("            __sb.Append(").Append(ToLiteral(f.PropertyName + ": ")).Append(").Append(before.")
                  .Append(f.PropertyName).Append(").Append(\" -> \").Append(this.").Append(f.FieldName).AppendLine(");");
                sb.AppendLine("        }");
            }
            sb.AppendLine("        return __sb.Length == 0 ? \"(no changes)\" : __sb.ToString();");
            sb.AppendLine("    }");
        }

        // =====================================================================
        // VTX.Res：强类型资源清单
        // =====================================================================

        private sealed class AssetScan
        {
            public DateTime BuiltAtUtc;
            public readonly List<string> Scenes = new List<string>();
            public readonly List<string> Shaders = new List<string>();
            public readonly List<string> Textures = new List<string>();
            public readonly List<string> Resources = new List<string>();
            public readonly List<string> Audio = new List<string>();
            public readonly List<string> Scripts = new List<string>();
        }

        private static readonly ConcurrentDictionary<string, AssetScan> ScanCache = new ConcurrentDictionary<string, AssetScan>(StringComparer.Ordinal);
        private static readonly TimeSpan ScanMaxAge = TimeSpan.FromSeconds(5);

        private static void GenerateAssetRegistry(GeneratorExecutionContext context)
        {
            string? projectDir = TryGetGodotProjectDir(context);
            if (string.IsNullOrEmpty(projectDir) || !Directory.Exists(projectDir))
                return;

            string root = projectDir!.TrimEnd('/', '\\');
            if (!ScanCache.TryGetValue(root, out var scan) || DateTime.UtcNow - scan.BuiltAtUtc > ScanMaxAge)
            {
                scan = new AssetScan { BuiltAtUtc = DateTime.UtcNow };
                ScanProjectAssets(root, root, scan);
                ScanCache[root] = scan;
            }

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#nullable enable");
            sb.AppendLine("namespace VTX");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// 编译期自动扫描项目目录生成的强类型资源清单（Assets as Code）。");
            sb.AppendLine("    /// 消除手写 \"res://...\" 字符串拼写错误；任何被删除或改名的资源会在编译期直接报错。");
            sb.AppendLine("    /// 跳过以 '.' 开头的目录、addons/bin/obj/analyzers 以及包含 .gdignore 的目录。");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    internal static class Res");
            sb.AppendLine("    {");

            EmitAssetCategory(sb, "Scenes", scan.Scenes);
            EmitAssetCategory(sb, "Shaders", scan.Shaders);
            EmitAssetCategory(sb, "Textures", scan.Textures);
            EmitAssetCategory(sb, "Resources", scan.Resources);
            EmitAssetCategory(sb, "Audio", scan.Audio);
            EmitAssetCategory(sb, "Scripts", scan.Scripts);

            sb.AppendLine("    }");
            sb.AppendLine("}");

            context.AddSource("VTX_Res.generated.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
        }

        private static void ScanProjectAssets(string rootDir, string currentDir, AssetScan scan)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(currentDir);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            if (currentDir != rootDir && files.Any(f => Path.GetFileName(f) == ".gdignore"))
                return;

            foreach (var file in files)
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                string rel = file.Substring(rootDir.Length).TrimStart('\\', '/').Replace('\\', '/');

                switch (ext)
                {
                    case ".tscn":
                    case ".scn":
                        scan.Scenes.Add(rel);
                        break;
                    case ".gdshader":
                    case ".shader":
                        scan.Shaders.Add(rel);
                        break;
                    case ".svg":
                    case ".png":
                    case ".jpg":
                    case ".jpeg":
                    case ".webp":
                        scan.Textures.Add(rel);
                        break;
                    case ".tres":
                    case ".res":
                        scan.Resources.Add(rel);
                        break;
                    case ".wav":
                    case ".ogg":
                    case ".mp3":
                        scan.Audio.Add(rel);
                        break;
                    case ".gd":
                        scan.Scripts.Add(rel);
                        break;
                }
            }

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(currentDir);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            foreach (var subDir in subDirs)
            {
                string dirName = Path.GetFileName(subDir);
                if (dirName.StartsWith(".", StringComparison.Ordinal) ||
                    dirName.Equals("addons", StringComparison.OrdinalIgnoreCase) ||
                    dirName.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    dirName.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    dirName.Equals("analyzers", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ScanProjectAssets(rootDir, subDir, scan);
            }
        }

        private static void EmitAssetCategory(StringBuilder sb, string categoryName, List<string> items)
        {
            sb.Append("        public static class ").Append(categoryName).AppendLine();
            sb.AppendLine("        {");

            // 成员名不能与外层类名相同（CS0542）
            var usedNames = new HashSet<string>(StringComparer.Ordinal) { categoryName };

            var shortNameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var relPath in items)
            {
                string shortId = SanitizeIdentifier(Path.GetFileNameWithoutExtension(relPath));
                shortNameCounts.TryGetValue(shortId, out int c);
                shortNameCounts[shortId] = c + 1;
            }

            var sorted = items.OrderBy(x => x, StringComparer.Ordinal).ToList();

            // 先分配完整路径名（冲突时追加 _2、_3 …，而不是悄悄丢掉）
            var fullIds = new List<(string id, string relPath)>();
            foreach (var relPath in sorted)
            {
                string withoutExt = relPath.Substring(0, relPath.Length - Path.GetExtension(relPath).Length);
                string id = Unique(SanitizeIdentifier(withoutExt), usedNames);
                fullIds.Add((id, relPath));
            }

            foreach (var (id, relPath) in fullIds)
            {
                sb.Append("            public const string ").Append(id).Append(" = ").Append(ToLiteral("res://" + relPath)).AppendLine(";");
            }

            // 再为文件名唯一的资源分配短别名
            foreach (var (_, relPath) in fullIds)
            {
                string shortId = SanitizeIdentifier(Path.GetFileNameWithoutExtension(relPath));
                if (!shortNameCounts.TryGetValue(shortId, out int count) || count != 1)
                    continue;
                if (shortId == categoryName)
                    shortId += "_";
                if (usedNames.Add(shortId))
                {
                    sb.Append("            public const string ").Append(shortId).Append(" = ").Append(ToLiteral("res://" + relPath)).AppendLine(";");
                }
            }

            sb.AppendLine("        }");
        }

        private static string Unique(string id, HashSet<string> used)
        {
            if (used.Add(id))
                return id;
            for (int i = 2; ; i++)
            {
                string candidate = id + "_" + i;
                if (used.Add(candidate))
                    return candidate;
            }
        }

        private static string SanitizeIdentifier(string raw)
        {
            var sb = new StringBuilder();
            bool capitalizeNext = true;

            foreach (char ch in raw)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    if (sb.Length == 0 && char.IsDigit(ch))
                    {
                        sb.Append('_');
                    }
                    sb.Append(capitalizeNext ? char.ToUpperInvariant(ch) : ch);
                    capitalizeNext = false;
                }
                else
                {
                    if (ch is '/' or '\\')
                    {
                        sb.Append('_');
                    }
                    capitalizeNext = true;
                }
            }

            return sb.Length == 0 ? "Asset" : sb.ToString();
        }

        private static string ToLiteral(string value)
        {
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char ch in value)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (char.IsControl(ch))
                            sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else
                            sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // =====================================================================
        // 工具
        // =====================================================================

        internal static string? TryGetGodotProjectDir(GeneratorExecutionContext context)
        {
            if (context.TryGetGlobalAnalyzerProperty("GodotProjectDirBase64", out string? b64) && !string.IsNullOrEmpty(b64))
            {
                try
                {
                    return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                }
                catch (FormatException)
                {
                }
            }

            if (context.TryGetGlobalAnalyzerProperty("GodotProjectDir", out string? dir) && !string.IsNullOrEmpty(dir))
            {
                return dir;
            }

            return null;
        }

        private static string EscapeIdentifier(string name) =>
            SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

        private static int AccessRank(Accessibility a) => a switch
        {
            Accessibility.Private => 0,
            Accessibility.ProtectedAndInternal => 1,
            Accessibility.Protected => 2,
            Accessibility.Internal => 2,
            Accessibility.ProtectedOrInternal => 3,
            _ => 4,
        };

        private static Accessibility MinAccessibility(Accessibility a, Accessibility b)
        {
            if ((a == Accessibility.Protected && b == Accessibility.Internal) || (a == Accessibility.Internal && b == Accessibility.Protected))
                return Accessibility.ProtectedAndInternal;
            return AccessRank(a) <= AccessRank(b) ? a : b;
        }

        /// <summary>一个类型在别处被使用时的有效可见性（考虑外层类型、数组元素与泛型实参）。</summary>
        private static Accessibility EffectiveAccessibility(ITypeSymbol type)
        {
            switch (type)
            {
                case IArrayTypeSymbol array:
                    return EffectiveAccessibility(array.ElementType);
                case IPointerTypeSymbol pointer:
                    return EffectiveAccessibility(pointer.PointedAtType);
                case ITypeParameterSymbol:
                    return Accessibility.Public;
                case INamedTypeSymbol named:
                {
                    var result = Accessibility.Public;
                    for (var t = named; t != null; t = t.ContainingType)
                    {
                        if (t.DeclaredAccessibility != Accessibility.NotApplicable)
                            result = MinAccessibility(result, t.DeclaredAccessibility);
                    }
                    foreach (var arg in named.TypeArguments)
                        result = MinAccessibility(result, EffectiveAccessibility(arg));
                    return result;
                }
                default:
                    return Accessibility.Public;
            }
        }

        private static string AccessibilityKeyword(Accessibility a) => a switch
        {
            Accessibility.Private => "private",
            Accessibility.ProtectedAndInternal => "private protected",
            Accessibility.Protected => "protected",
            Accessibility.Internal => "internal",
            Accessibility.ProtectedOrInternal => "protected internal",
            _ => "public",
        };

        private static bool IsVtxAttribute(AttributeData attr, string name) =>
            attr.AttributeClass != null &&
            attr.AttributeClass.Name == name &&
            attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "VTX";

        internal static string ComputePropertyName(string fieldName)
        {
            string trimmed = fieldName;
            if (trimmed.StartsWith("m_", StringComparison.Ordinal) && trimmed.Length > 2)
                trimmed = trimmed.Substring(2);
            else if (trimmed.StartsWith("_", StringComparison.Ordinal) && trimmed.Length > 1)
                trimmed = trimmed.Substring(1);

            if (trimmed.Length == 0)
                return fieldName + "Prop";

            return char.ToUpperInvariant(trimmed[0]) + trimmed.Substring(1);
        }
    }
}
