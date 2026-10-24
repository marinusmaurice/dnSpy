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
using System.IO;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpyModern.Core {
	/// <summary>
	/// Writes ICSharpCode.Decompiler's C# AST to dnSpy's <see cref="IDecompilerOutput"/>: colors,
	/// indentation and references to dnlib definitions (so they can be clicked)
	/// </summary>
	sealed class DecompilerOutputTokenWriter : TokenWriter, ILocatable {
		readonly IDecompilerOutput output;
		readonly ConvertedModules modules;
		readonly Stack<AstNode> nodeStack = new Stack<AstNode>();
		// Output position of the start of each line (before indentation). Used to convert the
		// TextLocations that ICSharpCode.Decompiler stores in the AST to output positions.
		readonly List<int> lineStarts = new List<int>();

		public DecompilerOutputTokenWriter(IDecompilerOutput output, ConvertedModules modules) {
			this.output = output;
			this.modules = modules;
			lineStarts.Add(output.Length);
		}

		// NextPosition includes indentation that hasn't been written yet, so a location at the
		// start of a line is the position of its first token
		public TextLocation Location => new TextLocation(lineStarts.Count, output.NextPosition - lineStarts[lineStarts.Count - 1] + 1);
		public int Length => output.NextPosition;

		/// <summary>
		/// Converts a location that was set by this writer to an output position, or returns -1
		/// </summary>
		public int GetPosition(int line, int column) {
			if (line < 1 || line > lineStarts.Count || column < 1)
				return -1;
			return lineStarts[line - 1] + column - 1;
		}

		void WriteLine() {
			output.WriteLine();
			lineStarts.Add(output.Length);
		}

		public override void StartNode(AstNode node) => nodeStack.Push(node);
		public override void EndNode(AstNode node) => nodeStack.Pop();

		public override void WriteIdentifier(Identifier identifier) {
			if (identifier.IsVerbatim || CSharpOutputVisitor.IsKeyword(identifier.Name, identifier))
				output.Write("@", BoxedTextColor.Text);
			var name = TextWriterTokenWriter.EscapeIdentifier(identifier.Name);

			var definition = GetCurrentDefinition();
			if (definition is not null) {
				var def = ToDnlib(definition);
				output.Write(name, def, def is null ? DecompilerReferenceFlags.None : DecompilerReferenceFlags.Definition, GetColor(def, definition));
				return;
			}

			var symbol = GetCurrentMemberReference();
			if (symbol is not null) {
				var def = ToDnlib(symbol);
				output.Write(name, def, DecompilerReferenceFlags.None, GetColor(def, symbol));
				return;
			}

			var localDef = GetCurrentLocalDefinition(identifier);
			if (localDef is not null) {
				output.Write(name, localDef, DecompilerReferenceFlags.Local | DecompilerReferenceFlags.Definition, GetLocalColor(localDef));
				return;
			}

			var localRef = GetCurrentLocalReference();
			if (localRef is not null) {
				output.Write(name, localRef, DecompilerReferenceFlags.Local, GetLocalColor(localRef));
				return;
			}

			output.Write(name, GetColorFromRole(identifier));
		}

		object GetColorFromRole(Identifier identifier) {
			var parent = identifier.Parent;
			if (parent is NamespaceDeclaration || parent is UsingDeclaration || parent?.Parent is UsingDeclaration)
				return BoxedTextColor.Namespace;
			if (parent is MemberType && parent.Parent is UsingDeclaration)
				return BoxedTextColor.Namespace;
			return BoxedTextColor.Text;
		}

		ISymbol? GetCurrentDefinition() {
			if (nodeStack.Count == 0)
				return null;
			var node = nodeStack.Peek();
			if (node is Identifier && node.Parent is not null)
				node = node.Parent;
			if (IsDefinition(ref node))
				return node.GetSymbol();
			return null;
		}

		static bool IsDefinition(ref AstNode node) {
			if (node is EntityDeclaration && node.Parent is not LocalFunctionDeclarationStatement)
				return true;
			if (node is VariableInitializer && node.Parent is FieldDeclaration) {
				node = node.Parent;
				return true;
			}
			if (node is FixedVariableInitializer && node.Parent is FixedFieldDeclaration) {
				node = node.Parent;
				return true;
			}
			return false;
		}

		ISymbol? GetCurrentMemberReference() {
			if (nodeStack.Count == 0)
				return null;
			var node = nodeStack.Peek();
			if (node is Identifier && node.Parent is not null)
				node = node.Parent;
			var symbol = node.GetSymbol();
			if (symbol is null && node.Parent is InvocationExpression invocation && invocation.Target == node)
				symbol = node.Parent.GetSymbol();
			if (symbol is not null && node.Parent is ObjectCreateExpression oce && oce.Type == node)
				symbol = node.Parent.GetSymbol();
			if (node is IdentifierExpression && node.Parent is InvocationExpression invocation2 && invocation2.Target == node && symbol is IMember member) {
				if (member.DeclaringType is IType declaringType && declaringType.Kind == TypeKind.Delegate)
					return null;
			}
			if (symbol is ILVariable || symbol is IParameter || symbol is ITypeParameter && node is not SimpleType)
				return null;
			return symbol;
		}

		object? GetCurrentLocalReference() {
			if (nodeStack.Count == 0)
				return null;
			var node = nodeStack.Peek();
			if (node is Identifier && node.Parent is not null)
				node = node.Parent;
			var variable = node.Annotation<ILVariableResolveResult>()?.Variable;
			if (variable is not null)
				return variable;
			var letVariable = node.Annotation<LetIdentifierAnnotation>();
			if (letVariable is not null)
				return letVariable;
			return null;
		}

		object? GetCurrentLocalDefinition(Identifier id) {
			if (nodeStack.Count == 0)
				return null;
			var node = nodeStack.Peek();
			if (node is Identifier && node.Parent is not null)
				node = node.Parent;
			if (node is ParameterDeclaration || node is VariableInitializer || node is CatchClause || node is VariableDesignation) {
				var variable = node.Annotation<ILVariableResolveResult>()?.Variable;
				if (variable is not null)
					return variable;
			}
			if (id.Parent is QueryJoinClause join && (join.JoinIdentifierToken == id || join.IntoIdentifierToken == id)) {
				var variable = id.Annotation<ILVariableResolveResult>()?.Variable;
				if (variable is not null)
					return variable;
			}
			if (node is QueryLetClause) {
				var letVariable = node.Annotation<LetIdentifierAnnotation>();
				if (letVariable is not null)
					return letVariable;
			}
			return null;
		}

		object? ToDnlib(ISymbol symbol) => modules.GetDefinition(symbol);

		static object GetColor(object? dnlibDef, ISymbol symbol) {
			if (dnlibDef is not null)
				return CSharpMetadataTextColorProvider.Instance.GetColor(dnlibDef);
			switch (symbol) {
			case ITypeParameter tp:
				return tp.OwnerType == SymbolKind.Method ? BoxedTextColor.MethodGenericParameter : BoxedTextColor.TypeGenericParameter;
			case IType type:
				switch (type.Kind) {
				case TypeKind.Interface: return BoxedTextColor.Interface;
				case TypeKind.Struct: return BoxedTextColor.ValueType;
				case TypeKind.Delegate: return BoxedTextColor.Delegate;
				case TypeKind.Enum: return BoxedTextColor.Enum;
				default: return BoxedTextColor.Type;
				}
			case IMethod method:
				return method.IsExtensionMethod ? BoxedTextColor.ExtensionMethod : method.IsStatic ? BoxedTextColor.StaticMethod : BoxedTextColor.InstanceMethod;
			case IField field:
				return field.IsConst ? BoxedTextColor.LiteralField : field.IsStatic ? BoxedTextColor.StaticField : BoxedTextColor.InstanceField;
			case IProperty property:
				return property.IsStatic ? BoxedTextColor.StaticProperty : BoxedTextColor.InstanceProperty;
			case IEvent ev:
				return ev.IsStatic ? BoxedTextColor.StaticEvent : BoxedTextColor.InstanceEvent;
			case INamespace:
				return BoxedTextColor.Namespace;
			default:
				return BoxedTextColor.Text;
			}
		}

		static object GetLocalColor(object local) =>
			local is ILVariable v && v.Kind == VariableKind.Parameter ? BoxedTextColor.Parameter : BoxedTextColor.Local;

		public override void WriteKeyword(string keyword) {
			// Accessor keywords are the definitions of the accessor methods (like the old C# language)
			if (nodeStack.Count != 0 && nodeStack.Peek() is Accessor accessor) {
				switch (keyword) {
				case "get": case "set": case "init": case "add": case "remove":
					var symbol = accessor.GetSymbol();
					var def = symbol is null ? null : ToDnlib(symbol);
					if (def is not null) {
						output.Write(keyword, def, DecompilerReferenceFlags.Definition, BoxedTextColor.Keyword);
						return;
					}
					break;
				}
			}
			// Operators have no name, so "operator" is the definition
			else if (keyword == "operator" && nodeStack.Count != 0 && nodeStack.Peek() is OperatorDeclaration op) {
				var symbol = op.GetSymbol();
				var def = symbol is null ? null : ToDnlib(symbol);
				if (def is not null) {
					output.Write(keyword, def, DecompilerReferenceFlags.Definition, BoxedTextColor.Keyword);
					return;
				}
			}
			output.Write(keyword, BoxedTextColor.Keyword);
		}

		public override void WriteToken(string token) {
			switch (token) {
			case "{": case "}": case "(": case ")": case "[": case "]": case ";": case ",": case ".":
				output.Write(token, BoxedTextColor.Punctuation);
				break;
			default:
				output.Write(token, BoxedTextColor.Operator);
				break;
			}
		}

		public override void WritePrimitiveValue(object? value, LiteralFormat format = LiteralFormat.None) {
			var text = Format(w => w.WritePrimitiveValue(value, format));
			object color;
			switch (value) {
			case null:
			case bool:
				color = BoxedTextColor.Keyword;
				break;
			case string:
				color = format == LiteralFormat.VerbatimStringLiteral ? BoxedTextColor.VerbatimString : BoxedTextColor.String;
				break;
			case char:
				color = BoxedTextColor.Char;
				break;
			default:
				color = BoxedTextColor.Number;
				break;
			}
			output.Write(text, color);
		}

		public override void WritePrimitiveType(string type) {
			output.Write(type, BoxedTextColor.Keyword);
			// The new() constraint is a primitive type and the token writer adds the parentheses (same as TextWriterTokenWriter)
			if (type == "new") {
				output.Write("(", BoxedTextColor.Punctuation);
				output.Write(")", BoxedTextColor.Punctuation);
			}
		}

		public override void WriteInterpolatedText(string text) => output.Write(Format(w => w.WriteInterpolatedText(text)), BoxedTextColor.String);

		public override void Space() => output.Write(" ", BoxedTextColor.Text);
		public override void Indent() => output.IncreaseIndent();
		public override void Unindent() => output.DecreaseIndent();
		public override void NewLine() => WriteLine();

		public override void WriteComment(CommentType commentType, string content) {
			if (commentType == CommentType.Documentation) {
				output.Write("///", BoxedTextColor.XmlDocCommentDelimiter);
				output.WriteXmlDoc(content);
				WriteLine();
				return;
			}
			var text = Format(w => w.WriteComment(commentType, content));
			output.Write(text.TrimEnd('\r', '\n'), BoxedTextColor.Comment);
			if (commentType == CommentType.SingleLine)
				WriteLine();
		}

		public override void WritePreProcessorDirective(PreProcessorDirectiveType type, string? argument) {
			var text = Format(w => w.WritePreProcessorDirective(type, argument));
			output.Write(text.TrimEnd('\r', '\n'), BoxedTextColor.PreprocessorKeyword);
			WriteLine();
		}

		// Uses ICSharpCode.Decompiler's own escaping and number formatting
		static string Format(System.Action<TextWriterTokenWriter> write) {
			var writer = new StringWriter();
			write(new TextWriterTokenWriter(writer));
			return writer.ToString();
		}
	}
}
