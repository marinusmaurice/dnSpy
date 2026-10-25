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
	/// ICSharpCode.Decompiler 11.1 can write a pointer <c>stackalloc</c> as an assignment to a local declared earlier, often
	/// typed <c>void*</c>, eg. when it duplicates the code of a <c>fixed</c> statement (Roslyn's <c>CodeAnalysisEventSource</c>:
	/// <c>void* ptr2; ... ptr2 = stackalloc EventData[4];</c> in two blocks). That doesn't compile (CS8346): a pointer
	/// <c>stackalloc</c> must be a local's initializer, and of the element's pointer type. When each use of the local comes
	/// after one of the assignments in the same block (or a block in it), each assignment becomes the declaration
	/// <c>EventData* ptr2 = stackalloc EventData[4];</c>, and casts of the local to that type are removed.
	/// </summary>
	sealed class StackAllocPointerLocalTransform : IAstTransform {
		public void Run(AstNode rootNode, TransformContext context) {
			foreach (var decl in rootNode.Descendants.OfType<VariableDeclarationStatement>().ToArray()) {
				if (decl.Variables.Count != 1 || !(decl.Type is ComposedType { PointerRank: > 0, HasRefSpecifier: false }) || !(decl.Parent is BlockStatement declBlock))
					continue;
				var vi = decl.Variables.Single();
				if (!(vi.GetILVariable() is ILVariable variable) || !(variable.Kind == VariableKind.Local || variable.Kind == VariableKind.StackSlot))
					continue;
				if (vi.Initializer is null)
					SplitDeclaration(decl, declBlock, variable);
				else
					Retype(decl, variable);
			}
		}

		// `void* p = stackalloc T[n];` => `T* p = stackalloc T[n];`
		static void Retype(VariableDeclarationStatement decl, ILVariable variable) {
			if (!(decl.Variables.Single().Initializer is StackAllocExpression stackAlloc) || !IsVoidPointer(variable.Type))
				return;
			if (!(decl.Parent is BlockStatement block) || !(GetElementType(stackAlloc) is IType elementType))
				return;
			var refs = block.Descendants.OfType<IdentifierExpression>().Where(a => a.GetILVariable() == variable).ToArray();
			if (refs.Any(a => !IsPlainRead(a)))
				return;
			var pointerType = new PointerType(elementType);
			decl.Type = CreatePointerType(stackAlloc);
			decl.Variables.Single().RemoveAnnotations<ILVariableResolveResult>();
			decl.Variables.Single().AddAnnotation(new ILVariableResolveResult(variable, pointerType));
			UpdateReferences(refs, variable, pointerType);
		}

		static void SplitDeclaration(VariableDeclarationStatement decl, BlockStatement declBlock, ILVariable variable) {
			if (declBlock.Descendants.OfType<GotoStatement>().Any())
				return;
			var refs = declBlock.Descendants.OfType<IdentifierExpression>().Where(a => a.GetILVariable() == variable).ToArray();
			// Every assignment must be a `v = stackalloc T[n];` statement in a block
			var assignments = new List<ExpressionStatement>();
			IType? elementType = null;
			foreach (var r in refs) {
				if (!(r.Parent is AssignmentExpression assignment) || assignment.Left != r)
					continue;
				if (assignment.Operator != AssignmentOperatorType.Assign || !(assignment.Right is StackAllocExpression stackAlloc) ||
					!(assignment.Parent is ExpressionStatement stmt) || !(stmt.Parent is BlockStatement))
					return;
				if (!(GetElementType(stackAlloc) is IType type) || (elementType is not null && !elementType.Equals(type)))
					return;
				elementType = type;
				assignments.Add(stmt);
			}
			if (assignments.Count == 0 || elementType is null)
				return;
			var pointerType = new PointerType(elementType);
			if (!IsVoidPointer(variable.Type) && !variable.Type.Equals(pointerType))
				return;
			// Each declaration's scope is its whole block, so no assignment's block may contain another assignment
			foreach (var a in assignments) {
				foreach (var b in assignments) {
					if (a != b && b.Ancestors.Contains(a.Parent))
						return;
				}
			}
			var reads = refs.Where(a => !(a.Parent?.Parent is ExpressionStatement stmt && assignments.Contains(stmt))).ToArray();
			foreach (var r in reads) {
				if (!IsPlainRead(r) || r.Ancestors.TakeWhile(a => a != declBlock).Any(a => a is LambdaExpression || a is AnonymousMethodExpression || a is LocalFunctionDeclarationStatement))
					return;
				if (FindAssignmentBefore(r, declBlock, assignments) is null)
					return;
			}

			UpdateReferences(reads, variable, IsVoidPointer(variable.Type) ? pointerType : variable.Type);
			foreach (var stmt in assignments) {
				var assignment = (AssignmentExpression)stmt.Expression;
				var stackAlloc = (StackAllocExpression)assignment.Right;
				var newDecl = new VariableDeclarationStatement(CreatePointerType(stackAlloc), variable.Name, stackAlloc.Detach());
				var newVi = newDecl.Variables.Single();
				newVi.AddAnnotation(new ILVariableResolveResult(variable, pointerType));
				foreach (var annotation in assignment.Left.Annotations.Concat(assignment.Annotations)) {
					if (!(annotation is ResolveResult))
						newVi.AddAnnotation(annotation);
				}
				foreach (var annotation in stmt.Annotations) {
					if (!(annotation is ResolveResult))
						newDecl.AddAnnotation(annotation);
				}
				stmt.ReplaceWith(newDecl);
			}
			decl.Remove();
		}

		// The assignment statement that comes before the node in its block or a block around it, inside the declaration's block
		static ExpressionStatement? FindAssignmentBefore(AstNode node, BlockStatement declBlock, List<ExpressionStatement> assignments) {
			for (var n = node; n is not null && n != declBlock; n = n.Parent) {
				if (!(n is Statement && n.Parent is BlockStatement))
					continue;
				for (var prev = n.PrevSibling; prev is not null; prev = prev.PrevSibling) {
					if (prev is ExpressionStatement stmt && assignments.Contains(stmt))
						return stmt;
				}
			}
			return null;
		}

		// Not `ref v`/`out v`/`&v`, which need the declared type
		static bool IsPlainRead(IdentifierExpression id) =>
			!(id.Parent is DirectionExpression) && !(id.Parent is UnaryOperatorExpression { Operator: UnaryOperatorType.AddressOf }) &&
			!(id.Parent is AssignmentExpression assignment && assignment.Left == id);

		static void UpdateReferences(IdentifierExpression[] refs, ILVariable variable, IType pointerType) {
			foreach (var r in refs) {
				r.RemoveAnnotations<ILVariableResolveResult>();
				r.AddAnnotation(new ILVariableResolveResult(variable, pointerType));
				// `(T*)v` => `v`
				if (r.Parent is CastExpression cast && cast.Type.GetResolveResult()?.Type is IType castType && castType.Equals(pointerType)) {
					var annotations = cast.Annotations.Where(a => !(a is ResolveResult)).ToArray();
					r.Remove();
					foreach (var annotation in annotations)
						r.AddAnnotation(annotation);
					cast.ReplaceWith(r);
				}
			}
		}

		// System.Void can be a struct definition here (from the referenced corlib's metadata), so not only TypeKind.Void
		static bool IsVoidPointer(IType type) => type is PointerType pointer && pointer.ElementType.IsKnownType(KnownTypeCode.Void);

		static IType? GetElementType(StackAllocExpression stackAlloc) {
			var type = stackAlloc.Type.GetResolveResult()?.Type;
			if (type is null || type.Kind == TypeKind.Unknown)
				type = (stackAlloc.GetResolveResult()?.Type as PointerType)?.ElementType;
			return type is null || type.Kind == TypeKind.Unknown ? null : type;
		}

		static ComposedType CreatePointerType(StackAllocExpression stackAlloc) =>
			new ComposedType { BaseType = stackAlloc.Type.Clone(), PointerRank = 1 };
	}
}
