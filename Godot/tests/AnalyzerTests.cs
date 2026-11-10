#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace VTX.Tests;

/// <summary>
/// 分析器 / 生成器测试：
/// - Cases/*.cs 每个文件是一组用例。在行尾写 <c>// expect: VTX001</c>（多个用逗号分隔）或 <c>// expect: none</c>；
///   未标注的行不允许出现任何 VTX 诊断，任何编译错误都算失败（严格模式，同时捕获漏报与误报）。
/// - 若用例文件定义了 <c>public static class CaseRun { public static string Run() }</c>，会编译并执行，返回 "ok" 视为通过。
/// - 生成器在一个临时的“Godot 项目目录”上运行（见 CreateFakeProject），用于测试 VTX.Res 与 VTX008。
/// </summary>
internal static class AnalyzerTests
{
    private static readonly string BaseDir = AppContext.BaseDirectory;
    private static readonly Regex ExpectRegex = new(@"//\s*expect:\s*([A-Za-z0-9_, ]+)", RegexOptions.Compiled);

    public static void Register()
    {
        string casesDir = Path.Combine(BaseDir, "Cases");
        if (!Directory.Exists(casesDir))
            return;
        foreach (var file in Directory.GetFiles(casesDir, "*.cs").OrderBy(f => f, StringComparer.Ordinal))
        {
            string path = file;
            T.Add($"Analyzer: {Path.GetFileName(path)}", () => RunCase(path, File.ReadAllText(path)));
        }

        T.Add("Analyzer: docs/Examples.cs 零诊断且编译通过", () =>
        {
            string examples = File.ReadAllText(Path.Combine(BaseDir, "sources", "docs", "Examples.cs"));
            // Examples.cs 依赖 Godot 生成器产生的 SignalName；这里补一个等价的桩。
            const string signalShim = @"namespace GodotTest.Examples { public partial class VtxFeatureShowcase { public static class SignalName { public static readonly Godot.StringName LootDropped = ""LootDropped""; } } }";
            // 顺便执行 Examples 自带的综合自检（同步部分），验证生成器输出 + 运行时语义端到端正确
            const string runner = @"public static class CaseRun { public static string Run() { var r = new GodotTest.Examples.VtxFeatureShowcase().RunAllNewFeaturesSelfTest(); return r.Contains(""False"") ? r : ""ok""; } }";
            RunCase("Examples.cs", examples, extraSources: new[] { signalShim, runner }, strictAllLines: true);
        });

        T.Add("Generator: 尊重 GodotDisabledSourceGenerators=TxField", () =>
        {
            var result = Compile("[assembly: System.Reflection.AssemblyTitle(\"x\")] public partial class P { [VTX.TxField] private int _a; }", "disabled.cs",
                Array.Empty<string>(), new Dictionary<string, string> { ["build_property.GodotDisabledSourceGenerators"] = "TxField" });
            T.True(!result.GeneratedSources.Any(s => s.Contains("_TxFields")), "禁用后不生成");
        });
    }

    private sealed class Options : AnalyzerConfigOptions
    {
        private readonly Dictionary<string, string> _values;
        public Options(Dictionary<string, string> values) => _values = values;
        public override bool TryGetValue(string key, out string value)
        {
            if (_values.TryGetValue(key, out var v)) { value = v; return true; }
            value = null!;
            return false;
        }
    }

    private sealed class OptionsProvider : AnalyzerConfigOptionsProvider
    {
        private readonly Options _global;
        private static readonly Options Empty = new(new Dictionary<string, string>());
        public OptionsProvider(Dictionary<string, string> global) => _global = new Options(global);
        public override AnalyzerConfigOptions GlobalOptions => _global;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Empty;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Empty;
    }

    private sealed record CompileResult(Compilation Compilation, ImmutableArray<Diagnostic> CompileErrors, ImmutableArray<Diagnostic> VtxDiagnostics, List<string> GeneratedSources);

    private static string? _fakeProject;

    /// <summary>临时 Godot 项目目录，供 VTX.Res 生成与 VTX008 校验使用。</summary>
    private static string CreateFakeProject()
    {
        if (_fakeProject != null) return _fakeProject;
        string root = Path.Combine(Path.GetTempPath(), "vtx-tests-project-" + Environment.ProcessId);
        if (Directory.Exists(root)) Directory.Delete(root, true);
        void Touch(string rel) { string p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, ""); }
        Touch("project.godot");
        Touch("icon.svg");
        Touch("demo/player.tscn");
        Touch("demo/player.gdshader");
        Touch("levels/level_1.tscn");
        Touch("levels/Level-Two.tscn");
        Touch("textures/Textures.png");
        Touch("ui/a-b.tscn");
        Touch("ui/a_b.tscn");
        Touch("Art/Hero.png");
        Touch("ignored/.gdignore");
        Touch("ignored/secret.tscn");
        Touch(".godot/imported/x.tscn");
        Touch("addons/plugin/plugin.tscn");
        Touch("data/items.json");
        _fakeProject = root + Path.DirectorySeparatorChar;
        return _fakeProject;
    }

    private static IEnumerable<string> RuntimeSourceFiles() =>
        new[] { "VtxAttributes.cs", "Tx.cs", "TxCollections.cs", "TxRandom.cs", "TxGodot.cs", "TxTask.cs", "VtxThread.cs" }
            .Select(f => Path.Combine(BaseDir, "sources", "runtime", f))
            .Append(Path.Combine(BaseDir, "sources", "GodotStubs.cs"));

    private static CompileResult Compile(string source, string fileName, IEnumerable<string> extraSources, Dictionary<string, string>? extraOptions = null)
    {
        var parse = new CSharpParseOptions(LanguageVersion.CSharp12);
        var trees = RuntimeSourceFiles().Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parse, path: Path.GetFileName(f), encoding: Encoding.UTF8)).ToList();
        int i = 0;
        foreach (var extra in extraSources)
            trees.Add(CSharpSyntaxTree.ParseText(extra, parse, path: $"extra{i++}.cs", encoding: Encoding.UTF8));
        var caseTree = CSharpSyntaxTree.ParseText(source, parse, path: fileName, encoding: Encoding.UTF8);
        trees.Add(caseTree);

        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create(
            "VtxCase",
            trees,
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
                .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic> { ["CS1998"] = ReportDiagnostic.Suppress }));

        var globals = new Dictionary<string, string>
        {
            ["build_property.GodotProjectDir"] = CreateFakeProject(),
        };
        if (extraOptions != null)
            foreach (var kv in extraOptions) globals[kv.Key] = kv.Value;
        var provider = new OptionsProvider(globals);

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new ISourceGenerator[] { new Godot.SourceGenerators.TxFieldGenerator() },
            parseOptions: parse,
            optionsProvider: provider);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outCompilation, out var generatorDiagnostics);
        var generated = driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()).ToList();
        // 设置环境变量 VTX_TEST_DUMP=<目录> 可以把生成器输出写出来查看
        string? dump = Environment.GetEnvironmentVariable("VTX_TEST_DUMP");
        if (!string.IsNullOrEmpty(dump))
        {
            string dir = Path.Combine(dump, Path.GetFileNameWithoutExtension(fileName));
            Directory.CreateDirectory(dir);
            foreach (var tree in driver.GetRunResult().GeneratedTrees)
                File.WriteAllText(Path.Combine(dir, Path.GetFileName(tree.FilePath)), tree.ToString());
        }

        var withAnalyzers = outCompilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new Godot.SourceGenerators.VtxAnalyzer()),
            new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty, provider));
        var analyzerDiagnostics = withAnalyzers.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();

        var errors = outCompilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        var vtx = generatorDiagnostics.Concat(analyzerDiagnostics).Where(d => d.Id.StartsWith("VTX", StringComparison.Ordinal)).ToImmutableArray();
        var unexpectedAnalyzerErrors = analyzerDiagnostics.Where(d => d.Id == "AD0001").ToList();
        foreach (var ad in unexpectedAnalyzerErrors)
            T.Fail($"分析器崩溃: {ad.GetMessage()}");
        return new CompileResult(outCompilation, errors, vtx, generated);
    }

    private static void RunCase(string fileName, string source, IEnumerable<string>? extraSources = null, bool strictAllLines = true)
    {
        var result = Compile(source, Path.GetFileName(fileName), extraSources ?? Array.Empty<string>());
        string[] lines = source.Replace("\r\n", "\n").Split('\n');

        // 行号 -> 期望的诊断集合
        var expected = new Dictionary<int, HashSet<string>>();
        var expectedCompile = new HashSet<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            var m = ExpectRegex.Match(lines[i]);
            if (!m.Success) continue;
            var ids = m.Groups[1].Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0 && s != "none").ToHashSet();
            expected[i] = ids;
            foreach (var id in ids.Where(id => id.StartsWith("CS", StringComparison.Ordinal))) expectedCompile.Add($"{i}:{id}");
        }

        string caseName = Path.GetFileName(fileName);
        foreach (var err in result.CompileErrors)
        {
            var span = err.Location.GetLineSpan();
            bool inCase = span.Path == caseName;
            if (inCase && expectedCompile.Contains($"{span.StartLinePosition.Line}:{err.Id}"))
                continue;
            T.Fail($"编译错误 {span.Path}({span.StartLinePosition.Line + 1}): {err.Id} {err.GetMessage()}");
        }

        var actual = new Dictionary<int, HashSet<string>>();
        foreach (var d in result.VtxDiagnostics)
        {
            var span = d.Location.GetLineSpan();
            if (span.Path != caseName)
            {
                if (d.Location != Location.None)
                    T.Fail($"用例以外的位置出现诊断 {d.Id} {span.Path}({span.StartLinePosition.Line + 1}): {d.GetMessage()}");
                else
                    T.Fail($"无位置诊断 {d.Id}: {d.GetMessage()}");
                continue;
            }
            int line = span.StartLinePosition.Line;
            if (!actual.TryGetValue(line, out var set)) actual[line] = set = new HashSet<string>();
            set.Add(d.Id);
        }

        foreach (var (line, ids) in expected)
        {
            var vtxIds = ids.Where(id => id.StartsWith("VTX", StringComparison.Ordinal)).ToHashSet();
            actual.TryGetValue(line, out var got);
            got ??= new HashSet<string>();
            if (!vtxIds.SetEquals(got))
            {
                string msgs = string.Join(" | ", result.VtxDiagnostics.Where(d => d.Location.GetLineSpan().Path == caseName && d.Location.GetLineSpan().StartLinePosition.Line == line).Select(d => d.GetMessage()));
                T.Fail($"{caseName}:{line + 1} 期望 [{string.Join(",", vtxIds.OrderBy(x => x))}] 实际 [{string.Join(",", got.OrderBy(x => x))}]  {lines[line].Trim()}  {msgs}");
            }
        }
        if (strictAllLines)
        {
            foreach (var (line, ids) in actual)
            {
                if (expected.ContainsKey(line)) continue;
                string msgs = string.Join(" | ", result.VtxDiagnostics.Where(d => d.Location.GetLineSpan().Path == caseName && d.Location.GetLineSpan().StartLinePosition.Line == line).Select(d => d.Id + " " + d.GetMessage()));
                T.Fail($"{caseName}:{line + 1} 出现未标注的诊断: {msgs}  ←  {lines[line].Trim()}");
            }
        }

        bool hasRunner = source.Contains("class CaseRun") || (extraSources?.Any(e => e.Contains("class CaseRun")) ?? false);
        if (hasRunner && result.CompileErrors.IsEmpty)
        {
            using var ms = new MemoryStream();
            var emit = result.Compilation.Emit(ms);
            if (!emit.Success)
            {
                T.Fail("Emit 失败: " + string.Join("; ", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())));
                return;
            }
            var alc = new AssemblyLoadContext(caseName, isCollectible: true);
            try
            {
                ms.Position = 0;
                var asm = alc.LoadFromStream(ms);
                // 用例程序集里有自己的一份 Godot 桩：把当前线程登记为它的主线程
                asm.GetType("Godot.OS")?.GetField("TestMainThreadId")?.SetValue(null, (ulong)Environment.CurrentManagedThreadId);
                var run = asm.GetTypes().First(t => t.Name == "CaseRun").GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
                string outcome;
                try { outcome = (string)run.Invoke(null, null)!; }
                catch (TargetInvocationException tie) { outcome = "exception: " + tie.InnerException; }
                T.Eq("ok", outcome, $"{caseName} CaseRun.Run()");
            }
            finally
            {
                alc.Unload();
            }
        }
    }
}
