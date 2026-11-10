using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

#pragma warning disable RS1035 // Allow checking res:// asset paths and vtx.schema.txt on disk

namespace Godot.SourceGenerators
{
    /// <summary>
    /// VTX 编译期红线。核心概念：
    /// - “事务上下文”（Tx）：[Transacts]/[Decides]/[TxInvariant] 方法与访问器（含 override / 接口实现继承来的效果），
    ///   以及传给带 [Transacts]/[Decides] 委托参数（如 Tx.Try / Tx.Simulate / Tx.FirstOf / Tx.WhereCan）的 lambda。
    /// - “纯上下文”（Pure）：[Pure] 方法与访问器，以及传给带 [Pure] 委托参数的 lambda。
    /// 两种上下文都只允许修改自己声明的局部变量；Tx 上下文还允许写事务属性 / 事务容器，并调用 [Transacts]/[Decides]/[Pure] 方法。
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class VtxAnalyzer : DiagnosticAnalyzer
    {
        public static readonly DiagnosticDescriptor RuleNonTxMutation = new DiagnosticDescriptor(
            id: "VTX001",
            title: "事务内禁止修改非事务状态",
            messageFormat: "事务上下文内禁止修改非事务状态 '{0}'。请使用 [TxField] 生成的事务属性、TxArray<T>/TxList<T>/TxDict<K,V> 或 TxGodot 封装。",
            category: "VTX",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor RuleEffectViolation = new DiagnosticDescriptor(
            id: "VTX002",
            title: "事务内禁止调用非事务或不可回滚方法",
            messageFormat: "{0}。请调用标记了 [Transacts]/[Decides]/[Pure] 的方法，或通过 TxGodot / Tx.Defer 延迟执行。",
            category: "VTX",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor RuleNoSuspendInTx = new DiagnosticDescriptor(
            id: "VTX003",
            title: "事务必须在单帧同步完成，禁止跨帧挂起",
            messageFormat: "事务上下文必须在单帧内同步执行完毕，禁止使用 '{0}'（await、[Suspends] 异步挂起或延迟执行的迭代器）。",
            category: "VTX",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor RuleDirectTxFieldWrite = new DiagnosticDescriptor(
            id: "VTX004",
            title: "禁止直接修改 [TxField] 底层私有字段",
            messageFormat: "字段 '{0}' 标记了 [TxField]，禁止直接写入底层字段（会绕过事务 Undo Log），请改用生成的属性 '{1}'（构造函数内的初始化除外）。",
            category: "VTX",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor RuleRawToSignalInSuspends = new DiagnosticDescriptor(
            id: "VTX005",
            title: "[Suspends] 结构化并发内的等待必须可取消、按游戏时间推进",
            messageFormat: "{0}",
            category: "VTX",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor RuleMutableCollectionInTxField = new DiagnosticDescriptor(
            id: "VTX006",
            title: "禁止在 [TxField] 上声明非事务可变容器或原生数组",
            messageFormat: "字段 '{0}' 的类型 '{1}' 是非事务可变容器，[TxField] 只能回滚引用指针而无法回滚其内部元素修改。请改用 'TxArray<T>'、'TxList<T>' 或 'TxDict<K,V>'。",
            category: "VTX",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor RuleNonDeterministicCall = new DiagnosticDescriptor(
            id: "VTX007",
            title: "事务或 [Pure] 内禁止调用非确定性或不可回滚的随机/时间源",
            messageFormat: "事务或 [Pure] 上下文内禁止调用非确定性源 '{0}'（会导致 Tx.Simulate 试跑与 Tx.Try 真跑结果不一致且无法回滚种子）。请改用 'TxRandom'，或在事务外生成后作为参数传入。",
            category: "VTX",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor RuleInvalidResPath = new DiagnosticDescriptor(
            id: "VTX008",
            title: "res:// 资源路径在项目中不存在",
            messageFormat: "资源路径 '{0}' {1}",
            category: "VTX",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor RulePersistableSchemaBreak = new DiagnosticDescriptor(
            id: "VTX009",
            title: "[TxPersistable] 类破坏了存档 Schema 向后兼容性",
            messageFormat: "[TxPersistable] 类 '{0}' 破坏了存档向后兼容性：基线字段 '{1} ({2})' {3}。请保留该 [TxField] 字段与原类型，或新增字段进行平滑迁移。",
            category: "VTX",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor RulePureViolation = new DiagnosticDescriptor(
            id: "VTX010",
            title: "[Pure] 方法不能有副作用",
            messageFormat: "[Pure] 上下文内禁止{0}。[Pure] 只能修改自己的局部变量、只能调用 [Pure] 方法；需要试跑事务逻辑请用 Tx.Simulate / Tx.WhereCan。",
            category: "VTX",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor RuleDiscardedSuspends = new DiagnosticDescriptor(
            id: "VTX012",
            title: "[Suspends] 任务被丢弃，异常会悄悄丢失",
            messageFormat: "{0}",
            category: "VTX",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor RuleBackgroundThread = new DiagnosticDescriptor(
            id: "VTX013",
            title: "后台线程里使用了只能在主线程使用的东西",
            messageFormat: "{0}",
            category: "VTX",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor RuleDiscardedDecides = new DiagnosticDescriptor(
            id: "VTX014",
            title: "返回 bool 的 [Decides] 方法的结果被丢弃，失败会被忽略",
            messageFormat: "{0}",
            category: "VTX",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor RuleUnrolledFailure = new DiagnosticDescriptor(
            id: "VTX015",
            title: "返回 bool 的 [Decides] 方法失败后，它已做的修改不会回滚",
            messageFormat: "{0}",
            category: "VTX",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(
                RuleNonTxMutation,
                RuleEffectViolation,
                RuleNoSuspendInTx,
                RuleDirectTxFieldWrite,
                RuleRawToSignalInSuspends,
                RuleMutableCollectionInTxField,
                RuleNonDeterministicCall,
                RuleInvalidResPath,
                RulePersistableSchemaBreak,
                RulePureViolation,
                RuleDiscardedSuspends,
                RuleBackgroundThread,
                RuleDiscardedDecides,
                RuleUnrolledFailure);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterSyntaxNodeAction(AnalyzeClassDeclaration, SyntaxKind.ClassDeclaration, SyntaxKind.RecordDeclaration);
            context.RegisterSyntaxNodeAction(AnalyzeFieldDeclaration, SyntaxKind.FieldDeclaration);
            context.RegisterSyntaxNodeAction(AnalyzeMethodLike,
                SyntaxKind.MethodDeclaration,
                SyntaxKind.LocalFunctionStatement,
                SyntaxKind.GetAccessorDeclaration,
                SyntaxKind.SetAccessorDeclaration,
                SyntaxKind.InitAccessorDeclaration);
            context.RegisterSyntaxNodeAction(AnalyzeAssignment,
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxKind.AddAssignmentExpression,
                SyntaxKind.SubtractAssignmentExpression,
                SyntaxKind.MultiplyAssignmentExpression,
                SyntaxKind.DivideAssignmentExpression,
                SyntaxKind.ModuloAssignmentExpression,
                SyntaxKind.AndAssignmentExpression,
                SyntaxKind.OrAssignmentExpression,
                SyntaxKind.ExclusiveOrAssignmentExpression,
                SyntaxKind.LeftShiftAssignmentExpression,
                SyntaxKind.RightShiftAssignmentExpression,
                SyntaxKind.CoalesceAssignmentExpression);
            context.RegisterSyntaxNodeAction(AnalyzeUnaryMutation,
                SyntaxKind.PreIncrementExpression,
                SyntaxKind.PostIncrementExpression,
                SyntaxKind.PreDecrementExpression,
                SyntaxKind.PostDecrementExpression);
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
            context.RegisterSyntaxNodeAction(AnalyzeArgument, SyntaxKind.Argument);
            context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
            context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
            context.RegisterSyntaxNodeAction(AnalyzeAwait, SyntaxKind.AwaitExpression);
            context.RegisterSyntaxNodeAction(AnalyzeBackgroundName, SyntaxKind.IdentifierName, SyntaxKind.GenericName);
            context.RegisterSyntaxNodeAction(AnalyzeAsyncVoid,
                SyntaxKind.MethodDeclaration,
                SyntaxKind.LocalFunctionStatement,
                SyntaxKind.ParenthesizedLambdaExpression,
                SyntaxKind.SimpleLambdaExpression);
            context.RegisterSyntaxNodeAction(AnalyzeAwaitStatement,
                SyntaxKind.ForEachStatement,
                SyntaxKind.ForEachVariableStatement,
                SyntaxKind.UsingStatement,
                SyntaxKind.LocalDeclarationStatement);

            context.RegisterCompilationStartAction(start =>
            {
                string? projectDir = TryGetProjectDir(start.Options.AnalyzerConfigOptionsProvider.GlobalOptions);
                if (string.IsNullOrEmpty(projectDir))
                    return;
                var index = new Lazy<ProjectFileIndex?>(() => ProjectFileIndex.Get(projectDir!));
                start.RegisterSyntaxNodeAction(c => AnalyzeStringLiteral(c, index), SyntaxKind.StringLiteralExpression);
            });
        }

        // =====================================================================
        // 效果（Effect）解析：自身特性 + 属性级特性 + override 链 + 接口实现
        // =====================================================================

        [Flags]
        private enum Effect
        {
            None = 0,
            Transacts = 1,
            Decides = 2,
            Pure = 4,
            Suspends = 8,
            NoRollback = 16,
            TxInvariant = 32,
            TxLike = Transacts | Decides | TxInvariant,
        }

        private static Effect OwnEffects(ISymbol symbol)
        {
            var e = Effect.None;
            foreach (var attr in symbol.GetAttributes())
            {
                var cls = attr.AttributeClass;
                if (cls == null || cls.ContainingNamespace?.ToDisplayString() != "VTX")
                    continue;
                switch (cls.Name)
                {
                    case "TransactsAttribute": e |= Effect.Transacts; break;
                    case "DecidesAttribute": e |= Effect.Decides; break;
                    case "PureAttribute": e |= Effect.Pure; break;
                    case "SuspendsAttribute": e |= Effect.Suspends; break;
                    case "NoRollbackAttribute": e |= Effect.NoRollback; break;
                    case "TxInvariantAttribute": e |= Effect.TxInvariant; break;
                }
            }
            return e;
        }

        private static Effect GetEffects(IMethodSymbol? method)
        {
            if (method == null)
                return Effect.None;

            // 用列表 + SymbolEqualityComparer 去重（数量很少）；旧版 RS1024 会误报 HashSet<ISymbol>
            var visited = new List<IMethodSymbol>();
            var e = Effect.None;
            var stack = new Stack<IMethodSymbol>();
            stack.Push(method.ReducedFrom ?? method);
            while (stack.Count > 0)
            {
                var m = stack.Pop();
                var def = m.OriginalDefinition;
                if (visited.Any(v => SymbolEqualityComparer.Default.Equals(v, def)))
                    continue;
                visited.Add(def);

                e |= OwnEffects(def);
                if (def.AssociatedSymbol != null)
                    e |= OwnEffects(def.AssociatedSymbol);

                if (def.OverriddenMethod != null)
                    stack.Push(def.OverriddenMethod);

                foreach (var explicitImpl in def.ExplicitInterfaceImplementations)
                    stack.Push(explicitImpl);

                var type = def.ContainingType;
                if (type != null && type.TypeKind != TypeKind.Interface && !def.IsStatic)
                {
                    foreach (var iface in type.AllInterfaces)
                    {
                        foreach (var member in iface.GetMembers(def.Name).OfType<IMethodSymbol>())
                        {
                            var impl = type.FindImplementationForInterfaceMember(member) as IMethodSymbol;
                            if (impl != null && SymbolEqualityComparer.Default.Equals(impl.OriginalDefinition, def))
                                stack.Push(member);
                        }
                    }
                }
            }
            return e;
        }

        private static bool IsTxLike(Effect e) => (e & Effect.TxLike) != 0;

        // =====================================================================
        // 上下文（Scope）判定
        // =====================================================================

        private enum ScopeKind
        {
            Tx,
            Pure,
        }

        private sealed class Scope
        {
            public ScopeKind Kind;
            public SyntaxNode Node = null!;
        }

        private static readonly HashSet<string> DeferredExecutionApis = new HashSet<string>(StringComparer.Ordinal)
        {
            "VTX.Tx.Defer",
            "VTX.Tx.Record",
            "VTX.TxTask.Defer",
        };

        private static Scope? FindScope(SemanticModel model, SyntaxNode node, System.Threading.CancellationToken ct)
        {
            for (SyntaxNode? cur = node.Parent; cur != null; cur = cur.Parent)
            {
                switch (cur)
                {
                    case AnonymousFunctionExpressionSyntax anon:
                    {
                        if (model.GetSymbolInfo(anon, ct).Symbol is IMethodSymbol lambdaSym)
                        {
                            var own = OwnEffects(lambdaSym);
                            if (IsTxLike(own))
                                return new Scope { Kind = ScopeKind.Tx, Node = anon };
                            if ((own & Effect.Pure) != 0)
                                return new Scope { Kind = ScopeKind.Pure, Node = anon };
                        }

                        if (anon.Parent is ArgumentSyntax arg && TryGetCalledMethodAndParameter(model, arg, ct, out var called, out var param))
                        {
                            string fullName = (called!.ContainingType?.ToDisplayString() ?? string.Empty) + "." + called.Name;
                            if (DeferredExecutionApis.Contains(fullName))
                                return null; // 延迟到事务之外执行：内部不受事务约束

                            var pe = OwnEffects(param!.OriginalDefinition);
                            if (IsTxLike(pe))
                                return new Scope { Kind = ScopeKind.Tx, Node = anon };
                            if ((pe & Effect.Pure) != 0)
                                return new Scope { Kind = ScopeKind.Pure, Node = anon };
                        }
                        // 其它 lambda（LINQ、List.ForEach、局部变量等）视为在当前位置内联执行，继续向外查找
                        break;
                    }
                    case LocalFunctionStatementSyntax localFunc:
                    {
                        if (model.GetDeclaredSymbol(localFunc, ct) is IMethodSymbol localSym)
                        {
                            var own = OwnEffects(localSym);
                            if (IsTxLike(own))
                                return new Scope { Kind = ScopeKind.Tx, Node = localFunc };
                            if ((own & Effect.Pure) != 0)
                                return new Scope { Kind = ScopeKind.Pure, Node = localFunc };
                        }
                        break;
                    }
                    case MethodDeclarationSyntax:
                    case AccessorDeclarationSyntax:
                    case OperatorDeclarationSyntax:
                    case ConversionOperatorDeclarationSyntax:
                    {
                        var sym = model.GetDeclaredSymbol(cur, ct) as IMethodSymbol;
                        return ScopeForMember(sym, cur);
                    }
                    case ArrowExpressionClauseSyntax arrow when arrow.Parent is PropertyDeclarationSyntax || arrow.Parent is IndexerDeclarationSyntax:
                    {
                        // 表达式体属性：public int X => ...; 等价于 get 访问器
                        var prop = model.GetDeclaredSymbol(arrow.Parent, ct) as IPropertySymbol;
                        return ScopeForMember(prop?.GetMethod, cur);
                    }
                    case ConstructorDeclarationSyntax:
                    case DestructorDeclarationSyntax:
                    case BaseTypeDeclarationSyntax:
                    case FieldDeclarationSyntax:
                    case PropertyDeclarationSyntax:
                    case EventDeclarationSyntax:
                        return null;
                }
            }
            return null;
        }

        private static Scope? ScopeForMember(IMethodSymbol? sym, SyntaxNode node)
        {
            if (sym == null || IsTrustedRuntimeType(sym.ContainingType))
                return null;
            var e = GetEffects(sym);
            if (IsTxLike(e))
                return new Scope { Kind = ScopeKind.Tx, Node = node };
            if ((e & Effect.Pure) != 0)
                return new Scope { Kind = ScopeKind.Pure, Node = node };
            return null;
        }

        private static bool IsTrustedRuntimeType(INamedTypeSymbol? type)
        {
            while (type != null)
            {
                if (type.ContainingNamespace?.ToDisplayString() == "VTX" &&
                    type.Name is "Tx" or "TxArray" or "TxList" or "TxDict" or "TxGodot" or "TxTask" or "TxScope" or "TxRandom")
                {
                    return true;
                }
                type = type.ContainingType;
            }
            return false;
        }

        private static bool TryGetCalledMethodAndParameter(
            SemanticModel model,
            ArgumentSyntax arg,
            System.Threading.CancellationToken ct,
            out IMethodSymbol? method,
            out IParameterSymbol? parameter)
        {
            method = null;
            parameter = null;
            if (arg.Parent is not BaseArgumentListSyntax argList)
                return false;

            var info = model.GetSymbolInfo(argList.Parent!, ct);
            method = info.Symbol as IMethodSymbol ??
                     (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] as IMethodSymbol : null);
            if (method == null)
                return false;

            if (arg.NameColon != null)
            {
                string name = arg.NameColon.Name.Identifier.ValueText;
                parameter = method.Parameters.FirstOrDefault(p => p.Name == name);
                return parameter != null;
            }

            int index = argList.Arguments.IndexOf(arg);
            if (index < 0)
                return false;
            if (index < method.Parameters.Length)
            {
                parameter = method.Parameters[index];
                return true;
            }
            if (method.Parameters.Length > 0 && method.Parameters[method.Parameters.Length - 1].IsParams)
            {
                parameter = method.Parameters[method.Parameters.Length - 1];
                return true;
            }
            return false;
        }

        // =====================================================================
        // 声明级检查
        // =====================================================================

        private static void AnalyzeClassDeclaration(SyntaxNodeAnalysisContext context)
        {
            var classDecl = (TypeDeclarationSyntax)context.Node;
            if (classDecl.AttributeLists.Count == 0)
                return;

            if (context.SemanticModel.GetDeclaredSymbol(classDecl, context.CancellationToken) is not INamedTypeSymbol classSym)
                return;

            // 只在携带 [TxPersistable] 的那个 partial 声明上做检查，避免重复报告
            bool hasPersistableHere = false;
            foreach (var list in classDecl.AttributeLists)
            {
                foreach (var attrSyntax in list.Attributes)
                {
                    if (context.SemanticModel.GetSymbolInfo(attrSyntax, context.CancellationToken).Symbol is IMethodSymbol ctor &&
                        IsVtxType(ctor.ContainingType, "TxPersistableAttribute"))
                    {
                        hasPersistableHere = true;
                        break;
                    }
                }
            }

            if (!hasPersistableHere)
                return;

            var currentFields = new Dictionary<string, (string shortType, string fullType)>(StringComparer.Ordinal);
            foreach (var member in classSym.GetMembers().OfType<IFieldSymbol>())
            {
                if (!HasVtxAttribute(member, "TxFieldAttribute"))
                    continue;

                string propName = GetTxFieldPropertyName(member);
                string shortType = member.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                string fullType = member.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                currentFields[propName] = (shortType, fullType);
            }

            foreach (var attr in classSym.GetAttributes())
            {
                if (attr.AttributeClass == null || !IsVtxType(attr.AttributeClass, "TxBaselineFieldAttribute"))
                    continue;
                if (attr.ConstructorArguments.Length < 2)
                    continue;

                string? baseProp = attr.ConstructorArguments[0].Value as string;
                string? baseType = attr.ConstructorArguments[1].Value as string;
                if (string.IsNullOrEmpty(baseProp) || string.IsNullOrEmpty(baseType))
                    continue;

                VerifyBaselineField(context, classDecl.Identifier.GetLocation(), classSym.Name, baseProp!, baseType!, currentFields);
            }

            // 项目根目录的 vtx.schema.txt（每行：ClassName|Prop1:Type1|Prop2:Type2）
            string? projectDir = TryGetProjectDir(context.Options.AnalyzerConfigOptionsProvider.GlobalOptions);
            if (!string.IsNullOrEmpty(projectDir))
            {
                string schemaFile = Path.Combine(projectDir!, "vtx.schema.txt");
                if (File.Exists(schemaFile))
                {
                    try
                    {
                        foreach (var rawLine in File.ReadAllLines(schemaFile))
                        {
                            string line = rawLine.Trim();
                            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                                continue;

                            string[] parts = line.Split('|');
                            if (parts.Length < 2 || parts[0].Trim() != classSym.Name)
                                continue;

                            for (int i = 1; i < parts.Length; i++)
                            {
                                int colon = parts[i].IndexOf(':');
                                if (colon <= 0) continue;
                                string bProp = parts[i].Substring(0, colon).Trim();
                                string bType = parts[i].Substring(colon + 1).Trim();
                                VerifyBaselineField(context, classDecl.Identifier.GetLocation(), classSym.Name, bProp, bType, currentFields);
                            }
                        }
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        private static void VerifyBaselineField(
            SyntaxNodeAnalysisContext context,
            Location location,
            string className,
            string baseProp,
            string baseType,
            Dictionary<string, (string shortType, string fullType)> currentFields)
        {
            if (!currentFields.TryGetValue(baseProp, out var actual))
            {
                context.ReportDiagnostic(Diagnostic.Create(RulePersistableSchemaBreak, location, className, baseProp, baseType, "已被删除或重命名"));
                return;
            }

            string fullNoGlobal = actual.fullType.StartsWith("global::", StringComparison.Ordinal) ? actual.fullType.Substring(8) : actual.fullType;
            if (!string.Equals(actual.shortType, baseType, StringComparison.Ordinal) &&
                !string.Equals(actual.fullType, baseType, StringComparison.Ordinal) &&
                !string.Equals(fullNoGlobal, baseType, StringComparison.Ordinal))
            {
                context.ReportDiagnostic(Diagnostic.Create(RulePersistableSchemaBreak, location, className, baseProp, baseType, $"类型被修改为 '{actual.shortType}'"));
            }
        }

        private static void AnalyzeFieldDeclaration(SyntaxNodeAnalysisContext context)
        {
            var fieldDecl = (FieldDeclarationSyntax)context.Node;
            if (fieldDecl.AttributeLists.Count == 0)
                return;

            foreach (var variable in fieldDecl.Declaration.Variables)
            {
                if (context.SemanticModel.GetDeclaredSymbol(variable, context.CancellationToken) is not IFieldSymbol fieldSym)
                    continue;
                if (!HasVtxAttribute(fieldSym, "TxFieldAttribute"))
                    continue;

                if (IsNonTransactionalMutableCollection(fieldSym.Type))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        RuleMutableCollectionInTxField,
                        variable.Identifier.GetLocation(),
                        fieldSym.Name,
                        fieldSym.Type.ToDisplayString()));
                }
            }
        }

        private static bool IsNonTransactionalMutableCollection(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol)
                return true;
            if (type is not INamedTypeSymbol named)
                return false;

            string ns = named.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            if (ns == "VTX" || ns.StartsWith("System.Collections.Immutable", StringComparison.Ordinal))
                return false;
            if (named.SpecialType == SpecialType.System_String)
                return false;

            string def = named.OriginalDefinition.ToDisplayString();
            if (def.StartsWith("Godot.Collections.", StringComparison.Ordinal))
                return true;

            foreach (var iface in named.AllInterfaces)
            {
                string idef = iface.OriginalDefinition.ToDisplayString();
                if (idef == "System.Collections.Generic.ICollection<T>" || idef == "System.Collections.ICollection" || idef == "System.Collections.IList")
                    return true;
            }
            return false;
        }

        private static void AnalyzeMethodLike(SyntaxNodeAnalysisContext context)
        {
            if (context.SemanticModel.GetDeclaredSymbol(context.Node, context.CancellationToken) is not IMethodSymbol methodSymbol)
                return;
            if (IsTrustedRuntimeType(methodSymbol.ContainingType))
                return;

            var effects = GetEffects(methodSymbol);
            if (!IsTxLike(effects))
                return;

            Location location = context.Node switch
            {
                MethodDeclarationSyntax m => m.Identifier.GetLocation(),
                LocalFunctionStatementSyntax lf => lf.Identifier.GetLocation(),
                AccessorDeclarationSyntax acc => acc.Keyword.GetLocation(),
                _ => context.Node.GetLocation()
            };

            if ((effects & Effect.Suspends) != 0 || methodSymbol.IsAsync)
            {
                context.ReportDiagnostic(Diagnostic.Create(RuleNoSuspendInTx, location, methodSymbol.Name));
            }

            // 迭代器方法体在枚举时才执行（通常已在事务之外），不能作为事务方法
            SyntaxNode? body = context.Node switch
            {
                MethodDeclarationSyntax md => (SyntaxNode?)md.Body ?? md.ExpressionBody,
                LocalFunctionStatementSyntax lf => (SyntaxNode?)lf.Body ?? lf.ExpressionBody,
                AccessorDeclarationSyntax acc => (SyntaxNode?)acc.Body ?? acc.ExpressionBody,
                _ => null
            };
            if (body != null && ContainsYield(body))
            {
                context.ReportDiagnostic(Diagnostic.Create(RuleNoSuspendInTx, location, "yield（迭代器）"));
            }
        }

        private static bool ContainsYield(SyntaxNode body)
        {
            foreach (var node in body.DescendantNodes(n => n is not LocalFunctionStatementSyntax && n is not AnonymousFunctionExpressionSyntax))
            {
                if (node is YieldStatementSyntax)
                    return true;
            }
            return false;
        }

        // =====================================================================
        // 修改（赋值 / 自增 / out-ref 实参 / 事件订阅）
        // =====================================================================

        private static void AnalyzeAssignment(SyntaxNodeAnalysisContext context)
        {
            var assignment = (AssignmentExpressionSyntax)context.Node;

            // 对象初始化器 / with 表达式里的赋值只初始化新对象
            if (assignment.Parent is InitializerExpressionSyntax)
                return;

            CheckMutationTarget(context, assignment.Left, assignment.GetLocation());
        }

        private static void AnalyzeUnaryMutation(SyntaxNodeAnalysisContext context)
        {
            ExpressionSyntax? operand = context.Node switch
            {
                PrefixUnaryExpressionSyntax pre => pre.Operand,
                PostfixUnaryExpressionSyntax post => post.Operand,
                _ => null
            };
            if (operand != null)
            {
                CheckMutationTarget(context, operand, context.Node.GetLocation());
            }
        }

        private static void CheckMutationTarget(SyntaxNodeAnalysisContext context, ExpressionSyntax target, Location reportLocation)
        {
            target = StripParenthesesAndSuppressions(target);

            // 元组解构赋值：(a.X, b.Y) = ...
            if (target is TupleExpressionSyntax tuple)
            {
                foreach (var element in tuple.Arguments)
                {
                    if (element.Expression is DeclarationExpressionSyntax)
                        continue;
                    CheckMutationTarget(context, element.Expression, reportLocation);
                }
                return;
            }
            if (target is DeclarationExpressionSyntax)
                return;

            var model = context.SemanticModel;
            var symbol = model.GetSymbolInfo(target, context.CancellationToken).Symbol;
            if (symbol is IDiscardSymbol)
                return;

            // 1. 任何地方直接写 [TxField] 底层字段都会绕过 Undo Log（声明类型的构造函数内除外）
            if (symbol is IFieldSymbol txField && HasVtxAttribute(txField, "TxFieldAttribute"))
            {
                if (!IsInsideConstructorOf(target, model, txField.ContainingType, context.CancellationToken))
                {
                    context.ReportDiagnostic(Diagnostic.Create(RuleDirectTxFieldWrite, reportLocation, txField.Name, GetTxFieldPropertyName(txField)));
                }
                return;
            }

            var scope = FindScope(model, target, context.CancellationToken);
            if (scope == null)
                return;

            string? violation = ClassifyMutation(model, target, symbol, scope, context.CancellationToken);
            if (violation == null)
                return;

            if (scope.Kind == ScopeKind.Pure)
                context.ReportDiagnostic(Diagnostic.Create(RulePureViolation, reportLocation, $"修改 '{violation}'"));
            else
                context.ReportDiagnostic(Diagnostic.Create(RuleNonTxMutation, reportLocation, violation));
        }

        /// <summary>返回 null 表示允许；否则返回被修改目标的描述。</summary>
        private static string? ClassifyMutation(SemanticModel model, ExpressionSyntax target, ISymbol? symbol, Scope scope, System.Threading.CancellationToken ct)
        {
            switch (symbol)
            {
                case ILocalSymbol local:
                    if (!IsDeclaredIn(local, scope.Node, ct))
                        return local.Name + " (外部捕获局部变量)";
                    // ref 局部变量是别名，写入的是它引用的外部存储
                    if (local.IsRef)
                        return local.Name + " (ref 局部变量)";
                    return null;

                case IParameterSymbol param:
                    if (!IsDeclaredIn(param, scope.Node, ct))
                        return param.Name + " (外部捕获参数)";
                    // ref 参数指向调用方的存储位置，写入不会被回滚；out 参数是返回值通道，允许
                    if (param.RefKind == RefKind.Ref)
                        return param.Name + " (ref 参数)";
                    return null;

                case IEventSymbol evt:
                    return $"{evt.ContainingType?.Name}.{evt.Name}（事件订阅/退订无法回滚）";

                case IPropertySymbol prop:
                    if (target is ElementAccessExpressionSyntax || prop.IsIndexer)
                    {
                        if (scope.Kind == ScopeKind.Tx && prop.SetMethod != null && IsTxLike(GetEffects(prop.SetMethod)))
                            return null;
                        if (IsFreshLocalReceiver(model, target, scope, ct))
                            return null;
                        return prop.ContainingType?.Name + "[]";
                    }
                    if (scope.Kind == ScopeKind.Tx && prop.SetMethod != null && IsTxLike(GetEffects(prop.SetMethod)))
                        return null;
                    if (IsFreshLocalReceiver(model, target, scope, ct))
                        return null;
                    return $"{prop.ContainingType?.Name}.{prop.Name}";

                case IFieldSymbol field:
                    if (IsFreshLocalReceiver(model, target, scope, ct))
                        return null;
                    return $"{field.ContainingType?.Name}.{field.Name}";

                default:
                    // 原生数组元素等
                    if (target is ElementAccessExpressionSyntax && IsFreshLocalReceiver(model, target, scope, ct))
                        return null;
                    return target.ToString();
            }
        }

        private static ExpressionSyntax StripParenthesesAndSuppressions(ExpressionSyntax expr)
        {
            while (true)
            {
                switch (expr)
                {
                    case ParenthesizedExpressionSyntax p:
                        expr = p.Expression;
                        continue;
                    case PostfixUnaryExpressionSyntax post when post.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                        expr = post.Operand;
                        continue;
                    default:
                        return expr;
                }
            }
        }

        private static bool IsDeclaredIn(ISymbol symbol, SyntaxNode scopeNode, System.Threading.CancellationToken ct)
        {
            var decl = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct);
            return decl != null && decl.SyntaxTree == scopeNode.SyntaxTree && scopeNode.Span.Contains(decl.Span);
        }

        /// <summary>
        /// 目标形如 local.X / local[i]，且 local 是在当前上下文中声明、用新建表达式初始化、之后从未被重新赋值的局部变量：
        /// 修改的是本次事务新建的对象（回滚后不可达），因此允许。值类型局部变量同理。
        /// </summary>
        private static bool IsFreshLocalReceiver(SemanticModel model, ExpressionSyntax target, Scope scope, System.Threading.CancellationToken ct)
        {
            ExpressionSyntax? receiver = target switch
            {
                MemberAccessExpressionSyntax ma => ma.Expression,
                ElementAccessExpressionSyntax ea => ea.Expression,
                _ => null
            };
            if (receiver == null)
                return false;
            receiver = StripParenthesesAndSuppressions(receiver);

            if (model.GetSymbolInfo(receiver, ct).Symbol is not ILocalSymbol local || !IsDeclaredIn(local, scope.Node, ct))
                return false;

            if (local.IsRef)
                return false;
            // 普通值类型局部变量是副本，可以随意修改；Span<T> 等 ref struct 可能指向外部内存，需要看初始化表达式
            if (local.Type.IsValueType && !local.Type.IsRefLikeType)
                return true;

            if (local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) is not VariableDeclaratorSyntax declarator)
                return false;
            var init = declarator.Initializer?.Value;
            if (init == null)
                return false;
            init = StripParenthesesAndSuppressions(init);
            bool isFresh = init is ObjectCreationExpressionSyntax
                || init is ImplicitObjectCreationExpressionSyntax
                || init is ArrayCreationExpressionSyntax
                || init is ImplicitArrayCreationExpressionSyntax
                || init is AnonymousObjectCreationExpressionSyntax
                || init is StackAllocArrayCreationExpressionSyntax
                || init is ImplicitStackAllocArrayCreationExpressionSyntax
                || init.Kind().ToString() == "CollectionExpression";
            if (!isFresh)
                return false;

            // 之后被重新赋值过（可能指向共享对象）则不再视为新建对象
            foreach (var assign in scope.Node.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (assign.Left is IdentifierNameSyntax id && id.Identifier.ValueText == local.Name &&
                    SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id, ct).Symbol, local))
                {
                    return false;
                }
            }
            foreach (var arg in scope.Node.DescendantNodes().OfType<ArgumentSyntax>())
            {
                if (!arg.RefKindKeyword.IsKind(SyntaxKind.None) && arg.Expression is IdentifierNameSyntax id2 &&
                    id2.Identifier.ValueText == local.Name &&
                    SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id2, ct).Symbol, local))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsInsideConstructorOf(SyntaxNode node, SemanticModel model, INamedTypeSymbol type, System.Threading.CancellationToken ct)
        {
            for (SyntaxNode? cur = node.Parent; cur != null; cur = cur.Parent)
            {
                if (cur is AnonymousFunctionExpressionSyntax || cur is LocalFunctionStatementSyntax)
                    return false;
                if (cur is ConstructorDeclarationSyntax ctor)
                {
                    return model.GetDeclaredSymbol(ctor, ct) is IMethodSymbol ctorSym &&
                           SymbolEqualityComparer.Default.Equals(ctorSym.ContainingType, type);
                }
                if (cur is MemberDeclarationSyntax)
                    return false;
            }
            return false;
        }

        // =====================================================================
        // 调用
        // =====================================================================

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            var model = context.SemanticModel;
            if (model.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol methodSymbol)
                return;

            var effects = GetEffects(methodSymbol);

            // VTX014 / VTX015：返回 bool 的 [Decides] 方法（返回 false 即失败）
            if ((effects & Effect.Decides) != 0 && methodSymbol.ReturnType.SpecialType == SpecialType.System_Boolean)
                CheckBoolDecides(context, invocation, methodSymbol);

            var scope = FindScope(model, invocation, context.CancellationToken);

            if (scope == null || scope.Kind != ScopeKind.Tx)
            {
                // VTX005：[Suspends] 协程里的等待必须可取消、按游戏时间推进
                if (IsInsideSuspendsMethod(model, invocation, context.CancellationToken))
                    CheckSuspendsWait(context, invocation, methodSymbol);

                // VTX012：[Suspends] 返回的任务被丢弃（事务内已由 VTX003 报告）
                if ((effects & Effect.Suspends) != 0 && IsTaskLike(methodSymbol.ReturnType) && IsDiscarded(invocation))
                {
                    context.ReportDiagnostic(Diagnostic.Create(RuleDiscardedSuspends, invocation.GetLocation(),
                        $"[Suspends] 方法 '{methodSymbol.ContainingType?.Name}.{methodSymbol.Name}' 返回的任务被丢弃了：它抛出的异常会悄悄丢失，也没有人能等它结束。请 await 它，或在末尾调用 .Forget()（异常会通过 GD.PushError 报告）"));
                }
            }

            if (scope == null)
                return;

            string displayName = $"{methodSymbol.ContainingType?.Name}.{methodSymbol.Name}";

            if (IsNonDeterministicMethod(methodSymbol))
            {
                context.ReportDiagnostic(Diagnostic.Create(RuleNonDeterministicCall, invocation.GetLocation(), displayName));
                return;
            }

            if ((effects & Effect.Suspends) != 0)
            {
                if (scope.Kind == ScopeKind.Tx)
                    context.ReportDiagnostic(Diagnostic.Create(RuleNoSuspendInTx, invocation.GetLocation(), displayName));
                else
                    context.ReportDiagnostic(Diagnostic.Create(RulePureViolation, invocation.GetLocation(), $"调用挂起方法 '{displayName}'"));
                return;
            }

            if ((effects & Effect.NoRollback) != 0)
            {
                if (scope.Kind == ScopeKind.Tx)
                    context.ReportDiagnostic(Diagnostic.Create(RuleEffectViolation, invocation.GetLocation(), $"事务上下文内禁止调用不可回滚的方法 '{displayName} [NoRollback]'"));
                else
                    context.ReportDiagnostic(Diagnostic.Create(RulePureViolation, invocation.GetLocation(), $"调用不可回滚的方法 '{displayName}'"));
                return;
            }

            if (methodSymbol.MethodKind == MethodKind.DelegateInvoke)
            {
                if (!IsVerifiedDelegate(model, invocation, scope, context.CancellationToken, out string delegateName))
                {
                    if (scope.Kind == ScopeKind.Tx)
                        context.ReportDiagnostic(Diagnostic.Create(RuleEffectViolation, invocation.GetLocation(),
                            $"事务上下文内禁止调用无法静态验证的委托 '{delegateName}'（请把该参数标为 [Transacts]，或在本上下文内用 lambda 定义它）"));
                    else
                        context.ReportDiagnostic(Diagnostic.Create(RulePureViolation, invocation.GetLocation(),
                            $"调用无法静态验证的委托 '{delegateName}'（请把该参数标为 [Pure]）"));
                }
                return;
            }

            if (scope.Kind == ScopeKind.Tx)
            {
                if (IsTxLike(effects) || (effects & Effect.Pure) != 0 || IsAllowedWithoutAnnotation(methodSymbol, scope, context.CancellationToken))
                    return;
                context.ReportDiagnostic(Diagnostic.Create(RuleEffectViolation, invocation.GetLocation(),
                    $"事务上下文内禁止调用非事务或不可回滚的方法 '{displayName}'"));
            }
            else
            {
                if ((effects & Effect.Pure) != 0 || IsAllowedWithoutAnnotation(methodSymbol, scope, context.CancellationToken))
                    return;
                string why = IsTxLike(effects) ? "调用会修改事务状态的方法" : "调用未标记 [Pure] 的方法";
                context.ReportDiagnostic(Diagnostic.Create(RulePureViolation, invocation.GetLocation(), $"{why} '{displayName}'"));
            }
        }

        /// <summary>委托调用是否可验证：来自带效果特性的参数，或来自本上下文内用 lambda / 带效果方法组初始化且从未被重新赋值的局部变量。</summary>
        private static bool IsVerifiedDelegate(SemanticModel model, InvocationExpressionSyntax invocation, Scope scope, System.Threading.CancellationToken ct, out string name)
        {
            ExpressionSyntax target = invocation.Expression;
            if (target is MemberAccessExpressionSyntax ma && ma.Name.Identifier.ValueText == "Invoke")
            {
                target = ma.Expression;
            }
            else if (target is MemberBindingExpressionSyntax && invocation.Parent is ConditionalAccessExpressionSyntax cond)
            {
                target = cond.Expression;
            }
            target = StripParenthesesAndSuppressions(target);
            name = target.ToString();
            return IsVerifiedDelegateExpression(model, target, scope.Kind, scope, ct);
        }

        /// <summary>
        /// 一个委托表达式在给定上下文（Tx 或 Pure）里调用是否安全：
        /// lambda（函数体会按上下文检查）、带相容效果的方法组、带相容效果特性的参数、
        /// 或在本上下文内用上述之一初始化且从未被重新赋值的局部变量。
        /// </summary>
        private static bool IsVerifiedDelegateExpression(SemanticModel model, ExpressionSyntax expr, ScopeKind kind, Scope? scope, System.Threading.CancellationToken ct)
        {
            expr = StripParenthesesAndSuppressions(expr);
            if (expr is AnonymousFunctionExpressionSyntax || IsNullOrDefaultLiteral(expr))
                return true;

            var info = model.GetSymbolInfo(expr, ct);
            var sym = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
            switch (sym)
            {
                case IMethodSymbol group:
                    return EffectCompatible(GetEffects(group), kind);
                case IParameterSymbol p:
                    return EffectCompatible(OwnEffects(p.OriginalDefinition), kind);
                case ILocalSymbol local when scope != null && IsDeclaredIn(local, scope.Node, ct):
                {
                    if (local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) is not VariableDeclaratorSyntax d || d.Initializer == null)
                        return false;
                    var init = StripParenthesesAndSuppressions(d.Initializer.Value);
                    if (init is IdentifierNameSyntax || init is MemberAccessExpressionSyntax)
                    {
                        // 方法组初始化：Func<int> f = Foo;
                        if (model.GetSymbolInfo(init, ct).Symbol is not IMethodSymbol initGroup || !EffectCompatible(GetEffects(initGroup), kind))
                            return false;
                    }
                    else if (init is not AnonymousFunctionExpressionSyntax)
                    {
                        return false;
                    }
                    foreach (var assign in scope.Node.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                    {
                        if (assign.Left is IdentifierNameSyntax id && id.Identifier.ValueText == local.Name &&
                            SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id, ct).Symbol, local))
                        {
                            return false;
                        }
                    }
                    return true;
                }
                default:
                    return false;
            }
        }

        private static bool EffectCompatible(Effect e, ScopeKind kind) =>
            kind == ScopeKind.Tx ? (IsTxLike(e) || (e & Effect.Pure) != 0) : (e & Effect.Pure) != 0;

        /// <summary>
        /// 传给带 [Transacts]/[Decides]/[Pure] 的委托参数的实参必须可静态验证：
        /// lambda（其函数体按对应上下文检查）、带相容效果的方法组、转发同样标注过的参数、或 null。
        /// </summary>
        private static void AnalyzeArgument(SyntaxNodeAnalysisContext context)
        {
            var arg = (ArgumentSyntax)context.Node;
            var model = context.SemanticModel;

            if (!arg.RefKindKeyword.IsKind(SyntaxKind.None))
            {
                if (arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) || arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword))
                {
                    CheckMutationTarget(context, arg.Expression, arg.GetLocation());
                }
                return;
            }

            var expr = StripParenthesesAndSuppressions(arg.Expression);
            if (expr is AnonymousFunctionExpressionSyntax)
                return;

            if (!TryGetCalledMethodAndParameter(model, arg, context.CancellationToken, out var called, out var parameter))
                return;

            // 1. 参数自身声明了效果：实参必须可静态验证（无论调用点是否在事务内）
            var pe = OwnEffects(parameter!.OriginalDefinition);
            bool txParam = IsTxLike(pe);
            bool pureParam = (pe & Effect.Pure) != 0;
            if (txParam || pureParam)
            {
                var kind = txParam ? ScopeKind.Tx : ScopeKind.Pure;
                var enclosing = FindScope(model, arg, context.CancellationToken);
                if (IsVerifiedDelegateExpression(model, expr, kind, enclosing, context.CancellationToken))
                    return;

                string what = $"传给 {(txParam ? "[Transacts]" : "[Pure]")} 参数 '{parameter.Name}' 的委托 '{expr}' 无法静态验证（请直接在参数位置写 lambda，或给方法组 / 参数加上对应特性）";
                if (txParam)
                    context.ReportDiagnostic(Diagnostic.Create(RuleEffectViolation, arg.GetLocation(), what));
                else
                    context.ReportDiagnostic(Diagnostic.Create(RulePureViolation, arg.GetLocation(), what));
                return;
            }

            // 2. 在事务 / 纯上下文里把委托交给其它方法（LINQ、List.ForEach…）：这些方法通常会立即调用它
            var scope = FindScope(model, arg, context.CancellationToken);
            if (scope == null)
                return;
            string calledName = (called!.ContainingType?.ToDisplayString() ?? string.Empty) + "." + called.Name;
            if (DeferredExecutionApis.Contains(calledName))
                return;
            if (model.GetTypeInfo(expr, context.CancellationToken).ConvertedType is not INamedTypeSymbol { TypeKind: TypeKind.Delegate })
                return;
            if (IsVerifiedDelegateExpression(model, expr, scope.Kind, scope, context.CancellationToken))
                return;

            string msg = $"把无法静态验证的委托 '{expr}' 传给 '{called.ContainingType?.Name}.{called.Name}'（它可能在这里被调用）";
            if (scope.Kind == ScopeKind.Tx)
                context.ReportDiagnostic(Diagnostic.Create(RuleEffectViolation, arg.GetLocation(), "事务上下文内禁止" + msg));
            else
                context.ReportDiagnostic(Diagnostic.Create(RulePureViolation, arg.GetLocation(), msg));
        }

        private static bool IsNullOrDefaultLiteral(ExpressionSyntax expr)
        {
            while (expr is CastExpressionSyntax cast)
                expr = StripParenthesesAndSuppressions(cast.Expression);
            return expr.IsKind(SyntaxKind.NullLiteralExpression) || expr.IsKind(SyntaxKind.DefaultLiteralExpression) || expr is DefaultExpressionSyntax;
        }

        private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
        {
            var creation = (ExpressionSyntax)context.Node;
            var model = context.SemanticModel;
            var type = model.GetTypeInfo(creation, context.CancellationToken).Type as INamedTypeSymbol;
            if (type == null)
                return;

            // Node 等非引用计数的 Godot 对象：回滚后不会被释放，造成泄漏
            if (!InheritsFromName(type, "Godot.GodotObject") || InheritsFromName(type, "Godot.RefCounted"))
                return;

            var scope = FindScope(model, creation, context.CancellationToken);
            if (scope == null)
                return;

            string what = $"创建 Godot 对象 '{type.Name}'（它不是 RefCounted，回滚时不会被释放；请在事务外创建，或放进 Tx.Defer）";
            if (scope.Kind == ScopeKind.Tx)
                context.ReportDiagnostic(Diagnostic.Create(RuleEffectViolation, creation.GetLocation(), "事务上下文内禁止" + what));
            else
                context.ReportDiagnostic(Diagnostic.Create(RulePureViolation, creation.GetLocation(), what));
        }

        private static bool InheritsFromName(ITypeSymbol? type, string fullName)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                if (t.ToDisplayString() == fullName)
                    return true;
            }
            return false;
        }

        private static readonly HashSet<string> PureBclTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.Math",
            "System.MathF",
            "System.Convert",
            "System.String",
            "System.Char",
            "System.Boolean",
            "System.Byte",
            "System.SByte",
            "System.Int16",
            "System.UInt16",
            "System.Int32",
            "System.UInt32",
            "System.Int64",
            "System.UInt64",
            "System.Single",
            "System.Double",
            "System.Decimal",
            "System.Enum",
            "System.Nullable<T>",
            "System.Collections.Generic.KeyValuePair<TKey, TValue>",
            "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler",
            "System.Collections.Generic.EqualityComparer<T>",
            "System.Collections.Generic.Comparer<T>",
            "System.Linq.Enumerable",
            "System.Object",
            "System.TimeSpan",
            "System.DateTime",
            "System.DateTimeOffset",
            "System.Guid",
            "Godot.Mathf",
        };

        /// <summary>BCL 集合上只读的方法（读取允许，修改仍然禁止）。</summary>
        private static readonly HashSet<string> ReadOnlyCollectionMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "Contains", "ContainsKey", "ContainsValue", "TryGetValue", "IndexOf", "LastIndexOf",
            "Exists", "Find", "FindIndex", "FindLast", "FindLastIndex", "TrueForAll", "BinarySearch",
            "GetEnumerator", "ToArray", "GetValueOrDefault",
        };

        private static bool IsAllowedWithoutAnnotation(IMethodSymbol method, Scope scope, System.Threading.CancellationToken ct)
        {
            var original = method.ReducedFrom ?? method.OriginalDefinition;

            if (method.MethodKind == MethodKind.LocalFunction)
            {
                var decl = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct);
                if (decl != null && decl.SyntaxTree == scope.Node.SyntaxTree && scope.Node.Span.Contains(decl.Span))
                    return true;
            }

            var containingType = original.ContainingType;
            if (containingType == null)
                return false;

            string typeFullName = containingType.OriginalDefinition.ToDisplayString();
            if (PureBclTypes.Contains(typeFullName))
                return true;

            if (method.Name is "ToString" or "GetHashCode" or "Equals" or "GetType" or "CompareTo" or "Deconstruct" or "ReferenceEquals")
                return true;

            string ns = containingType.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            if ((ns == "System.Collections.Generic" || ns == "System.Collections.ObjectModel") && ReadOnlyCollectionMethods.Contains(method.Name))
                return true;

            // Godot 值类型（Vector2/3、Color、Rect2 ...）的方法都是纯计算
            if (containingType.IsValueType && (ns == "Godot" || ns.StartsWith("Godot.", StringComparison.Ordinal)))
                return true;

            if (typeFullName == "Godot.StringName" || typeFullName == "Godot.NodePath")
                return true;

            return false;
        }

        private static bool IsNonDeterministicMethod(IMethodSymbol method)
        {
            string typeFull = method.ContainingType?.ToDisplayString() ?? string.Empty;
            switch (typeFull)
            {
                case "System.Random":
                case "System.Security.Cryptography.RandomNumberGenerator":
                case "System.Diagnostics.Stopwatch":
                case "Godot.RandomNumberGenerator":
                case "Godot.Time":
                    return true;
                case "System.Guid":
                    return method.Name == "NewGuid";
                case "Godot.GD":
                    return method.Name is "Randf" or "Randi" or "Randfn" or "RandRange" or "Randomize" or "Seed" or "RandSeed";
                case "System.Environment":
                    return method.Name is "GetTickCount" or "GetTickCount64";
                default:
                    return false;
            }
        }

        private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
        {
            var memberAccess = (MemberAccessExpressionSyntax)context.Node;
            if (context.SemanticModel.GetSymbolInfo(memberAccess, context.CancellationToken).Symbol is not IPropertySymbol propSymbol)
                return;

            string typeFull = propSymbol.ContainingType?.ToDisplayString() ?? string.Empty;
            bool isNonDeterministicProp =
                (typeFull is "System.DateTime" or "System.DateTimeOffset" && propSymbol.Name is "Now" or "UtcNow" or "Today") ||
                (typeFull == "System.Environment" && propSymbol.Name is "TickCount" or "TickCount64") ||
                (typeFull == "System.Diagnostics.Stopwatch" && propSymbol.IsStatic);

            if (!isNonDeterministicProp)
                return;

            if (FindScope(context.SemanticModel, memberAccess, context.CancellationToken) != null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    RuleNonDeterministicCall,
                    memberAccess.GetLocation(),
                    $"{propSymbol.ContainingType?.Name}.{propSymbol.Name}"));
            }
        }

        private static void AnalyzeAwait(SyntaxNodeAnalysisContext context)
        {
            var awaitExpr = (AwaitExpressionSyntax)context.Node;
            var scope = FindScope(context.SemanticModel, awaitExpr, context.CancellationToken);
            if (scope != null && scope.Kind == ScopeKind.Tx)
            {
                context.ReportDiagnostic(Diagnostic.Create(RuleNoSuspendInTx, awaitExpr.AwaitKeyword.GetLocation(), "await"));
            }
        }

        private static void AnalyzeAwaitStatement(SyntaxNodeAnalysisContext context)
        {
            SyntaxToken awaitKeyword = context.Node switch
            {
                CommonForEachStatementSyntax f => f.AwaitKeyword,
                UsingStatementSyntax u => u.AwaitKeyword,
                LocalDeclarationStatementSyntax l => l.AwaitKeyword,
                _ => default
            };
            if (!awaitKeyword.IsKind(SyntaxKind.AwaitKeyword))
                return;

            var scope = FindScope(context.SemanticModel, context.Node, context.CancellationToken);
            if (scope != null && scope.Kind == ScopeKind.Tx)
            {
                context.ReportDiagnostic(Diagnostic.Create(RuleNoSuspendInTx, awaitKeyword.GetLocation(), "await"));
            }
        }

        private static bool IsInsideSuspendsMethod(SemanticModel model, SyntaxNode node, System.Threading.CancellationToken ct)
        {
            for (SyntaxNode? cur = node.Parent; cur != null; cur = cur.Parent)
            {
                if (cur is LocalFunctionStatementSyntax localDecl &&
                    model.GetDeclaredSymbol(localDecl, ct) is IMethodSymbol localSym &&
                    (OwnEffects(localSym) & Effect.Suspends) != 0)
                {
                    return true;
                }
                if (cur is MethodDeclarationSyntax methodDecl)
                {
                    return model.GetDeclaredSymbol(methodDecl, ct) is IMethodSymbol methodSym &&
                           (GetEffects(methodSym) & Effect.Suspends) != 0;
                }
            }
            return false;
        }

        // =====================================================================
        // VTX005 / VTX012：协程（[Suspends]）规则
        // =====================================================================

        private static void CheckSuspendsWait(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, IMethodSymbol method)
        {
            string typeName = method.ContainingType?.ToDisplayString() ?? string.Empty;

            if (method.Name == "ToSignal" && InheritsFromName(method.ContainingType, "Godot.GodotObject"))
            {
                context.ReportDiagnostic(Diagnostic.Create(RuleRawToSignalInSuspends, invocation.GetLocation(),
                    "[Suspends] 方法内禁止调用不可取消的 'ToSignal'（Race 落败或作用域结束时协程会一直挂着），请改用 'TxTask.WaitSignal(obj, signal, ct)'"));
                return;
            }

            if (typeName == "System.Threading.Tasks.Task" && method.Name == "Delay")
            {
                context.ReportDiagnostic(Diagnostic.Create(RuleRawToSignalInSuspends, invocation.GetLocation(),
                    "[Suspends] 方法内禁止使用 'Task.Delay'（按真实时间计时、不受暂停与慢放影响，计时器跑在线程池上），请改用 'TxTask.Sleep(秒, ct)' 或 'TxTask.DelayFrames(帧数, ct)'"));
                return;
            }

            if (typeName != "VTX.TxTask" && (GetEffects(method) & Effect.Suspends) == 0)
                return;

            // 协程手里有取消令牌时，调用 TxTask 的等待 / 组合 API 以及其它 [Suspends] 方法都必须把它传下去
            var available = FindAvailableCancellationToken(context.SemanticModel, invocation, context.CancellationToken);
            if (available == null)
                return;

            bool passesToken = false;
            foreach (var arg in invocation.ArgumentList.Arguments)
            {
                var t = context.SemanticModel.GetTypeInfo(arg.Expression, context.CancellationToken).Type;
                if (t != null && t.ToDisplayString() == "System.Threading.CancellationToken")
                {
                    passesToken = true;
                    break;
                }
            }
            if (passesToken)
                return;

            bool acceptsToken = method.Parameters.Any(IsCancellationTokenParameter) ||
                                method.ContainingType!.GetMembers(method.Name).OfType<IMethodSymbol>().Any(m => m.Parameters.Any(IsCancellationTokenParameter));
            if (!acceptsToken)
                return;

            context.ReportDiagnostic(Diagnostic.Create(RuleRawToSignalInSuspends, invocation.GetLocation(),
                $"调用 '{method.ContainingType?.Name}.{method.Name}' 时没有传入取消令牌 '{available.Name}'：外层被取消（Race 落败、作用域结束、节点释放）时这次等待不会停止。请把 '{available.Name}' 传进去；确实不需要取消时显式传 CancellationToken.None"));
        }

        private static bool IsCancellationTokenParameter(IParameterSymbol p) =>
            p.Type.ToDisplayString() == "System.Threading.CancellationToken";

        /// <summary>从调用处向外找最近的、类型为 CancellationToken 的参数（lambda / 局部函数 / 方法）。</summary>
        private static IParameterSymbol? FindAvailableCancellationToken(SemanticModel model, SyntaxNode node, System.Threading.CancellationToken ct)
        {
            for (SyntaxNode? cur = node.Parent; cur != null; cur = cur.Parent)
            {
                IMethodSymbol? fn = cur switch
                {
                    AnonymousFunctionExpressionSyntax anon => model.GetSymbolInfo(anon, ct).Symbol as IMethodSymbol,
                    LocalFunctionStatementSyntax local => model.GetDeclaredSymbol(local, ct) as IMethodSymbol,
                    MethodDeclarationSyntax method => model.GetDeclaredSymbol(method, ct) as IMethodSymbol,
                    _ => null
                };
                if (fn == null)
                    continue;
                var token = fn.Parameters.FirstOrDefault(IsCancellationTokenParameter);
                if (token != null)
                    return token;
                if (cur is MethodDeclarationSyntax)
                    return null;
            }
            return null;
        }

        private static bool IsTaskLike(ITypeSymbol type)
        {
            string name = type.OriginalDefinition.ToDisplayString();
            return name is "System.Threading.Tasks.Task" or "System.Threading.Tasks.Task<TResult>"
                or "System.Threading.Tasks.ValueTask" or "System.Threading.Tasks.ValueTask<TResult>";
        }

        /// <summary>调用结果被直接丢弃：单独成句，或赋给弃元 _。</summary>
        private static bool IsDiscarded(InvocationExpressionSyntax invocation)
        {
            SyntaxNode node = invocation;
            while (node.Parent is ParenthesizedExpressionSyntax)
                node = node.Parent;
            if (node.Parent is ExpressionStatementSyntax)
                return true;
            return node.Parent is AssignmentExpressionSyntax assign && assign.Right == node &&
                   assign.Left is IdentifierNameSyntax id && id.Identifier.ValueText == "_";
        }

        // =====================================================================
        // VTX014 / VTX015：不抛异常的失败（返回 bool 的 [Decides] 方法）
        // =====================================================================

        private static void CheckBoolDecides(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, IMethodSymbol method)
        {
            var model = context.SemanticModel;
            var ct = context.CancellationToken;
            bool trusted = IsTrustedRuntimeType(method.ContainingType);
            string name = $"{method.ContainingType?.Name}.{method.Name}";

            if (IsDiscarded(invocation))
            {
                string msg = trusted
                    ? $"'{name}' 的返回值被丢弃了：返回 false 时什么也不会发生，事务照常提交。写成 if (!{name}(...)) return false; 让失败传播，或改用会直接中断的 Tx.Check / Tx.Fail"
                    : $"'{name}' 返回 false 表示失败，结果不能丢弃：写成 if (!{method.Name}(...)) return false; 让失败一路传播到 Tx.Try，或用 Tx.Try(state, static s => ...) 把它包成一个能单独回滚的保存点";
                context.ReportDiagnostic(Diagnostic.Create(RuleDiscardedDecides, invocation.GetLocation(), msg));
                return;
            }

            // Tx.Require / Tx.Failed 自己不改任何状态，失败后做别的事是安全的
            if (trusted)
                return;

            if (FindScope(model, invocation, ct) == null)
            {
                context.ReportDiagnostic(Diagnostic.Create(RuleUnrolledFailure, invocation.GetLocation(),
                    $"在事务之外调用了 '{name}'：它返回 false 时，已经做的修改不会回滚（事务外没有 Undo Log）。请在 Tx.Try(...) 里调用它"));
                return;
            }

            if (IsUnsafeFallback(model, invocation, ct))
            {
                context.ReportDiagnostic(Diagnostic.Create(RuleUnrolledFailure, invocation.GetLocation(),
                    $"'{name}' 失败后这里继续执行了别的逻辑，但它失败前做的修改还留着。" +
                    $"要么直接 return false / Tx.Fail 传播失败，要么用 Tx.Try(state, static s => s.{method.Name}(...)) 把它包成保存点（对应 Verse 的 A[] or B[]）"));
            }
        }

        /// <summary>调用结果为 false 时，代码没有传播失败而是继续执行（if 的 false 分支、||、?: 的 false 分支）。</summary>
        private static bool IsUnsafeFallback(SemanticModel model, InvocationExpressionSyntax invocation, System.Threading.CancellationToken ct)
        {
            SyntaxNode cond = invocation;
            while (cond.Parent is ParenthesizedExpressionSyntax)
                cond = cond.Parent;
            bool negated = false;
            if (cond.Parent is PrefixUnaryExpressionSyntax not && not.IsKind(SyntaxKind.LogicalNotExpression))
            {
                negated = true;
                cond = not;
                while (cond.Parent is ParenthesizedExpressionSyntax)
                    cond = cond.Parent;
            }

            switch (cond.Parent)
            {
                case IfStatementSyntax ifs when ifs.Condition == cond:
                {
                    StatementSyntax? falseBranch = negated ? ifs.Statement : ifs.Else?.Statement;
                    return falseBranch == null || !Propagates(model, falseBranch, ct);
                }
                case BinaryExpressionSyntax bin when bin.IsKind(SyntaxKind.LogicalOrExpression) && bin.Left == cond && !negated:
                    return true;
                case ConditionalExpressionSyntax ce when ce.Condition == cond:
                {
                    var falseExpr = negated ? ce.WhenTrue : ce.WhenFalse;
                    return !IsFailureValue(model, falseExpr, ct);
                }
                default:
                    return false;
            }
        }

        private static bool Propagates(SemanticModel model, StatementSyntax statement, System.Threading.CancellationToken ct)
        {
            if (statement is BlockSyntax block)
            {
                if (block.Statements.Count == 0)
                    return false;
                statement = block.Statements[block.Statements.Count - 1];
            }
            switch (statement)
            {
                case ThrowStatementSyntax:
                    return true;
                case ReturnStatementSyntax ret:
                    return ret.Expression != null && IsFailureValue(model, ret.Expression, ct);
                case ExpressionStatementSyntax es when es.Expression is InvocationExpressionSyntax inv:
                    return model.GetSymbolInfo(inv, ct).Symbol is IMethodSymbol m && IsVtxType(m.ContainingType, "Tx") && m.Name == "Fail";
                default:
                    return false;
            }
        }

        private static bool IsFailureValue(SemanticModel model, ExpressionSyntax expr, System.Threading.CancellationToken ct)
        {
            expr = StripParenthesesAndSuppressions(expr);
            if (expr.IsKind(SyntaxKind.FalseLiteralExpression))
                return true;
            return expr is InvocationExpressionSyntax inv &&
                   model.GetSymbolInfo(inv, ct).Symbol is IMethodSymbol m &&
                   IsVtxType(m.ContainingType, "Tx") && m.Name == "Failed";
        }

        // =====================================================================
        // VTX013：后台线程（Task.Run / Parallel / ThreadPool / new Thread / WorkerThreadPool）
        // =====================================================================

        private enum ThreadDispatch
        {
            None,
            Background,
            MainThread,
        }

        private static readonly HashSet<string> ThreadSafeNodeMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            "CallDeferred", "SetDeferred", "CallDeferredThreadGroup", "SetDeferredThreadGroup", "NotifyDeferredThreadGroup",
            "CallThreadSafe", "SetThreadSafe", "NotifyThreadSafe", "GetInstanceId",
        };

        private static void AnalyzeBackgroundName(SyntaxNodeAnalysisContext context)
        {
            var name = (SimpleNameSyntax)context.Node;
            // 快速过滤：只有在 lambda 里才可能跑在后台线程
            bool inLambda = false;
            for (SyntaxNode? p = name.Parent; p != null; p = p.Parent)
            {
                if (p is AnonymousFunctionExpressionSyntax) { inLambda = true; break; }
                if (p is MemberDeclarationSyntax) break;
            }
            if (!inLambda)
                return;
            if (name.Parent is QualifiedNameSyntax || name.Parent is NameColonSyntax || name.Parent is TypeArgumentListSyntax)
                return;

            var model = context.SemanticModel;
            var ct = context.CancellationToken;
            var sym = model.GetSymbolInfo(name, ct).Symbol;
            if (sym is not IMethodSymbol && sym is not IPropertySymbol)
                return;
            if (sym is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.LocalFunction or MethodKind.AnonymousFunction })
                return;

            var bg = FindBackgroundLambda(model, name, ct);
            if (bg == null)
                return;

            ExpressionSyntax? receiver = name.Parent is MemberAccessExpressionSyntax ma && ma.Name == name ? ma.Expression : null;
            var containing = sym.ContainingType;
            string member = $"{containing?.Name}.{sym.Name}";
            string? message = null;

            if (IsVtxType(containing, "TxTask"))
            {
                if (sym is IMethodSymbol && sym.Name is not ("Forget" or "Defer"))
                    message = $"后台线程里调用了 '{member}'：TxTask 的等待登记表只能在主线程使用。先 await 回到主线程（不要 ConfigureAwait(false)），再等待";
            }
            else if (IsVtxType(containing, "TxGodot"))
            {
                message = $"后台线程里调用了 '{member}'：它修改的是 Godot 对象，只能在主线程。把计算结果带回主线程再写入";
            }
            else if (IsVtxType(containing, "Tx"))
            {
                if (sym is IMethodSymbol && sym.Name != "EnterWorkerScope" && !HasWorkerScope(model, bg, ct))
                    message = $"后台线程里调用了 '{member}'：事务只能在主线程执行。如果是在后台线程对只属于这个线程的数据做推演，先写 using var scope = Tx.EnterWorkerScope();";
            }
            else if (sym is IPropertySymbol prop && IsTxFieldProperty(prop))
            {
                if (!HasWorkerScope(model, bg, ct) && !IsFreshReceiver(model, name, bg, ct))
                    message = $"后台线程里访问了 [TxField] 属性 '{member}'：事务状态属于主线程，在这里读写会和主线程发生数据竞争。先在主线程把需要的值拷贝成局部变量再传进来";
            }
            else if (!sym.IsStatic && containing?.ContainingNamespace?.ToDisplayString() == "Godot" && !ThreadSafeNodeMembers.Contains(sym.Name))
            {
                ITypeSymbol? receiverType = receiver != null
                    ? model.GetTypeInfo(receiver, ct).Type
                    : model.GetEnclosingSymbol(name.SpanStart, ct)?.ContainingType;
                if (InheritsFromName(receiverType, "Godot.Node") && !IsFreshReceiver(model, name, bg, ct))
                    message = $"后台线程里访问了节点成员 '{member}'：场景树里的节点只能在主线程访问。先在主线程拷贝需要的数据，算完后 await 回主线程或用 CallDeferred 写回";
            }

            if (message != null)
            {
                Location loc = name.Parent is MemberAccessExpressionSyntax ma2 && ma2.Name == name ? ma2.GetLocation() : name.GetLocation();
                context.ReportDiagnostic(Diagnostic.Create(RuleBackgroundThread, loc, message));
            }
        }

        private static bool IsTxFieldProperty(IPropertySymbol prop)
        {
            var type = prop.ContainingType;
            if (type == null)
                return false;
            foreach (var m in type.GetMembers())
            {
                if (m is IFieldSymbol f && HasVtxAttribute(f, "TxFieldAttribute") && GetTxFieldPropertyName(f) == prop.Name)
                    return true;
            }
            return false;
        }

        /// <summary>接收者是 lambda 里新建的局部对象（还没进场景树、没有共享）。</summary>
        private static bool IsFreshReceiver(SemanticModel model, SimpleNameSyntax name, AnonymousFunctionExpressionSyntax bg, System.Threading.CancellationToken ct)
        {
            if (name.Parent is not MemberAccessExpressionSyntax ma || ma.Name != name)
                return false; // 隐式 this
            return IsFreshLocalReceiver(model, ma, new Scope { Kind = ScopeKind.Tx, Node = bg }, ct);
        }

        private static bool HasWorkerScope(SemanticModel model, AnonymousFunctionExpressionSyntax bg, System.Threading.CancellationToken ct)
        {
            foreach (var inv in bg.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (inv.Expression.ToString().EndsWith("EnterWorkerScope", StringComparison.Ordinal) &&
                    model.GetSymbolInfo(inv, ct).Symbol is IMethodSymbol m && IsVtxType(m.ContainingType, "Tx"))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>向外找：最近的“决定在哪个线程执行”的 lambda。传给后台 API 的返回它；传回主线程（CallDeferred / Post）的返回 null。</summary>
        private static AnonymousFunctionExpressionSyntax? FindBackgroundLambda(SemanticModel model, SyntaxNode node, System.Threading.CancellationToken ct)
        {
            for (SyntaxNode? cur = node.Parent; cur != null; cur = cur.Parent)
            {
                switch (cur)
                {
                    case AnonymousFunctionExpressionSyntax anon:
                        switch (DispatchOf(model, anon, ct))
                        {
                            case ThreadDispatch.Background: return anon;
                            case ThreadDispatch.MainThread: return null;
                        }
                        break;
                    case LocalFunctionStatementSyntax:
                    case MemberDeclarationSyntax:
                        return null;
                }
            }
            return null;
        }

        private static ThreadDispatch DispatchOf(SemanticModel model, AnonymousFunctionExpressionSyntax anon, System.Threading.CancellationToken ct)
        {
            SyntaxNode node = anon;
            for (int hop = 0; hop < 2; hop++)
            {
                while (node.Parent is ParenthesizedExpressionSyntax || node.Parent is CastExpressionSyntax)
                    node = node.Parent;
                if (node.Parent is not ArgumentSyntax arg || arg.Parent?.Parent is not ExpressionSyntax callee)
                    return ThreadDispatch.None;
                if (model.GetSymbolInfo(callee, ct).Symbol is not IMethodSymbol m)
                    return ThreadDispatch.None;

                string type = m.ContainingType?.OriginalDefinition.ToDisplayString() ?? string.Empty;
                if (m.MethodKind == MethodKind.Constructor)
                    return type == "System.Threading.Thread" ? ThreadDispatch.Background : ThreadDispatch.None;

                if ((type == "System.Threading.Tasks.Task" && m.Name == "Run") ||
                    (type.StartsWith("System.Threading.Tasks.TaskFactory", StringComparison.Ordinal) && m.Name == "StartNew") ||
                    (type == "System.Threading.ThreadPool" && m.Name.EndsWith("QueueUserWorkItem", StringComparison.Ordinal)) ||
                    type == "System.Threading.Tasks.Parallel" ||
                    (type == "Godot.WorkerThreadPool" && m.Name is "AddTask" or "AddGroupTask"))
                {
                    return ThreadDispatch.Background;
                }
                if (type == "System.Threading.SynchronizationContext" && m.Name is "Post" or "Send")
                    return ThreadDispatch.MainThread;

                if (type == "Godot.Callable" && m.Name == "From")
                {
                    // Callable.From(...).CallDeferred()：回到主线程；作为参数传给 WorkerThreadPool：看下一层
                    if (callee.Parent is MemberAccessExpressionSyntax next && next.Expression == callee &&
                        next.Name.Identifier.ValueText is "CallDeferred")
                    {
                        return ThreadDispatch.MainThread;
                    }
                    node = callee;
                    continue;
                }
                return ThreadDispatch.None;
            }
            return ThreadDispatch.None;
        }

        private static void AnalyzeAsyncVoid(SyntaxNodeAnalysisContext context)
        {
            var model = context.SemanticModel;
            IMethodSymbol? symbol = context.Node switch
            {
                MethodDeclarationSyntax m => model.GetDeclaredSymbol(m, context.CancellationToken),
                LocalFunctionStatementSyntax l => model.GetDeclaredSymbol(l, context.CancellationToken) as IMethodSymbol,
                AnonymousFunctionExpressionSyntax a => model.GetSymbolInfo(a, context.CancellationToken).Symbol as IMethodSymbol,
                _ => null
            };
            if (symbol == null || !symbol.IsAsync || !symbol.ReturnsVoid)
                return;
            if ((GetEffects(symbol) & Effect.Suspends) == 0)
                return;

            Location location = context.Node switch
            {
                MethodDeclarationSyntax m => m.Identifier.GetLocation(),
                LocalFunctionStatementSyntax l => l.Identifier.GetLocation(),
                _ => context.Node.GetLocation()
            };
            context.ReportDiagnostic(Diagnostic.Create(RuleDiscardedSuspends, location,
                $"[Suspends] 方法 '{symbol.Name}' 不能是 async void：调用方无法等待或取消它，异常也会丢失。请返回 Task"));
        }

        // =====================================================================
        // VTX008：res:// 路径
        // =====================================================================

        private static void AnalyzeStringLiteral(SyntaxNodeAnalysisContext context, Lazy<ProjectFileIndex?> index)
        {
            var literal = (LiteralExpressionSyntax)context.Node;
            string text = literal.Token.ValueText;
            if (!text.StartsWith("res://", StringComparison.Ordinal))
                return;

            // 拼接（"res://levels/level_" + n）、格式串、通配符：只是路径片段，不做存在性检查
            if (literal.Parent is BinaryExpressionSyntax bin && bin.IsKind(SyntaxKind.AddExpression))
                return;
            if (text.IndexOf('{') >= 0 || text.IndexOf('}') >= 0 || text.IndexOf('*') >= 0 || text.IndexOf('%') >= 0)
                return;

            string rel = text.Substring("res://".Length);
            int sub = rel.IndexOf("::", StringComparison.Ordinal);
            if (sub >= 0)
                rel = rel.Substring(0, sub);
            if (string.IsNullOrWhiteSpace(rel) ||
                rel.StartsWith(".", StringComparison.Ordinal) ||
                rel.StartsWith("addons/", StringComparison.Ordinal))
            {
                return;
            }

            var idx = index.Value;
            if (idx == null)
                return;

            switch (idx.Lookup(rel, out string? actualCase))
            {
                case ProjectFileIndex.Match.Exact:
                    return;
                case ProjectFileIndex.Match.CaseMismatch:
                    context.ReportDiagnostic(Diagnostic.Create(RuleInvalidResPath, literal.GetLocation(), text,
                        $"与磁盘上的 'res://{actualCase}' 大小写不一致！Windows 编辑器里能加载，导出到区分大小写的平台（Linux/Android/Web/PCK）后会找不到。"));
                    return;
                default:
                    context.ReportDiagnostic(Diagnostic.Create(RuleInvalidResPath, literal.GetLocation(), text,
                        "在项目磁盘中不存在！请使用编译期生成的 'VTX.Res' 强类型常量，或检查路径拼写。"));
                    return;
            }
        }

        /// <summary>项目文件索引（带短期缓存，避免每个字符串字面量都访问磁盘；编译器服务器常驻进程下 5 秒后自动刷新）。</summary>
        internal sealed class ProjectFileIndex
        {
            public enum Match
            {
                Missing,
                Exact,
                CaseMismatch,
            }

            private static readonly ConcurrentDictionary<string, ProjectFileIndex> Cache = new ConcurrentDictionary<string, ProjectFileIndex>(StringComparer.Ordinal);
            private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);

            private readonly HashSet<string> _exact = new HashSet<string>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _ignoreCase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private DateTime _builtAtUtc;

            public static ProjectFileIndex? Get(string projectDir)
            {
                if (!Directory.Exists(projectDir))
                    return null;
                if (Cache.TryGetValue(projectDir, out var cached) && DateTime.UtcNow - cached._builtAtUtc < MaxAge)
                    return cached;
                var fresh = Build(projectDir);
                Cache[projectDir] = fresh;
                return fresh;
            }

            private static ProjectFileIndex Build(string projectDir)
            {
                var idx = new ProjectFileIndex { _builtAtUtc = DateTime.UtcNow };
                string root = projectDir.TrimEnd('/', '\\');
                var pending = new Stack<string>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    string dir = pending.Pop();
                    IEnumerable<string> entries;
                    try
                    {
                        entries = Directory.EnumerateFileSystemEntries(dir).ToList();
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }

                    foreach (var entry in entries)
                    {
                        string name = Path.GetFileName(entry);
                        string rel = entry.Substring(root.Length).TrimStart('/', '\\').Replace('\\', '/');
                        bool isDir = Directory.Exists(entry);
                        if (isDir && (name == ".godot" || name == ".git" || name == ".vs" || name == ".idea"))
                            continue;
                        idx._exact.Add(rel);
                        if (!idx._ignoreCase.ContainsKey(rel))
                            idx._ignoreCase[rel] = rel;
                        if (isDir)
                            pending.Push(entry);
                    }
                }
                return idx;
            }

            public Match Lookup(string relativePath, out string? actual)
            {
                string rel = relativePath.TrimEnd('/');
                actual = null;
                if (rel.Length == 0 || _exact.Contains(rel))
                    return Match.Exact;
                if (_ignoreCase.TryGetValue(rel, out var found))
                {
                    actual = found;
                    return Match.CaseMismatch;
                }
                return Match.Missing;
            }
        }

        // =====================================================================
        // 工具
        // =====================================================================

        internal static string? TryGetProjectDir(AnalyzerConfigOptions opts)
        {
            if (opts.TryGetValue("build_property.GodotProjectDirBase64", out string? b64) && !string.IsNullOrEmpty(b64))
            {
                try
                {
                    return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                }
                catch (FormatException)
                {
                }
            }

            if (opts.TryGetValue("build_property.GodotProjectDir", out string? dir) && !string.IsNullOrEmpty(dir))
            {
                return dir;
            }

            return null;
        }

        private static bool IsVtxType(INamedTypeSymbol? type, string name) =>
            type != null && type.Name == name && type.ContainingNamespace?.ToDisplayString() == "VTX";

        private static bool HasVtxAttribute(ISymbol symbol, string attributeClassName)
        {
            foreach (var attr in symbol.GetAttributes())
            {
                if (IsVtxType(attr.AttributeClass, attributeClassName))
                    return true;
            }
            return false;
        }

        private static string GetTxFieldPropertyName(IFieldSymbol fieldSymbol)
        {
            foreach (var attr in fieldSymbol.GetAttributes())
            {
                if (IsVtxType(attr.AttributeClass, "TxFieldAttribute") &&
                    attr.ConstructorArguments.Length > 0 &&
                    attr.ConstructorArguments[0].Value is string custom &&
                    !string.IsNullOrWhiteSpace(custom))
                {
                    return custom;
                }
            }
            return TxFieldGenerator.ComputePropertyName(fieldSymbol.Name);
        }
    }
}
