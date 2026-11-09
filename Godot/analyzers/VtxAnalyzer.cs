using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Godot.SourceGenerators
{
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
            messageFormat: "事务上下文内禁止调用非事务或不可回滚的方法 '{0}'。请调用标记了 [Transacts]/[Decides]/[Pure] 的方法，或通过 TxGodot/Tx.Defer 延迟执行。",
            category: "VTX",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor RuleNoSuspendInTx = new DiagnosticDescriptor(
            id: "VTX003",
            title: "事务必须在单帧同步完成，禁止跨帧挂起",
            messageFormat: "事务上下文必须在单帧内同步执行完毕，禁止使用 '{0}'（await 或 [Suspends] 异步挂起）。",
            category: "VTX",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor RuleDirectTxFieldWrite = new DiagnosticDescriptor(
            id: "VTX004",
            title: "禁止直接修改 [TxField] 底层私有字段",
            messageFormat: "字段 '{0}' 标记了 [TxField]，禁止直接写入底层字段（会绕过事务 Undo Log），请改用生成的属性 '{1}'。",
            category: "VTX",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor RuleRawToSignalInSuspends = new DiagnosticDescriptor(
            id: "VTX005",
            title: "[Suspends] 结构化并发内禁止使用不可取消的 ToSignal",
            messageFormat: "[Suspends] 方法内禁止调用不可取消的 'ToSignal'（会导致 Race/作用域取消时协程泄漏），请改用 'TxTask.WaitSignal(obj, signal, ct)'。",
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

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(
                RuleNonTxMutation,
                RuleEffectViolation,
                RuleNoSuspendInTx,
                RuleDirectTxFieldWrite,
                RuleRawToSignalInSuspends,
                RuleMutableCollectionInTxField);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterSyntaxNodeAction(AnalyzeFieldDeclaration, SyntaxKind.FieldDeclaration);
            context.RegisterSyntaxNodeAction(AnalyzeMethodDeclaration, SyntaxKind.MethodDeclaration);
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
            context.RegisterSyntaxNodeAction(AnalyzeAwait, SyntaxKind.AwaitExpression);
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

                if (!HasAttribute(fieldSym, "TxFieldAttribute", "TxField"))
                    continue;

                var type = fieldSym.Type;
                if (type is IArrayTypeSymbol)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        RuleMutableCollectionInTxField,
                        variable.Identifier.GetLocation(),
                        fieldSym.Name,
                        type.ToDisplayString()));
                }
                else if (type is INamedTypeSymbol namedType)
                {
                    string fullDef = namedType.OriginalDefinition.ToDisplayString();
                    if (fullDef is
                        "System.Collections.Generic.List<T>" or
                        "System.Collections.Generic.Dictionary<TKey, TValue>" or
                        "System.Collections.Generic.HashSet<T>" or
                        "System.Collections.Generic.Queue<T>" or
                        "System.Collections.Generic.Stack<T>")
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            RuleMutableCollectionInTxField,
                            variable.Identifier.GetLocation(),
                            fieldSym.Name,
                            namedType.ToDisplayString()));
                    }
                }
            }
        }

        private static void AnalyzeMethodDeclaration(SyntaxNodeAnalysisContext context)
        {
            var methodSyntax = (MethodDeclarationSyntax)context.Node;
            if (context.SemanticModel.GetDeclaredSymbol(methodSyntax, context.CancellationToken) is not IMethodSymbol methodSymbol)
                return;

            bool isTx = HasTxEffect(methodSymbol);
            bool isSuspends = HasAttribute(methodSymbol, "SuspendsAttribute", "Suspends");

            if (isTx && (isSuspends || methodSymbol.IsAsync))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    RuleNoSuspendInTx,
                    methodSyntax.Identifier.GetLocation(),
                    methodSymbol.Name));
            }
        }

        private static void AnalyzeAssignment(SyntaxNodeAnalysisContext context)
        {
            var assignment = (AssignmentExpressionSyntax)context.Node;
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
            var symbolInfo = context.SemanticModel.GetSymbolInfo(target, context.CancellationToken);
            var symbol = symbolInfo.Symbol;

            // 1. Always check direct write to [TxField] backing field in non-generated code
            if (symbol is IFieldSymbol fieldSym && HasAttribute(fieldSym, "TxFieldAttribute", "TxField"))
            {
                string propName = GetTxFieldPropertyName(fieldSym);
                context.ReportDiagnostic(Diagnostic.Create(
                    RuleDirectTxFieldWrite,
                    reportLocation,
                    fieldSym.Name,
                    propName));
                return;
            }

            // 2. Check if we are inside a transactional scope
            SyntaxNode? txScopeNode = FindEnclosingTransactionalScope(context.SemanticModel, target);
            if (txScopeNode == null)
                return;

            // Element access: array[i] = val or indexer[i] = val
            if (target is ElementAccessExpressionSyntax elementAccess)
            {
                if (symbol is IPropertySymbol indexerSymbol)
                {
                    if (indexerSymbol.SetMethod != null && HasTxEffect(indexerSymbol.SetMethod))
                        return;

                    context.ReportDiagnostic(Diagnostic.Create(
                        RuleNonTxMutation,
                        reportLocation,
                        indexerSymbol.ContainingType.Name + "[]"));
                    return;
                }

                // Raw array element mutation (T[]) inside transaction
                context.ReportDiagnostic(Diagnostic.Create(
                    RuleNonTxMutation,
                    reportLocation,
                    elementAccess.ToString()));
                return;
            }

            if (symbol is ILocalSymbol localSym)
            {
                var declSyntax = localSym.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken);
                if (declSyntax != null && !txScopeNode.Span.Contains(declSyntax.Span))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        RuleNonTxMutation,
                        reportLocation,
                        localSym.Name + " (外部捕获局部变量)"));
                }
                return;
            }

            if (symbol is IParameterSymbol paramSym)
            {
                var declSyntax = paramSym.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken);
                if (declSyntax != null && !txScopeNode.Span.Contains(declSyntax.Span))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        RuleNonTxMutation,
                        reportLocation,
                        paramSym.Name + " (外部捕获参数)"));
                }
                return;
            }

            if (symbol is IPropertySymbol propSym)
            {
                if (propSym.SetMethod != null && HasTxEffect(propSym.SetMethod))
                    return;

                context.ReportDiagnostic(Diagnostic.Create(
                    RuleNonTxMutation,
                    reportLocation,
                    $"{propSym.ContainingType?.Name}.{propSym.Name}"));
                return;
            }

            if (symbol is IFieldSymbol nonTxField)
            {
                if (target is MemberAccessExpressionSyntax memberAccess)
                {
                    var rootLocal = GetRootSymbol(context.SemanticModel, memberAccess.Expression);
                    if (rootLocal is ILocalSymbol rootLocalSym && rootLocalSym.Type.IsValueType)
                    {
                        var declSyntax = rootLocalSym.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken);
                        if (declSyntax != null && txScopeNode.Span.Contains(declSyntax.Span))
                            return;
                    }
                }

                context.ReportDiagnostic(Diagnostic.Create(
                    RuleNonTxMutation,
                    reportLocation,
                    $"{nonTxField.ContainingType?.Name}.{nonTxField.Name}"));
            }
        }

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol methodSymbol)
                return;

            // Check VTX005: ToSignal inside [Suspends]
            if (methodSymbol.Name == "ToSignal" && IsInsideSuspendsMethod(context.SemanticModel, invocation))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    RuleRawToSignalInSuspends,
                    invocation.GetLocation()));
            }

            // Check if inside transactional context
            SyntaxNode? txScopeNode = FindEnclosingTransactionalScope(context.SemanticModel, invocation);
            if (txScopeNode == null)
                return;

            // Check VTX003: Calling [Suspends] method inside transaction
            if (HasAttribute(methodSymbol, "SuspendsAttribute", "Suspends"))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    RuleNoSuspendInTx,
                    invocation.GetLocation(),
                    $"{methodSymbol.ContainingType?.Name}.{methodSymbol.Name}"));
                return;
            }

            // Check VTX002: Calling [NoRollback] or non-transactional method
            if (HasAttribute(methodSymbol, "NoRollbackAttribute", "NoRollback"))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    RuleEffectViolation,
                    invocation.GetLocation(),
                    $"{methodSymbol.ContainingType?.Name}.{methodSymbol.Name} [NoRollback]"));
                return;
            }

            if (IsAllowedInTransaction(methodSymbol, txScopeNode))
                return;

            context.ReportDiagnostic(Diagnostic.Create(
                RuleEffectViolation,
                invocation.GetLocation(),
                $"{methodSymbol.ContainingType?.Name}.{methodSymbol.Name}"));
        }

        private static void AnalyzeAwait(SyntaxNodeAnalysisContext context)
        {
            var awaitExpr = (AwaitExpressionSyntax)context.Node;
            SyntaxNode? txScopeNode = FindEnclosingTransactionalScope(context.SemanticModel, awaitExpr);
            if (txScopeNode != null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    RuleNoSuspendInTx,
                    awaitExpr.AwaitKeyword.GetLocation(),
                    "await"));
            }
        }

        private static bool IsAllowedInTransaction(IMethodSymbol method, SyntaxNode txScopeNode)
        {
            var original = method.ReducedFrom ?? method.OriginalDefinition;

            if (HasTxEffect(method) || HasTxEffect(original) ||
                HasAttribute(method, "PureAttribute", "Pure") ||
                HasAttribute(original, "PureAttribute", "Pure"))
            {
                return true;
            }

            if (method.MethodKind == MethodKind.LocalFunction)
            {
                var decl = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
                if (decl != null && txScopeNode.Span.Contains(decl.Span))
                    return true;
            }

            if (method.MethodKind == MethodKind.DelegateInvoke)
                return true;

            var containingType = method.ContainingType;
            if (containingType == null)
                return false;

            string typeFullName = containingType.OriginalDefinition.ToDisplayString();

            if (typeFullName is "VTX.Tx" or "VTX.TxGodot")
                return true;

            if (typeFullName is
                "System.Math" or
                "System.MathF" or
                "System.Convert" or
                "System.String" or
                "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler" or
                "System.Collections.Generic.EqualityComparer<T>" or
                "System.Collections.Generic.Comparer<T>" or
                "System.Enum")
            {
                return true;
            }

            if (method.Name is "ToString" or "GetHashCode" or "Equals" or "GetType")
                return true;

            if (containingType.IsValueType && typeFullName.StartsWith("Godot."))
                return true;

            if (typeFullName == "Godot.StringName")
                return true;

            return false;
        }

        private static SyntaxNode? FindEnclosingTransactionalScope(SemanticModel model, SyntaxNode node)
        {
            for (SyntaxNode? cur = node.Parent; cur != null; cur = cur.Parent)
            {
                if (cur is AnonymousFunctionExpressionSyntax anonFunc)
                {
                    if (anonFunc.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax inv } })
                    {
                        if (model.GetSymbolInfo(inv).Symbol is IMethodSymbol invSym &&
                            invSym.ContainingType?.ToDisplayString() == "VTX.Tx")
                        {
                            if (invSym.Name is "Defer" or "Record")
                                return null;

                            if (invSym.Name is "Try" or "Simulate" or "FirstOf" or "WhereCan")
                                return anonFunc;
                        }
                    }

                    if (model.GetSymbolInfo(anonFunc).Symbol is IMethodSymbol lambdaSym && HasTxEffect(lambdaSym))
                        return anonFunc;
                }
                else if (cur is LocalFunctionStatementSyntax localFunc)
                {
                    if (model.GetDeclaredSymbol(localFunc) is IMethodSymbol localSym && HasTxEffect(localSym))
                        return localFunc;
                }
                else if (cur is MethodDeclarationSyntax methodDecl)
                {
                    if (model.GetDeclaredSymbol(methodDecl) is IMethodSymbol methodSym)
                    {
                        if (IsTrustedRuntimeType(methodSym.ContainingType))
                            return null;
                        if (HasTxEffect(methodSym))
                            return methodDecl;
                    }
                    return null;
                }
                else if (cur is AccessorDeclarationSyntax accessorDecl)
                {
                    if (model.GetDeclaredSymbol(accessorDecl) is IMethodSymbol accessorSym)
                    {
                        if (IsTrustedRuntimeType(accessorSym.ContainingType))
                            return null;
                        if (HasTxEffect(accessorSym))
                            return accessorDecl;
                    }
                    return null;
                }
            }

            return null;
        }

        private static bool IsTrustedRuntimeType(INamedTypeSymbol? type)
        {
            if (type == null)
                return false;
            string ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            return ns == "VTX" && type.Name is "Tx" or "TxArray" or "TxList" or "TxDict" or "TxGodot" or "TxTask" or "TxScope";
        }

        private static bool IsInsideSuspendsMethod(SemanticModel model, SyntaxNode node)
        {
            for (SyntaxNode? cur = node.Parent; cur != null; cur = cur.Parent)
            {
                if (cur is MethodDeclarationSyntax methodDecl &&
                    model.GetDeclaredSymbol(methodDecl) is IMethodSymbol methodSym)
                {
                    return HasAttribute(methodSym, "SuspendsAttribute", "Suspends");
                }
                if (cur is LocalFunctionStatementSyntax localDecl &&
                    model.GetDeclaredSymbol(localDecl) is IMethodSymbol localSym &&
                    HasAttribute(localSym, "SuspendsAttribute", "Suspends"))
                {
                    return true;
                }
            }
            return false;
        }

        private static ISymbol? GetRootSymbol(SemanticModel model, ExpressionSyntax expr)
        {
            while (expr is MemberAccessExpressionSyntax ma)
                expr = ma.Expression;
            return model.GetSymbolInfo(expr).Symbol;
        }

        private static bool HasTxEffect(IMethodSymbol symbol)
        {
            return HasAttribute(symbol, "TransactsAttribute", "Transacts") ||
                   HasAttribute(symbol, "DecidesAttribute", "Decides") ||
                   HasAttribute(symbol, "TxInvariantAttribute", "TxInvariant");
        }

        private static bool HasAttribute(ISymbol symbol, string attrNameFull, string attrNameShort)
        {
            foreach (var attr in symbol.GetAttributes())
            {
                var name = attr.AttributeClass?.Name;
                if (name == attrNameFull || name == attrNameShort)
                    return true;
            }
            return false;
        }

        private static string GetTxFieldPropertyName(IFieldSymbol fieldSymbol)
        {
            foreach (var attr in fieldSymbol.GetAttributes())
            {
                if (attr.AttributeClass?.Name is "TxFieldAttribute" or "TxField")
                {
                    if (attr.ConstructorArguments.Length > 0 &&
                        attr.ConstructorArguments[0].Value is string custom &&
                        !string.IsNullOrWhiteSpace(custom))
                    {
                        return custom;
                    }
                }
            }
            return TxFieldGenerator.ComputePropertyName(fieldSymbol.Name);
        }
    }
}
