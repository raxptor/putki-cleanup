﻿using System;
using System.Collections.Generic;

namespace Putki
{
	public static class MicroJson
	{
		public class Object
		{
			public Dictionary<String, object> Data;
		}

		public class Array
		{
			public List<object> Data;
		}

		public struct ParseStatus
		{
			public byte[] data;
			public int pos;
			public bool error;
		}

		public delegate void OnField(string name);
		public delegate void OnArrayEntry(string entry);

		enum Parsing
		{
			NOTHING,
			VALUE,
			QUOTED_VALUE,
			OBJECT,
			ARRAY
		};

		public static String DecodeString(byte[] buf, int begin, int end)
		{
			return System.Text.Encoding.ASCII.GetString(buf, begin, end-begin);
		}

		public static bool IsWhitespace(char c)
		{
			return c == ' ' || c == '\t' || c == 0xD || c == 0xA;
		}

		public static object Parse(ref ParseStatus status)
		{
			Parsing state = Parsing.NOTHING;
			Object o = null;
			Array a = null;
			String name = null;
			for (int i=status.pos;i<status.data.Length;i++)
			{
				byte b = status.data[i];
				char c = (char)b;
				switch (state)
				{
					case Parsing.NOTHING:
					{
						switch (c)
						{
							case '{': state = Parsing.OBJECT; o = new Object(); o.Data = new Dictionary<string, object>(); break;
							case '[': state = Parsing.ARRAY; a = new Array(); a.Data = new List<object>(); break;
							case ' ': case '\n': case '\t': break;
							case '"': state = Parsing.QUOTED_VALUE; status.pos = i+1; break;
							default: state = Parsing.VALUE; status.pos = i; break;
						}
						break;
					}
					case Parsing.QUOTED_VALUE:
					{
						if (c == '"')
						{
							String v = DecodeString(status.data, status.pos, i);
							status.pos = i + 1;
							return v;
						}
						break;
					}
					case Parsing.VALUE:
						{
							if (IsWhitespace(c) || c == ',' || c == ']' || c == '}' || c == ':')
							{
								String v = DecodeString(status.data, status.pos, i);
								status.pos = i;
								return v;
							}
							break;
						}
					case Parsing.OBJECT:
						{
							if (c == '}')
							{
								status.pos = i + 1;
								return o;
							}
							if (IsWhitespace(c) || c == ',')
							{
								continue;
							}
							if (name == null)
							{
								status.pos = i;
								name = Parse(ref status) as String;
								if (name == null)
								{
									status.error = true;
									return null;
								}
								i = status.pos - 1;
							}
							else 
							{
								if (c == ':')
								{
									continue;
								}
								status.pos = i;
								object val = Parse(ref status);
								if (val == null)
								{
									status.error = true;
									return null;
								}
								o.Data.Add(name, val);
								i = status.pos - 1;
								name = null;
							}
							break;
						}
					case Parsing.ARRAY:
						{
							if (c == ']')
							{
								status.pos = i + 1;
								return a;
							}
							if (IsWhitespace(c) || c == ',')
							{
								continue;
							}
							status.pos = i;
							object val = Parse(ref status);
							if (val == null)
							{
								status.error = true;
								return null;
							}
							a.Data.Add(val);
							i = status.pos - 1;
							break;
						}
					default:
						break;
				}
			}
			status.error = true;
			return null;
		}

		public static Object Parse(byte[] buffer)
		{
			ParseStatus status = new ParseStatus();
			status.data = buffer;
			status.pos = 0;
			object root = Parse(ref status);
			if (status.error)
			{
				return null;
			}
			else
			{
				return root as MicroJson.Object;
			}
		}
	}
}
