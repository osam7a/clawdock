using System.Collections.Generic;

namespace ClaudeNotch.Tests
{
    static class JsonTests
    {
        public static void Survives_a_round_trip()
        {
            string once = Json.Write(Check.Obj("{'a':'x','b':[1,true,null,{'c':'d'}],'e':{}}"));
            Check.Equal(once, Json.Write(Json.ParseObject(once)));
        }

        public static void Keeps_key_order()
        {
            string text = Json.Write(Check.Obj("{'zebra':1,'apple':2,'mango':3}"));
            Check.That(text.IndexOf("zebra") < text.IndexOf("apple") && text.IndexOf("apple") < text.IndexOf("mango"), text);
        }

        public static void Escapes_what_would_break_a_string()
        {
            Check.Equal("\"a\\\"b\\\\c\\nd\\te\\u0001\"", Json.Escape("a\"b\\c\nd\te\u0001"));
            Check.Equal("\"\"", Json.Escape(null));
        }

        public static void Keeps_non_ascii_text()
        {
            var d = Json.ParseObject(Json.Write(new Dictionary<string, object> { { "t", "café ✓ 日本語" } }));
            Check.Equal("café ✓ 日本語", Json.Str(d, "t"));
        }

        public static void Lookups_tolerate_missing_and_wrong_values()
        {
            var d = Check.Obj("{'s':'x','n':5,'o':{},'nothing':null,'word':'abc'}");
            Check.Equal("", Json.Str(d, "missing"));
            Check.Equal("", Json.Str(null, "s"));
            Check.Equal("", Json.Str(d, "nothing"));
            Check.Equal("5", Json.Str(d, "n"));
            Check.Equal(5L, Json.Long(d, "n"));
            Check.Equal(0L, Json.Long(d, "word"));
            Check.Equal(0L, Json.Long(d, "missing"));
            Check.That(Json.Obj(d, "o") != null, "an object is found");
            Check.That(Json.Obj(d, "s") == null, "a string isn't an object");
            Check.That(Json.Obj(null, "o") == null, "nothing to look in");
        }
    }
}
