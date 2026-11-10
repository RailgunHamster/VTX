#nullable enable
// 测试专用：Godot.SourceGenerators 中 VTX 分析器/生成器依赖的扩展方法（实现与引擎内 ExtensionMethods.cs 一致）。
using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Godot.SourceGenerators
{
    internal static class ExtensionMethods
    {
        public static bool TryGetGlobalAnalyzerProperty(this GeneratorExecutionContext context, string property, out string? value)
            => context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property." + property, out value);

        public static bool AreGodotSourceGeneratorsDisabled(this GeneratorExecutionContext context)
            => context.TryGetGlobalAnalyzerProperty("GodotSourceGenerators", out string? toggle) &&
               toggle != null && toggle.Equals("disabled", StringComparison.OrdinalIgnoreCase);

        public static bool IsGodotToolsProject(this GeneratorExecutionContext context)
            => context.TryGetGlobalAnalyzerProperty("IsGodotToolsProject", out string? toggle) &&
               toggle != null && toggle.Equals("true", StringComparison.OrdinalIgnoreCase);

        public static bool IsGodotSourceGeneratorDisabled(this GeneratorExecutionContext context, string generatorName) =>
            AreGodotSourceGeneratorsDisabled(context) ||
            (context.TryGetGlobalAnalyzerProperty("GodotDisabledSourceGenerators", out string? disabled) &&
             disabled != null && disabled.Split(';').Contains(generatorName));

        public static string SanitizeQualifiedNameForUniqueHint(this string qualifiedName)
            => qualifiedName.Replace("@", "").Replace("<", "(Of ").Replace(">", ")");
    }
}
