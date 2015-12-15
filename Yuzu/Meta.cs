﻿using System;
using System.Collections.Generic;
using System.Reflection;

namespace Yuzu
{
	internal class Meta
	{
		private static Dictionary<Tuple<Type, CommonOptions>, Meta> cache =
			new Dictionary<Tuple<Type, CommonOptions>, Meta>();

		internal class Item : IComparable<Item>
		{
			private string id;

			public string Name;
			public string Alias;
			public string Id
			{
				get
				{
					if (id == null)
						id = IdGenerator.GetNextId();
					return id;
				}
			}
			public bool IsOptional;
			public bool IsCompact;
			public Func<object, object, bool> SerializeIf;
			public Type Type;
			public Func<object, object> GetValue;
			public Action<object, object> SetValue;
			public FieldInfo FieldInfo;
			public PropertyInfo PropInfo;

			public int CompareTo(Item yi) { return Alias.CompareTo(yi.Alias); }

			public string Tag(CommonOptions options)
			{
				switch (options.TagMode) {
					case TagMode.Names:
						return Name;
					case TagMode.Aliases:
						return Alias;
					case TagMode.Ids:
						return Id;
					default:
						throw new YuzuAssert();
				}
			}
			public string NameTagged(CommonOptions options)
			{
				var tag = Tag(options);
				return Name + (tag == Name ? "" : " (" + tag + ")");
			}
		}

		public Type Type;
		public List<Item> Items = new List<Item>();

		private Meta(Type t, CommonOptions options)
		{
			Type = t;
			foreach (var m in t.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy)) {
				if (m.MemberType != MemberTypes.Field && m.MemberType != MemberTypes.Property)
					continue;

				var optional = m.GetCustomAttribute(options.OptionalAttribute, false);
				var required = m.GetCustomAttribute(options.RequiredAttribute, false);
				var serializeIf = m.GetCustomAttribute(options.SerializeIfAttribute, true);
				if (optional == null && required == null)
					continue;
				if (optional != null && required != null)
					throw Utils.Error(t, "Both optional and required attributes for field '{0}'", m.Name);
				var item = new Item {
					Alias = options.GetAlias(optional ?? required) ?? m.Name,
					IsOptional = optional != null,
					IsCompact =
						m.IsDefined(options.CompactAttribute) ||
						m.GetType().IsDefined(options.CompactAttribute),
					SerializeIf = serializeIf != null ? options.GetSerializeCondition(serializeIf) : null,
					Name = m.Name,
				};

				if (m.MemberType == MemberTypes.Field) {
					var f = m as FieldInfo;
					item.Type = f.FieldType;
					item.GetValue = f.GetValue;
					item.SetValue = f.SetValue;
					item.FieldInfo = f;
				}
				else {
					var p = m as PropertyInfo;
					item.Type = p.PropertyType;
					item.GetValue = p.GetValue;
					item.SetValue = p.SetValue;
					item.PropInfo = p;
				}

				Items.Add(item);
			}
			if (!options.AllowEmptyTypes && Items.Count == 0)
				throw Utils.Error(t, "No serializable fields");
			Items.Sort();
			var prevTag = "";
			foreach (var i in Items) {
				var tag = i.Tag(options);
				if (tag == "")
					throw Utils.Error(t, "Empty tag for field '{0}'", i.Name);
				foreach (var ch in tag)
					if (ch <= ' ' || ch >= 127)
						throw Utils.Error(t, "Bad character '{0}' in tag for field '{1}'", ch, i.Name);
				if (tag == prevTag)
					throw Utils.Error(t, "Duplicate tag '{0}' for field '{1}'", tag, i.Name);
				prevTag = tag;
			}
		}

		public static Meta Get(Type t, CommonOptions options)
		{
			Meta meta;
			if (cache.TryGetValue(Tuple.Create(t, options), out meta))
				return meta;
			meta = new Meta(t, options);
			cache.Add(Tuple.Create(t, options), meta);
			return meta;
		}
	}

}