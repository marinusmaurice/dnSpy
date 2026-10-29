/*
	Copyright (c) 2015 Ki

	Permission is hereby granted, free of charge, to any person obtaining a copy
	of this software and associated documentation files (the "Software"), to deal
	in the Software without restriction, including without limitation the rights
	to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
	copies of the Software, and to permit persons to whom the Software is
	furnished to do so, subject to the following conditions:

	The above copyright notice and this permission notice shall be included in
	all copies or substantial portions of the Software.

	THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
	IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
	FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
	AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
	LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
	OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
	THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.BamlDecompiler.Properties;

namespace dnSpy.BamlDecompiler.Rewrite {
	/// <summary>
	/// Adds the <c>x:Name</c>s and event handlers that the compiler moved from the XAML to the code-behind's
	/// <c>IComponentConnector.Connect</c> and <c>IStyleConnector.Connect</c> methods (events of elements in templates and
	/// style <c>EventSetter</c>s are in the latter). The methods are read from the IL: for each connection id in the BAML,
	/// the <c>switch</c>/compare dispatch at the start of the method is evaluated to find the id's code, and its field
	/// stores, <c>add_X</c> and <c>AddHandler</c> calls and <c>EventSetter</c>s become attributes and elements.
	/// </summary>
	internal class ConnectionIdRewritePass : IRewritePass {
		const string PresentationXmlNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

		public void Run(XamlContext ctx, XDocument document) {
			var xClass = document.Root.Elements().First().Attribute(ctx.GetXamlNsName("Class"));
			if (xClass is null)
				return;

			var type = ctx.Module.Find(xClass.Value, true);
			if (type is null)
				return;

			var connectMethods = FindConnectMethods(type);
			if (connectMethods.Count == 0)
				return;

			var ids = document.Descendants().Select(a => a.Annotation<BamlConnectionId>()).Where(a => a is not null).Select(a => (int)a.Id).Distinct().ToArray();
			var connIds = new Dictionary<int, Action<XamlContext, XElement>>();
			bool failed = false;
			foreach (var method in connectMethods) {
				try {
					ExtractConnectionIds(ctx, method, ids, connIds);
				}
				catch (OperationCanceledException) {
					throw;
				}
				catch {
					failed = true;
				}
			}

			if (failed && connIds.Count == 0) {
				var msg = dnSpy_BamlDecompiler_Resources.Error_IComponentConnectorConnetCannotBeParsed;
				document.Root.AddBeforeSelf(new XComment(string.Format(msg, type.ReflectionFullName)));
				return;
			}

			foreach (var elem in document.Elements()) {
				ProcessElement(ctx, elem, connIds);
			}
		}

		// The Connect(int, object) methods of IComponentConnector and IStyleConnector (WindowsBase in .NET Framework,
		// System.Xaml in .NET): explicit implementations (C#) or public methods named Connect (VB, older compilers)
		static List<MethodDef> FindConnectMethods(TypeDef type) {
			var list = new List<MethodDef>();
			bool implementsInterface = type.Interfaces.Any(a => IsConnectorInterface(a.Interface));
			foreach (var method in type.Methods) {
				if (method.IsStatic || method.Body is null)
					continue;
				var sig = method.MethodSig;
				if (sig is null || sig.Params.Count != 2 || sig.Params[0].ElementType != ElementType.I4 || sig.Params[1].ElementType != ElementType.Object)
					continue;
				if (method.Overrides.Any(a => a.MethodDeclaration.Name == "Connect" && IsConnectorInterface(a.MethodDeclaration.DeclaringType)) ||
					(implementsInterface && method.Name == "Connect"))
					list.Add(method);
			}
			return list;
		}

		static bool IsConnectorInterface(ITypeDefOrRef type) =>
			type?.Namespace == "System.Windows.Markup" && (type.Name == "IComponentConnector" || type.Name == "IStyleConnector");

		void ProcessElement(XamlContext ctx, XElement elem, Dictionary<int, Action<XamlContext, XElement>> connIds) {
			CheckConnectionId(ctx, elem, connIds);
			foreach (var child in elem.Elements()) {
				ProcessElement(ctx, child, connIds);
			}
		}

		void CheckConnectionId(XamlContext ctx, XElement elem, Dictionary<int, Action<XamlContext, XElement>> connIds) {
			var connId = elem.Annotation<BamlConnectionId>();
			if (connId is null)
				return;

			if (!connIds.TryGetValue((int)connId.Id, out var cb)) {
				elem.AddBeforeSelf(new XComment(string.Format(dnSpy_BamlDecompiler_Resources.Error_UnknownConnectionId, connId.Id)));
				return;
			}

			cb(ctx, elem);
		}

		struct FieldAssignment {
			public string FieldName;

			public void Callback(XamlContext ctx, XElement elem) {
				var xName = ctx.GetXamlNsName("Name");
				if (elem.Attribute("Name") is null && elem.Attribute(xName) is null)
					elem.Add(new XAttribute(xName, FieldName));
			}
		}

		struct EventAttachment {
			public ITypeDefOrRef AttachedType;
			public string EventName;
			public string MethodName;

			public void Callback(XamlContext ctx, XElement elem) {
				XName name;
				if (AttachedType is not null) {
					var clrNs = AttachedType.ReflectionNamespace;
					var xmlNs = ctx.XmlNs.LookupXmlns(AttachedType.DefinitionAssembly, clrNs);
					name = ctx.GetXmlNamespace(xmlNs)?.GetName(AttachedType.Name + "." + EventName) ?? AttachedType.Name + "." + EventName;
				}
				else
					name = EventName;

				elem.Add(new XAttribute(name, MethodName));
			}
		}

		// A style's EventSetters are created in IStyleConnector.Connect, they aren't in the BAML
		struct EventSetterElement {
			public ITypeDefOrRef EventType;
			public string EventName;
			public string MethodName;

			public void Callback(XamlContext ctx, XElement elem) {
				var xmlNs = ctx.XmlNs.LookupXmlns(EventType.DefinitionAssembly, EventType.ReflectionNamespace);
				// The type name is only written when it can be resolved without a prefix, else the style's TargetType is used
				string eventValue = xmlNs == PresentationXmlNs ? EventType.Name + "." + EventName : EventName;
				elem.Add(new XElement(ctx.GetXmlNamespace(PresentationXmlNs).GetName("EventSetter"),
					new XAttribute("Event", eventValue),
					new XAttribute("Handler", MethodName)));
			}
		}

		struct Error {
			public string Msg;

			public void Callback(XamlContext ctx, XElement elem) => elem.AddBeforeSelf(new XComment(Msg));
		}

		static void ExtractConnectionIds(XamlContext ctx, MethodDef method, int[] ids, Dictionary<int, Action<XamlContext, XElement>> connIds) {
			var instrs = method.Body.Instructions;
			var indexes = new Dictionary<Instruction, int>(instrs.Count);
			for (int i = 0; i < instrs.Count; i++)
				indexes[instrs[i]] = i;

			var starts = new Dictionary<int, int>();
			foreach (var id in ids) {
				ctx.CancellationToken.ThrowIfCancellationRequested();
				if (FindCaseBlock(method, indexes, id) is int start)
					starts[id] = start;
			}
			var allStarts = new HashSet<int>(starts.Values);
			foreach (var kv in starts) {
				// An id is handled by one of the two methods; the other one's default code does nothing with it
				if (connIds.ContainsKey(kv.Key))
					continue;
				var cb = ParseCaseBlock(instrs, kv.Value, allStarts);
				if (cb is not null)
					connIds[kv.Key] = cb;
			}
		}

		/// <summary>
		/// Runs the dispatch code at the start of the method with <c>connectionId == id</c> (constants, the
		/// <c>connectionId</c> argument, locals, <c>+</c>/<c>-</c>, compares and branches) and returns the index of the
		/// first other instruction, where the id's code starts, or null if the dispatch can't be evaluated
		/// </summary>
		static int? FindCaseBlock(MethodDef method, Dictionary<Instruction, int> indexes, int id) {
			var instrs = method.Body.Instructions;
			var stack = new Stack<int>();
			var locals = new Dictionary<int, int>();
			int pc = 0;
			for (int steps = 0; steps < 1000 && pc < instrs.Count; steps++) {
				var instr = instrs[pc];
				int a, b;
				switch (instr.OpCode.Code) {
				case Code.Nop:
					pc++;
					break;

				case Code.Ldarg_1:
				case Code.Ldarg_S:
				case Code.Ldarg:
					if (instr.GetParameterIndex() != 1)
						return stack.Count == 0 ? pc : null;
					stack.Push(id);
					pc++;
					break;

				case Code.Ldc_I4:
				case Code.Ldc_I4_S:
				case Code.Ldc_I4_M1:
				case Code.Ldc_I4_0:
				case Code.Ldc_I4_1:
				case Code.Ldc_I4_2:
				case Code.Ldc_I4_3:
				case Code.Ldc_I4_4:
				case Code.Ldc_I4_5:
				case Code.Ldc_I4_6:
				case Code.Ldc_I4_7:
				case Code.Ldc_I4_8:
					stack.Push(instr.GetLdcI4Value());
					pc++;
					break;

				case Code.Stloc:
				case Code.Stloc_S:
				case Code.Stloc_0:
				case Code.Stloc_1:
				case Code.Stloc_2:
				case Code.Stloc_3:
					if (stack.Count == 0)
						return null;
					locals[instr.GetLocal(method.Body.Variables).Index] = stack.Pop();
					pc++;
					break;

				case Code.Ldloc:
				case Code.Ldloc_S:
				case Code.Ldloc_0:
				case Code.Ldloc_1:
				case Code.Ldloc_2:
				case Code.Ldloc_3:
					if (!locals.TryGetValue(instr.GetLocal(method.Body.Variables).Index, out a))
						return stack.Count == 0 ? pc : null;
					stack.Push(a);
					pc++;
					break;

				case Code.Add:
				case Code.Sub:
				case Code.Ceq:
				case Code.Cgt:
				case Code.Cgt_Un:
				case Code.Clt:
				case Code.Clt_Un:
					if (stack.Count < 2)
						return null;
					b = stack.Pop();
					a = stack.Pop();
					stack.Push(instr.OpCode.Code switch {
						Code.Add => a + b,
						Code.Sub => a - b,
						Code.Ceq => a == b ? 1 : 0,
						Code.Cgt => a > b ? 1 : 0,
						Code.Cgt_Un => (uint)a > (uint)b ? 1 : 0,
						Code.Clt => a < b ? 1 : 0,
						_ => (uint)a < (uint)b ? 1 : 0,
					});
					pc++;
					break;

				case Code.Switch:
					if (stack.Count == 0 || instr.Operand is not IList<Instruction> targets)
						return null;
					a = stack.Pop();
					pc = (uint)a < (uint)targets.Count ? indexes[targets[a]] : pc + 1;
					break;

				case Code.Br:
				case Code.Br_S:
					pc = indexes[(Instruction)instr.Operand];
					break;

				case Code.Brtrue:
				case Code.Brtrue_S:
				case Code.Brfalse:
				case Code.Brfalse_S:
					if (stack.Count == 0)
						return null;
					a = stack.Pop();
					bool isTrue = instr.OpCode.Code is Code.Brtrue or Code.Brtrue_S;
					pc = (a != 0) == isTrue ? indexes[(Instruction)instr.Operand] : pc + 1;
					break;

				case Code.Beq:
				case Code.Beq_S:
				case Code.Bne_Un:
				case Code.Bne_Un_S:
				case Code.Bgt:
				case Code.Bgt_S:
				case Code.Bgt_Un:
				case Code.Bgt_Un_S:
				case Code.Bge:
				case Code.Bge_S:
				case Code.Bge_Un:
				case Code.Bge_Un_S:
				case Code.Blt:
				case Code.Blt_S:
				case Code.Blt_Un:
				case Code.Blt_Un_S:
				case Code.Ble:
				case Code.Ble_S:
				case Code.Ble_Un:
				case Code.Ble_Un_S:
					if (stack.Count < 2)
						return null;
					b = stack.Pop();
					a = stack.Pop();
					bool jump = instr.OpCode.Code switch {
						Code.Beq or Code.Beq_S => a == b,
						Code.Bne_Un or Code.Bne_Un_S => a != b,
						Code.Bgt or Code.Bgt_S => a > b,
						Code.Bgt_Un or Code.Bgt_Un_S => (uint)a > (uint)b,
						Code.Bge or Code.Bge_S => a >= b,
						Code.Bge_Un or Code.Bge_Un_S => (uint)a >= (uint)b,
						Code.Blt or Code.Blt_S => a < b,
						Code.Blt_Un or Code.Blt_Un_S => (uint)a < (uint)b,
						Code.Ble or Code.Ble_S => a <= b,
						_ => (uint)a <= (uint)b,
					};
					pc = jump ? indexes[(Instruction)instr.Operand] : pc + 1;
					break;

				default:
					// The id's own code starts here
					return stack.Count == 0 ? pc : null;
				}
			}
			return null;
		}

		/// <summary>
		/// Reads the code of one connection id, up to its <c>ret</c>/branch out or the start of another id's code
		/// </summary>
		static Action<XamlContext, XElement> ParseCaseBlock(IList<Instruction> instrs, int start, HashSet<int> allStarts) {
			Action<XamlContext, XElement> cb = null;
			IMethod handler = null;
			IField routedEvent = null;
			IField setterEvent = null;
			for (int i = start; i < instrs.Count; i++) {
				if (i != start && allStarts.Contains(i))
					break;
				var instr = instrs[i];
				var code = instr.OpCode.Code;
				if (code is Code.Ret or Code.Br or Code.Br_S or Code.Leave or Code.Leave_S or Code.Throw)
					break;

				switch (code) {
				case Code.Ldftn:
				case Code.Ldvirtftn:
					handler = instr.Operand as IMethod;
					break;

				case Code.Ldsfld:
					routedEvent = instr.Operand as IField;
					break;

				case Code.Stfld:
					// this.field = (T)target
					if (instr.Operand is IField field && PreviousCode(instrs, i, start) is Code.Castclass or Code.Isinst)
						cb += new FieldAssignment { FieldName = field.Name }.Callback;
					break;

				case Code.Call:
				case Code.Callvirt:
					if (instr.Operand is not IMethod called)
						break;
					string name = called.Name;
					if (name.StartsWith("set_", StringComparison.Ordinal) && called.MethodSig?.Params.Count == 1 && PreviousCode(instrs, i, start) is Code.Castclass or Code.Isinst) {
						// VB: Me.field = CType(target, T), the field is a WithEvents property
						cb += new FieldAssignment { FieldName = name.Substring(4) }.Callback;
					}
					else if (name == "AddHandler" && routedEvent is not null && handler is not null) {
						// Attached event: ((UIElement)target).AddHandler(Mouse.MouseDownEvent, new MouseButtonEventHandler(M))
						cb += new EventAttachment {
							AttachedType = routedEvent.DeclaringType,
							EventName = GetEventName(routedEvent),
							MethodName = handler.Name,
						}.Callback;
						handler = null;
						routedEvent = null;
					}
					else if (name.StartsWith("add_", StringComparison.Ordinal) && handler is not null) {
						// CLR event: ((Button)target).Click += new RoutedEventHandler(M)
						cb += new EventAttachment {
							EventName = name.Substring(4),
							MethodName = handler.Name,
						}.Callback;
						handler = null;
					}
					else if (name == "set_Event" && called.DeclaringType?.Name == "EventSetter" && routedEvent is not null) {
						setterEvent = routedEvent;
						routedEvent = null;
					}
					else if (name == "set_Handler" && called.DeclaringType?.Name == "EventSetter" && handler is not null) {
						if (setterEvent is not null) {
							cb += new EventSetterElement {
								EventType = setterEvent.DeclaringType,
								EventName = GetEventName(setterEvent),
								MethodName = handler.Name,
							}.Callback;
						}
						else
							cb += new Error { Msg = string.Format(dnSpy_BamlDecompiler_Resources.Error_AttachedEvent, name) }.Callback;
						setterEvent = null;
						handler = null;
					}
					break;
				}
			}
			return cb;
		}

		static Code? PreviousCode(IList<Instruction> instrs, int index, int start) {
			for (int i = index - 1; i >= start; i--) {
				if (instrs[i].OpCode.Code != Code.Nop)
					return instrs[i].OpCode.Code;
			}
			return null;
		}

		// ButtonBase.ClickEvent -> Click
		static string GetEventName(IField routedEventField) {
			string name = routedEventField.Name;
			return name.EndsWith("Event", StringComparison.Ordinal) ? name.Substring(0, name.Length - 5) : name;
		}
	}
}
