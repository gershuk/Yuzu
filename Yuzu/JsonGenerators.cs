﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Yuzu.Metadata;
using Yuzu.Util;

namespace Yuzu.Json
{
	public abstract class JsonDeserializerGenBase : JsonDeserializer
	{
		public abstract object FromReaderIntPartial(string name);

		public Assembly Assembly;

		protected virtual string GetWrapperNamespace()
		{
			var ns = GetType().Namespace;
			var i = ns.IndexOf('.');
			return i < 0 ? ns : ns.Remove(i);
		}

		protected string GetDeserializerName(Type t) =>
			GetWrapperNamespace() + "." + t.Namespace + "." +
			Utils.GetMangledTypeName(t) + "_JsonDeserializer";

		private static Dictionary<string, JsonDeserializerGenBase> deserializerCache =
			new Dictionary<string, JsonDeserializerGenBase>();

		private JsonDeserializerGenBase MakeDeserializer(string className)
		{
			if (!deserializerCache.TryGetValue(className, out JsonDeserializerGenBase result)) {
				var t = FindType(className);
				var dt = TypeSerializer.Deserialize(
					GetDeserializerName(t) + ", " + (Assembly ?? GetType().Assembly).FullName);
				if (dt == null)
					throw Error("Generated deserializer not found for type '{0}'", className);
				result = (JsonDeserializerGenBase)Utils.CreateObject(dt);
				deserializerCache[className] = result;
			}
			result.Reader = Reader;
			return result;
		}

		private object MaybeReadObject(string className, string name)
		{
			return name == null ?
				Utils.CreateObject(FindType(className)) :
				MakeDeserializer(className).FromReaderIntPartial(name);
		}

		private object FromReaderIntGenerated()
		{
			KillBuf();
			Require('{');
			CheckClassTag(GetNextName(first: true));
			var typeName = RequireUnescapedString();
			return MaybeReadObject(typeName, GetNextName(first: false));
		}

		protected object FromReaderIntGenerated(object obj)
		{
			KillBuf();
			Require('{');
			var expectedType = obj.GetType();
			string name = GetNextName(first: true);
			if (name == JsonOptions.ClassTag) {
				CheckExpectedType(RequireUnescapedString(), expectedType);
				name = GetNextName(first: false);
			}
			return name == null ? obj :
				MakeDeserializer(TypeSerializer.Serialize(obj.GetType())).ReadFields(obj, name);
		}

		public override object FromReaderInt()
		{
			return FromReaderIntGenerated();
		}

		public T DefaultFactory<T>() where T : new() => new T();
		public T FromReaderTyped<T>(BinaryReader reader) where T : new() =>
			FromReaderTypedFactory(reader, DefaultFactory<T>);

		public T FromReaderTypedFactory<T>(BinaryReader reader, Func<T> factory)
		{
			Reader = reader;
			KillBuf();
			var ch = RequireBracketOrNull();
			if (ch == 'n') return default(T);
			if (ch == '[') return (T)ReadFieldsCompact(factory());
			var name = GetNextName(true);
			if (name != JsonOptions.ClassTag) return (T)ReadFields(factory(), name);
			var typeName = RequireUnescapedString();
			return (T)MaybeReadObject(typeName, GetNextName(first: false));
		}

		public T FromReaderInterface<T>(BinaryReader reader) where T : class
		{
			Reader = reader;
			KillBuf();
			var ch = Require('{', 'n');
			if (ch == 'n') {
				Require("ull");
				return null;
			}
			CheckClassTag(GetNextName(first: true));
			var typeName = RequireUnescapedString();
			return (T)MaybeReadObject(typeName, GetNextName(first: false));
		}
	}

	public class JsonDeserializerGenerator : JsonDeserializerGenBase, IGenerator
	{
		public static new JsonDeserializerGenerator Instance = new JsonDeserializerGenerator();

		private CodeWriter cw = new CodeWriter();
		private string wrapperNameSpace = "";
		private string lastNameSpace = "";

		public string LineSeparator { get { return cw.LineSeparator; } set { cw.LineSeparator = value; } }

		public StreamWriter GenWriter
		{
			get { return cw.Output; }
			set { cw.Output = value; }
		}

		public JsonDeserializerGenerator(string wrapperNameSpace = "YuzuGen")
		{
			this.wrapperNameSpace = wrapperNameSpace;
		}

		public void GenerateHeader()
		{
			if (Options.AllowUnknownFields || JsonOptions.Unordered)
				throw new NotImplementedException();
			cw.Put("using System;\n");
			cw.Put("using System.Collections.Generic;\n");
			cw.Put("\n");
			cw.Put("using Yuzu;\n");
			cw.Put("using Yuzu.Json;\n");
		}

		public void GenerateFooter()
		{
			cw.PutEndBlock(); // Close namespace.
		}

		private void PutRequireOrNull(char ch, Type t, string name)
		{
			cw.PutPart("RequireOrNull('{0}') ? null : new {1}();\n", ch, Utils.GetTypeSpec(t));
			cw.Put("if ({0} != null) {{\n", name);
		}

		private void PutRequireOrNullArray(char ch, Type t, string name)
		{
			cw.PutPart("RequireOrNull('{0}') ? null : new {1};\n", ch, Utils.GetTypeSpec(t, arraySize: "0"));
			cw.Put("if ({0} != null) {{\n", name);
		}

		private void GenerateCollection(Type t, Type icoll, string name)
		{
			cw.Put("if (SkipSpacesCarefully() == ']') {\n");
			cw.Put("Require(']');\n");
			cw.PutEndBlock();
			cw.Put("else {\n");
			cw.Put("do {\n");
			var tempElementName = cw.GetTempName();
			cw.Put("var {0} = ", tempElementName);
			GenerateValue(icoll.GetGenericArguments()[0], tempElementName);
			cw.PutAddToCollection(t, icoll, name, tempElementName);
			cw.Put("} while (Require(']', ',') == ',');\n");
			cw.PutEndBlock();
		}

		private void GenerateMerge(Type t, string name)
		{
			var idict = Utils.GetIDictionary(t);
			if (idict != null) {
				cw.Put("Require('{');\n");
				GenerateDictionary(t, idict, name);
				return;
			}
			var icoll = Utils.GetICollection(t);
			if (icoll != null) {
				cw.Put("Require('[');\n");
				GenerateCollection(t, icoll, name);
				return;
			}
			if ((t.IsClass || t.IsInterface) && t != typeof(object))
				cw.Put("{0}.Instance.FromReader({1}, Reader);\n", GetDeserializerName(t), name);
			else
				throw Error("Unable to merge field {1} of type {0}", name, t.Name);
		}

		private void GenerateDictionary(Type t, Type idict, string name)
		{
			cw.Put("if (SkipSpacesCarefully() == '}') {\n");
			cw.Put("Require('}');\n");
			cw.PutEndBlock();
			cw.Put("else {\n");
			cw.Put("do {\n");
			var tempKeyStr = cw.GetTempName();
			cw.Put("var {0} = RequireString();\n", tempKeyStr);
			cw.Put("Require(':');\n");
			var tempValue = cw.GetTempName();
			cw.Put("var {0} = ", tempValue);
			GenerateValue(idict.GetGenericArguments()[1], tempValue);
			var keyType = idict.GetGenericArguments()[0];
			var tempKey =
				keyType == typeof(string) ? tempKeyStr :
				keyType == typeof(int) ? String.Format("int.Parse({0})", tempKeyStr) :
				keyType.IsEnum ?
					String.Format("({0})Enum.Parse(typeof({0}), {1})", Utils.GetTypeSpec(keyType), tempKeyStr) :
				// Slow.
					String.Format("({0})keyParsers[typeof({0})]({1})", Utils.GetTypeSpec(keyType), tempKeyStr);
			cw.Put("{0}.Add({1}, {2});\n", name, tempKey, tempValue);
			cw.Put("} while (Require('}', ',') == ',');\n");
			cw.PutEndBlock();
		}

		private static Dictionary<Type, string> simpleValueReader = new Dictionary<Type, string>() {
			{ typeof(sbyte), "checked((sbyte)RequireInt())" },
			{ typeof(byte), "checked((byte)RequireUInt())" },
			{ typeof(short), "checked((short)RequireInt())" },
			{ typeof(ushort), "checked((ushort)RequireUInt())" },
			{ typeof(int), "RequireInt()" },
			{ typeof(uint), "RequireUInt()" },
			{ typeof(long), "RequireLong()" },
			{ typeof(ulong), "RequireULong()" },
			{ typeof(bool), "RequireBool()" },
			{ typeof(char), "RequireChar()" },
			{ typeof(float), "RequireSingle()" },
			{ typeof(double), "RequireDouble()" },
			{ typeof(DateTime), "RequireDateTime()" },
			{ typeof(DateTimeOffset), nameof(RequireDateTimeOffset) + "()" },
			{ typeof(TimeSpan), "RequireTimeSpan()" },
			{ typeof(Guid), nameof(RequireGuid) + "()" },
			{ typeof(string), "RequireString()" },
			{ typeof(object), "ReadAnyObject()" },
		};

		private string GenerateFromReader(Meta meta) =>
			String.Format(
				meta.Type.IsInterface || meta.Type.IsAbstract ?
					"FromReaderInterface<{0}>(Reader);" :
				meta.FactoryMethod == null ?
					"FromReaderTyped<{0}>(Reader);" :
					"FromReaderTypedFactory(Reader, {0}.{1});",
				Utils.GetTypeSpec(meta.Type), meta.FactoryMethod?.Name);

		private void GenerateValue(Type t, string name)
		{
			if (simpleValueReader.TryGetValue(t, out string sr)) {
				cw.PutPart(sr + ";\n");
				return;
			}
			if (t == typeof(decimal)) {
				cw.PutPart(JsonOptions.DecimalAsString ? "RequireDecimalAsString();\n" : "RequireDecimal();\n");
				return;
			}
			if (t.IsEnum) {
				cw.PutPart(
					JsonOptions.EnumAsString ?
						"({0})Enum.Parse(typeof({0}), RequireString());\n" :
						"({0}){1};\n",
					Utils.GetTypeSpec(t), simpleValueReader[Enum.GetUnderlyingType(t)]);
				return;
			}
			if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>)) {
				cw.PutPart("null;\n");
				cw.Put("if (SkipSpacesCarefully() == 'n') {\n");
				cw.Put("Require(\"null\");\n");
				cw.PutEndBlock();
				cw.Put("else {\n");
				cw.Put("{0} = ", name);
				GenerateValue(t.GetGenericArguments()[0], name);
				cw.PutEndBlock();
				return;
			}
			if (t.IsArray && t.GetArrayRank() > 1) {
				cw.PutPart("({0}){1}(typeof({0}));\n", Utils.GetTypeSpec(t), nameof(ReadArrayNDim));
				return;
			}
			if (t.IsArray && !JsonOptions.ArrayLengthPrefix) {
				PutRequireOrNullArray('[', t, name);
				cw.Put("if (SkipSpacesCarefully() == ']') {\n");
				cw.Put("Require(']');\n");
				cw.PutEndBlock();
				cw.Put("else {\n");
				var tempListName = cw.GetTempName();
				cw.Put("var {0} = new List<{1}>();\n", tempListName, Utils.GetTypeSpec(t.GetElementType()));
				cw.Put("do {\n");
				var tempName = cw.GetTempName();
				cw.Put("var {0} = ", tempName);
				GenerateValue(t.GetElementType(), tempName);
				cw.Put("{0}.Add({1});\n", tempListName, tempName);
				cw.Put("} while (Require(']', ',') == ',');\n");
				cw.Put("{0} = {1}.ToArray();\n", name, tempListName);
				cw.PutEndBlock();
				cw.PutEndBlock();
				return;
			}
			if (t.IsArray && JsonOptions.ArrayLengthPrefix) {
				PutRequireOrNullArray('[', t, name);
				cw.Put("if (SkipSpacesCarefully() != ']') {\n");
				var tempArrayName = cw.GetTempName();
				cw.Put("var {0} = new {1};\n", tempArrayName, Utils.GetTypeSpec(t, arraySize: "RequireUInt()"));
				var tempIndexName = cw.GetTempName();
				cw.Put("for(int {0} = 0; {0} < {1}.Length; ++{0}) {{\n", tempIndexName, tempArrayName);
				cw.Put("Require(',');\n");
				cw.Put("{0}[{1}] = ", tempArrayName, tempIndexName);
				GenerateValue(t.GetElementType(), String.Format("{0}[{1}]", tempArrayName, tempIndexName));
				cw.PutEndBlock();
				cw.Put("{0} = {1};\n", name, tempArrayName);
				cw.PutEndBlock();
				cw.Put("Require(']');\n");
				cw.PutEndBlock();
				return;
			}
			var idict = Utils.GetIDictionary(t);
			if (idict != null) {
				PutRequireOrNull('{', t, name);
				GenerateDictionary(t, idict, name);
				cw.PutEndBlock();
				return;
			}
			var icoll = Utils.GetICollection(t);
			if (icoll != null) {
				PutRequireOrNull('[', t, name);
				GenerateCollection(t, icoll, name);
				cw.PutEndBlock();
				return;
			}
			if (t.IsClass || t.IsInterface || Utils.IsStruct(t)) {
				var meta = Meta.Get(t, Options);
				cw.PutPart("{0}.Instance.{1}\n", GetDeserializerName(t), GenerateFromReader(meta));
				return;
			}
			throw new NotImplementedException(t.Name);
		}

		private void GenAssigns(string name, object obj)
		{
			var def = Utils.CreateObject(obj.GetType());
			var assigns = obj.GetType().GetMembers().
				Where(m => !m.IsDefined(typeof(ObsoleteAttribute))).
				Select(m => {
					if (m.MemberType == MemberTypes.Field) {
						var f = (FieldInfo)m;
						var v = f.GetValue(obj);
						var defv = f.GetValue(def);
						return (f.Name, v, defv);
					}
					else if (m.MemberType == MemberTypes.Property) {
						var p = (PropertyInfo)m;
						if (!p.CanWrite) return (null, null, null);
						var v = p.GetValue(obj, Utils.ZeroObjects);
						var defv = p.GetValue(def, Utils.ZeroObjects);
						return (p.Name, v, defv);
					}
					else
						return (null, null, null);
				}).
				Where(line => line.Name != null);
			foreach (var (Name, v, defv) in assigns) {
				if (v?.Equals(defv) ?? defv == null) continue;
				var vcode = Utils.CodeValueFormat(v);
				if (vcode != "") // TODO
					cw.Put("{0}.{1} = {2};\n", name, Name, vcode);
			}
		}

		public void Generate<T>() { Generate(typeof(T)); }

		public void Generate(Type t)
		{
			var meta = Meta.Get(t, Options);

			if (lastNameSpace != t.Namespace) {
				if (lastNameSpace != "")
					cw.PutEndBlock();
				cw.Put("\n");
				lastNameSpace = t.Namespace;
				cw.Put("namespace {0}.{1}\n", wrapperNameSpace, lastNameSpace);
				cw.Put("{\n");
			}

			var deserializerName = Utils.GetMangledTypeName(t) + "_JsonDeserializer";
			cw.Put("class {0} : JsonDeserializerGenBase\n", deserializerName);
			cw.Put("{\n");

			cw.Put("public static new {0} Instance = new {0}();\n", deserializerName);
			cw.Put("\n");

			cw.Put("public {0}()\n", deserializerName);
			cw.Put("{\n");
			GenAssigns("Options", Options);
			GenAssigns("JsonOptions", JsonOptions);
			cw.PutEndBlock();
			cw.Put("\n");

			var icoll = Utils.GetICollection(t);
			var typeSpec = Utils.GetTypeSpec(t);
			cw.Put("public override object FromReaderInt()\n");
			cw.Put("{\n");
			if (icoll != null)
				cw.Put("return FromReaderInt(new {0}());\n", typeSpec);
			else
				cw.Put("return {0}\n", GenerateFromReader(meta));
			cw.PutEndBlock();
			cw.Put("\n");

			if (icoll != null) {
				cw.Put("public override object FromReaderInt(object obj)\n");
				cw.Put("{\n");
				cw.Put("var result = ({0})obj;\n", typeSpec);
				cw.Put("Require('[');\n");
				GenerateCollection(t, icoll, "result");
				cw.Put("return result;\n");
				cw.PutEndBlock();
				cw.Put("\n");
			}

			cw.Put("public override object FromReaderIntPartial(string name)\n");
			cw.Put("{\n");
			if (t.IsInterface || t.IsAbstract)
				cw.Put("return null;\n");
			else if (meta.FactoryMethod == null)
				cw.Put("return ReadFields(new {0}(), name);\n", typeSpec);
			else
				cw.Put("return ReadFields({0}.{1}(), name);\n", typeSpec, meta.FactoryMethod.Name);
			cw.PutEndBlock();
			cw.Put("\n");

			cw.Put("protected override object ReadFields(object obj, string name)\n");
			cw.Put("{\n");
			cw.Put("var result = ({0})obj;\n", typeSpec);
			if (icoll == null) {
				cw.GenerateActionList(meta.BeforeDeserialization);
				cw.ResetTempNames();
				foreach (var yi in meta.Items) {
					if (yi.IsOptional) {
						cw.Put("if (\"{0}\" == name) {{\n", yi.Tag(Options));
						if (yi.SetValue != null)
							cw.Put("result.{0} = ", yi.Name);
					}
					else {
						cw.Put("if (\"{0}\" != name) throw new YuzuException(\"{0}!=\" + name);\n",
							yi.Tag(Options));
						if (yi.SetValue != null)
							cw.Put("result.{0} = ", yi.Name);
					}
					if (yi.SetValue != null)
						GenerateValue(yi.Type, "result." + yi.Name);
					else
						GenerateMerge(yi.Type, "result." + yi.Name);
					cw.Put("name = GetNextName(false);\n");
					if (yi.IsOptional)
						cw.PutEndBlock();
				}
				cw.GenerateActionList(meta.AfterDeserialization);
			}
			cw.Put("return result;\n");
			cw.PutEndBlock();

			if (meta.IsCompact) {
				cw.Put("\n");
				cw.Put("protected override object ReadFieldsCompact(object obj)\n");
				cw.Put("{\n");
				cw.Put("var result = ({0})obj;\n", typeSpec);
				cw.GenerateActionList(meta.BeforeDeserialization);
				bool isFirst = true;
				cw.ResetTempNames();
				foreach (var yi in meta.Items) {
					if (!isFirst)
						cw.Put("Require(',');\n");
					isFirst = false;
					if (yi.SetValue != null) {
						cw.Put("result.{0} = ", yi.Name);
						GenerateValue(yi.Type, "result." + yi.Name);
					}
					else
						GenerateMerge(yi.Type, "result." + yi.Name);
				}
				cw.Put("Require(']');\n");
				cw.GenerateActionList(meta.AfterDeserialization);
				cw.Put("return result;\n");
				cw.PutEndBlock();
			}
			cw.PutEndBlock();
			cw.Put("\n");
		}

		public override object FromReaderInt(object obj)
		{
			return FromReaderIntGenerated(obj);
		}

		public override object FromReaderIntPartial(string name)
		{
			throw new NotSupportedException();
		}

		protected override string GetWrapperNamespace()
		{
			return wrapperNameSpace;
		}

	}
}
