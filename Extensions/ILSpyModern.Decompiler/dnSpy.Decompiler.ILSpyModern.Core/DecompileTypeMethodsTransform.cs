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
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;
using IMethod = ICSharpCode.Decompiler.TypeSystem.IMethod;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// Edit Method/Edit Class/Add Members (<see cref="DecompilationType.TypeMethods"/>): keeps the members
	/// to edit (main document) or everything else with empty bodies (hidden document). Both are compiled
	/// together, so the declaring types become partial. Same rules as the old C# language's transform.
	/// </summary>
	sealed class DecompileTypeMethodsTransform : IAstTransform {
		readonly DecompileTypeMethods info;
		readonly ConvertedModules modules;
		readonly ConvertedModule mainModule;
		readonly HashSet<IMemberDef> defsToShow = new HashSet<IMemberDef>();
		readonly HashSet<TypeDef> partialTypes = new HashSet<TypeDef>();
		// true = main document (the members to edit), false = hidden document (all other members)
		readonly bool showDefinitions;

		public DecompileTypeMethodsTransform(DecompileTypeMethods info, ConvertedModules modules, ConvertedModule mainModule) {
			this.info = info;
			this.modules = modules;
			this.mainModule = mainModule;
			showDefinitions = !info.DecompileHidden;

			foreach (var method in info.Methods) {
				// Properties and events can't be partial, so show the whole property or event
				var prop = method.DeclaringType.Properties.FirstOrDefault(a => a.GetMethods.Contains(method) || a.SetMethods.Contains(method));
				var evt = prop is null ? method.DeclaringType.Events.FirstOrDefault(a => a.AddMethod == method || a.RemoveMethod == method) : null;
				if (prop is not null) {
					defsToShow.Add(prop);
					foreach (var m in prop.GetMethods.Concat(prop.SetMethods).Concat(prop.OtherMethods))
						defsToShow.Add(m);
				}
				else if (evt is not null) {
					defsToShow.Add(evt);
					foreach (var m in new[] { evt.AddMethod, evt.RemoveMethod, evt.InvokeMethod }.Concat(evt.OtherMethods)) {
						if (m is not null)
							defsToShow.Add(m);
					}
				}
				else
					defsToShow.Add(method);
			}
			foreach (var type in info.Types) {
				if (!type.IsEnum) {
					defsToShow.Add(type);
					partialTypes.Add(type);
				}
			}
			foreach (var def in defsToShow.ToArray()) {
				for (var declType = def.DeclaringType; declType is not null; declType = declType.DeclaringType)
					partialTypes.Add(declType);
			}
			foreach (var type in info.Types) {
				if (type.IsEnum) {
					defsToShow.Add(type);
					foreach (var f in type.Fields)
						defsToShow.Add(f);
				}
			}
		}

		/// <summary>
		/// ICSharpCode.Decompiler settings that keep the two documents compilable together
		/// </summary>
		public static void UpdateSettings(ICSharpCode.Decompiler.DecompilerSettings settings, DecompileTypeMethods info) {
			// Fully qualified names in the hidden document, so its namespaces don't change what the main document binds to
			settings.UsingDeclarations = !info.DecompileHidden;
			// Primary constructor parameters would be declared in both parts of the partial type
			settings.UsePrimaryConstructorSyntaxForNonRecordTypes = false;
			// Getters must have a block body so it can be replaced by an empty one
			settings.UseExpressionBodyForCalculatedGetterOnlyProperties = false;
			// Extension blocks don't map 1:1 to the methods that are edited
			settings.ExtensionMembers = false;
		}

		public void Run(AstNode rootNode, TransformContext context) {
			var astBuilder = context.TypeSystemAstBuilder;
			// The hidden document has no using declarations, so it needs fully qualified type names
			TypeSystemAstBuilder? hiddenAstBuilder = null;
			foreach (var en in rootNode.Descendants.OfType<EntityDeclaration>().ToArray()) {
				var def = modules.GetDefinition(en.GetSymbol());
				if (def is null)
					continue;

				if (def is TypeDef typeDef && partialTypes.Contains(typeDef)) {
					if (en is TypeDeclaration tdecl) {
						if (tdecl.ClassType != ClassType.Enum)
							tdecl.Modifiers |= Modifiers.Partial;
						if (!showDefinitions) {
							tdecl.BaseTypes.Clear();
							tdecl.Attributes.Clear();
						}
					}
				}
				else if (showDefinitions) {
					if (!info.ShowAll && !defsToShow.Contains(def))
						en.Remove();
				}
				else {
					if (info.ShowAll || defsToShow.Contains(def))
						en.Remove();
					else
						SetEmptyBody(en, hiddenAstBuilder ??= new TypeSystemAstBuilder());
				}
			}

			if (showDefinitions)
				AddRemovedConstructors(rootNode, astBuilder);
		}

		// The compiler only needs the signatures of the hidden members
		static void SetEmptyBody(EntityDeclaration en, TypeSystemAstBuilder astBuilder) {
			var body = en switch {
				MethodDeclaration m => m.Body,
				ConstructorDeclaration c => c.Body,
				DestructorDeclaration d => d.Body,
				OperatorDeclaration o => o.Body,
				Accessor a => a.Body,
				_ => null,
			};
			if (body is null || !(en.GetSymbol() is IMethod method))
				return;
			var block = new BlockStatement();
			if (en is ConstructorDeclaration ctor && ctor.Initializer is not null && ctor.Initializer.GetSymbol() is IMethod initCtor &&
				!initCtor.Parameters.Any(p => p.Type.Kind == TypeKind.ByReference)) {
				ctor.Initializer.Arguments.Clear();
				foreach (var p in initCtor.Parameters)
					ctor.Initializer.Arguments.Add(new DefaultValueExpression(astBuilder.ConvertType(p.Type)));
			}
			if (en is MethodDeclaration md) {
				md.Modifiers &= ~Modifiers.Async;
				for (int i = 0; i < method.Parameters.Count && i < md.Parameters.Count; i++) {
					var p = method.Parameters[i];
					if (p.ReferenceKind != ReferenceKind.Out || !(p.Type is ByReferenceType byRef))
						continue;
					var name = md.Parameters.ElementAt(i).Name;
					block.Statements.Add(new ExpressionStatement(new AssignmentExpression(new IdentifierExpression(name), new DefaultValueExpression(astBuilder.ConvertType(byRef.ElementType)))));
				}
			}
			var returnType = method.ReturnType;
			if (returnType.Kind == TypeKind.ByReference)
				block.Statements.Add(new ThrowStatement(new NullReferenceExpression()));
			else if (!returnType.IsKnownType(KnownTypeCode.Void) && !method.IsConstructor)
				block.Statements.Add(new ReturnStatement(new DefaultValueExpression(astBuilder.ConvertType(returnType))));
			body.ReplaceWith(block);
		}

		// ICSharpCode.Decompiler removes empty default constructors and static constructors whose code was
		// moved to field initializers. They must still be shown if they're edited.
		void AddRemovedConstructors(AstNode rootNode, TypeSystemAstBuilder astBuilder) {
			var ctorsToShow = defsToShow.OfType<MethodDef>().Where(a => a.IsConstructor).ToList();
			if (info.ShowAll || ctorsToShow.Count == 0)
				return;
			var shown = new HashSet<IMemberDef>();
			var typeDecls = new Dictionary<TypeDef, TypeDeclaration>();
			foreach (var en in rootNode.Descendants.OfType<EntityDeclaration>()) {
				var def = modules.GetDefinition(en.GetSymbol());
				if (def is null)
					continue;
				shown.Add(def);
				if (def is TypeDef td && en is TypeDeclaration tdecl)
					typeDecls[td] = tdecl;
			}
			foreach (var ctor in ctorsToShow) {
				if (shown.Contains(ctor) || !typeDecls.TryGetValue(ctor.DeclaringType, out var tdecl))
					continue;
				if (!(tdecl.GetSymbol() is ITypeDefinition type))
					continue;
				var handle = mainModule.GetHandle(ctor);
				var method = type.Methods.FirstOrDefault(a => a.MetadataToken == handle);
				if (method is null)
					continue;
				if (!(astBuilder.ConvertEntity(method) is ConstructorDeclaration decl))
					continue;
				decl.Body = new BlockStatement();
				if (decl.GetSymbol() is null)
					decl.AddAnnotation(new MemberResolveResult(null, method));
				var insertBefore = tdecl.Members.FirstOrDefault(a => !(a is FieldDeclaration || a is FixedFieldDeclaration || a is EventDeclaration));
				if (insertBefore is null)
					tdecl.Members.Add(decl);
				else
					tdecl.Members.InsertBefore(insertBefore, decl);
			}
		}
	}
}
