/*
    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// ICSharpCode.Decompiler 11.1 can declare a local function F that is nested in another local function G next to G,
	/// when F captures G's parameters in a struct closure and other variables in a class closure (Roslyn's
	/// <c>AnalyzerDriver.ApplyProgrammaticSuppressionsCore</c>). F then reads the struct closure's fields through its hidden
	/// <c>ref</c> parameter, <c>foreach (Diagnostic d in P_1.reportedDiagnostics)</c>, which doesn't compile (CS0103).
	/// When all calls to F are in G and each field read is one of G's parameters (same name in the metadata and same type),
	/// F is moved to the end of G's body and <c>P_1.field</c> becomes the parameter.
	/// </summary>
	sealed class NestedLocalFunctionTransform : IAstTransform {
		public void Run(AstNode rootNode, TransformContext context) {
			foreach (var f in rootNode.Descendants.OfType<LocalFunctionDeclarationStatement>().ToArray())
				TryMove(f);
		}

		static void TryMove(LocalFunctionDeclarationStatement f) {
			if (!(f.Annotation<ILFunction>() is ILFunction fFunc) || !(f.Parent is BlockStatement) || !(f.Ancestors.OfType<EntityDeclaration>().FirstOrDefault() is EntityDeclaration member))
				return;
			var closureReads = new List<(MemberReferenceExpression read, IdentifierExpression closure, IField field)>();
			foreach (var id in f.Declaration.Body.Descendants.OfType<IdentifierExpression>()) {
				if (!(id.GetILVariable() is ILVariable v) || v.Kind != VariableKind.Parameter || v.Function != fFunc || !IsDisplayStructRef(v.Type))
					continue;
				if (!(id.Parent is MemberReferenceExpression read) || read.Target != id || !(read.GetSymbol() is IField field))
					return;
				closureReads.Add((read, id, field));
			}
			if (closureReads.Count == 0)
				return;

			// A local function can only be used in its member
			var calls = member.Descendants.OfType<IdentifierExpression>().Where(a => a.Identifier == f.Declaration.Name && !a.Ancestors.Contains(f) && RefersTo(a, fFunc)).ToArray();
			if (calls.Length == 0)
				return;
			foreach (var g in calls[0].Ancestors.OfType<LocalFunctionDeclarationStatement>()) {
				if (!calls.All(a => a.Ancestors.Contains(g)))
					continue;
				if (!g.Ancestors.Contains(f.Parent) || !(g.Annotation<ILFunction>() is ILFunction gFunc))
					continue;
				var parameters = new Dictionary<IField, ILVariable>();
				foreach (var (_, _, field) in closureReads) {
					if (!parameters.ContainsKey(field) && FindParameter(gFunc, field) is ILVariable p)
						parameters.Add(field, p);
				}
				if (parameters.Count != closureReads.Select(a => a.field).Distinct().Count())
					continue;
				if (HasNameConflict(f, g, parameters.Values))
					return;

				foreach (var (read, closure, field) in closureReads) {
					var p = parameters[field];
					var id = new IdentifierExpression(p.Name);
					id.AddAnnotation(new ILVariableResolveResult(p, p.Type));
					foreach (var annotation in read.Annotations.Concat(closure.Annotations)) {
						if (!(annotation is ResolveResult))
							id.AddAnnotation(annotation);
					}
					read.ReplaceWith(id);
				}
				f.Remove();
				g.Declaration.Body.Statements.Add(f);
				return;
			}
		}

		// Roslyn's struct closures, eg. <>c__DisplayClass112_1
		static bool IsDisplayStructRef(IType type) =>
			type is ByReferenceType byRef && byRef.ElementType.GetDefinition() is ITypeDefinition td &&
			td.Kind == TypeKind.Struct && td.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal);

		// A reference to another local function with the same name (in another scope) isn't a call to F. When the
		// symbol can't be found, it's treated as one, so F isn't moved away from it.
		static bool RefersTo(IdentifierExpression id, ILFunction fFunc) {
			var symbol = (id.Parent is InvocationExpression invocation && invocation.Target == id ? invocation.GetSymbol() : null) ?? id.GetSymbol();
			if (!(symbol is IMethod method) || fFunc.Method is null)
				return true;
			return method.MetadataToken.IsNil || method.MetadataToken == fFunc.Method.MetadataToken;
		}

		// Roslyn names the closure's fields after the captured variables. Only G's own parameters, not its captured ones.
		static ILVariable? FindParameter(ILFunction gFunc, IField field) {
			if (gFunc.Method is not IMethod method)
				return null;
			foreach (var v in gFunc.Variables) {
				if (v.Kind != VariableKind.Parameter || v.Index is not int index || index < 0 || index >= method.Parameters.Count)
					continue;
				var parameter = method.Parameters[index];
				if (parameter.Name == field.Name && !IsDisplayStructRef(parameter.Type) && SameType(parameter.Type, field.Type) && SameType(v.Type, field.Type))
					return v;
			}
			return null;
		}

		static readonly RemoveNullabilityVisitor ignoreNullability = new RemoveNullabilityVisitor();

		// The closure field has no nullability attributes
		static bool SameType(IType a, IType b) => a.AcceptVisitor(ignoreNullability).Equals(b.AcceptVisitor(ignoreNullability));

		// After the move, F's names must still mean the same: F can't declare a parameter's name, and G can't declare a
		// name F uses from outside.
		static bool HasNameConflict(LocalFunctionDeclarationStatement f, LocalFunctionDeclarationStatement g, IEnumerable<ILVariable> parameters) {
			var declaredInF = new HashSet<string>(DeclaredNames(f));
			if (parameters.Any(a => declaredInF.Contains(a.Name)))
				return true;
			var declaredInG = new HashSet<string>(DeclaredNames(g));
			declaredInG.Remove(g.Declaration.Name);
			var usedInF = f.Declaration.Body.Descendants.OfType<IdentifierExpression>().Select(a => a.Identifier).Where(a => !declaredInF.Contains(a));
			return usedInF.Any(a => declaredInG.Contains(a) && !parameters.Any(p => p.Name == a));
		}

		static IEnumerable<string> DeclaredNames(AstNode node) {
			foreach (var id in node.DescendantsAndSelf.OfType<Identifier>()) {
				switch (id.Parent) {
				case VariableInitializer:
				case ParameterDeclaration:
				case SingleVariableDesignation:
				case CatchClause:
				case MethodDeclaration:
				case QueryFromClause:
				case QueryLetClause:
				case QueryJoinClause:
				case QueryContinuationClause:
					yield return id.Name;
					break;
				}
			}
		}
	}
}
