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
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// ICSharpCode.Decompiler's <see cref="TransformFieldAndConstructorInitializers"/> only checks the IL instructions
	/// annotated on the top node of a constructor's <c>field = value;</c> value to see whether it uses a constructor
	/// parameter. A top node without them, eg. the cast in <c>index = (int)(code - Code.A);</c>, hides the parameter, so the
	/// assignment became a field initializer that uses the parameter (CS0103). <see cref="Mark"/> runs before it and adds
	/// the parameter load's instruction to such top nodes; <see cref="Unmark"/> runs after it and removes them again.
	/// </summary>
	sealed class ParameterInitializerTransform {
		readonly List<(Expression expr, ILInstruction inst)> added = new List<(Expression, ILInstruction)>();

		public IAstTransform Mark { get; }
		public IAstTransform Unmark { get; }

		public ParameterInitializerTransform() {
			Mark = new MarkTransform(this);
			Unmark = new UnmarkTransform(this);
		}

		public static void Insert(IList<IAstTransform> transforms) {
			int index = 0;
			while (index < transforms.Count && !(transforms[index] is TransformFieldAndConstructorInitializers))
				index++;
			if (index == transforms.Count)
				return;
			var transform = new ParameterInitializerTransform();
			transforms.Insert(index + 1, transform.Unmark);
			transforms.Insert(index, transform.Mark);
		}

		sealed class MarkTransform : IAstTransform {
			readonly ParameterInitializerTransform owner;
			public MarkTransform(ParameterInitializerTransform owner) => this.owner = owner;

			public void Run(AstNode rootNode, TransformContext context) {
				foreach (var ctor in rootNode.DescendantsAndSelf.OfType<ConstructorDeclaration>()) {
					if (ctor.Body is null || ctor.HasModifier(Modifiers.Static) || ctor.Annotation<ILFunction>() is not ILFunction function)
						continue;
					foreach (var stmt in ctor.Body.Statements) {
						if (!(stmt is ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign } assign }))
							continue;
						var value = assign.Right;
						if (value.Annotations.OfType<ILInstruction>().Any(a => FindParameterLoad(a, function) is not null))
							continue;
						var paramLoad = value.Descendants.SelectMany(a => a.Annotations.OfType<ILInstruction>()).Select(a => FindParameterLoad(a, function)).FirstOrDefault(a => a is not null);
						if (paramLoad is null)
							continue;
						value.AddAnnotation(paramLoad);
						owner.added.Add((value, paramLoad));
					}
				}
			}

			// Same check as TransformFieldAndConstructorInitializers
			static ILInstruction? FindParameterLoad(ILInstruction instruction, ILFunction function) {
				foreach (var inst in instruction.Descendants) {
					if (inst is IInstructionWithVariableOperand { Variable: var v } && v.Function == function && v.Kind == VariableKind.Parameter)
						return inst;
				}
				return null;
			}
		}

		sealed class UnmarkTransform : IAstTransform {
			readonly ParameterInitializerTransform owner;
			public UnmarkTransform(ParameterInitializerTransform owner) => this.owner = owner;

			public void Run(AstNode rootNode, TransformContext context) {
				foreach (var (expr, inst) in owner.added) {
					var others = expr.Annotations.OfType<ILInstruction>().Where(a => a != inst).ToList();
					expr.RemoveAnnotations<ILInstruction>();
					foreach (var a in others)
						expr.AddAnnotation(a);
				}
				owner.added.Clear();
			}
		}
	}
}
