using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Stark.Core.Json
{
    /// <summary>
    /// Небольшой JSON-парсер без зависимостей. Нужен потому, что JsonUtility в Unity
    /// не читает словари (partPaths, hints), а Core не должен зависеть от UnityEngine.
    /// Результат: Dictionary&lt;string, object&gt;, List&lt;object&gt;, string, double, bool, null.
    /// </summary>
    public static class MiniJson
    {
        public static object Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var p = new Parser(text);
            p.SkipWs();
            var value = p.ReadValue();
            p.SkipWs();
            if (!p.End) throw p.Error("лишние символы после JSON");
            return value;
        }

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case string s: WriteString(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); break;
                case float f: sb.Append(((double)f).ToString("R", CultureInfo.InvariantCulture)); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case IDictionary<string, object> dict:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        Write(sb, kv.Value);
                    }
                    sb.Append('}');
                    break;
                }
                case System.Collections.IEnumerable list:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (var item in list)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Write(sb, item);
                    }
                    sb.Append(']');
                    break;
                }
                default: throw new ArgumentException("Не умею сериализовать " + v.GetType());
            }
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        sealed class Parser
        {
            readonly string _s;
            int _i;

            public Parser(string s) { _s = s; }
            public bool End => _i >= _s.Length;

            public FormatException Error(string msg) =>
                new FormatException($"JSON: {msg} (позиция {_i})");

            public void SkipWs()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '﻿') _i++;
                    else break;
                }
            }

            public object ReadValue()
            {
                if (End) throw Error("неожиданный конец");
                char c = _s[_i];
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw Error($"неожиданный символ '{c}'");
                }
            }

            void Expect(string word)
            {
                if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) throw Error("ожидалось " + word);
                _i += word.Length;
            }

            Dictionary<string, object> ReadObject()
            {
                var d = new Dictionary<string, object>();
                _i++; // {
                SkipWs();
                if (!End && _s[_i] == '}') { _i++; return d; }
                while (true)
                {
                    SkipWs();
                    if (End || _s[_i] != '"') throw Error("ожидался ключ");
                    string key = ReadString();
                    SkipWs();
                    if (End || _s[_i] != ':') throw Error("ожидалось ':'");
                    _i++;
                    SkipWs();
                    d[key] = ReadValue();
                    SkipWs();
                    if (End) throw Error("незакрытый объект");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == '}') { _i++; return d; }
                    throw Error("ожидалось ',' или '}'");
                }
            }

            List<object> ReadArray()
            {
                var list = new List<object>();
                _i++; // [
                SkipWs();
                if (!End && _s[_i] == ']') { _i++; return list; }
                while (true)
                {
                    SkipWs();
                    list.Add(ReadValue());
                    SkipWs();
                    if (End) throw Error("незакрытый массив");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == ']') { _i++; return list; }
                    throw Error("ожидалось ',' или ']'");
                }
            }

            string ReadString()
            {
                _i++; // "
                var sb = new StringBuilder();
                while (true)
                {
                    if (End) throw Error("незакрытая строка");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (End) throw Error("незакрытая строка");
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) throw Error("обрезанный \\u");
                            sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _i += 4;
                            break;
                        default: throw Error($"неизвестная escape-последовательность \\{e}");
                    }
                }
            }

            double ReadNumber()
            {
                int start = _i;
                if (_s[_i] == '-') _i++;
                while (!End && "0123456789.eE+-".IndexOf(_s[_i]) >= 0) _i++;
                string num = _s.Substring(start, _i - start);
                if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    throw Error("неверное число " + num);
                return v;
            }
        }
    }

    /// <summary>Удобный доступ к дереву MiniJson с понятными ошибками.</summary>
    public static class JsonAccess
    {
        public static Dictionary<string, object> Obj(object o, string what) =>
            o as Dictionary<string, object> ?? throw new FormatException($"{what}: ожидался объект");

        public static List<object> Arr(object o, string what) =>
            o as List<object> ?? throw new FormatException($"{what}: ожидался массив");

        public static object Get(this Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out var v) ? v : null;

        public static string Str(this Dictionary<string, object> d, string key, string fallback = "") =>
            d.Get(key) as string ?? fallback;

        public static double Num(this Dictionary<string, object> d, string key, double fallback = 0) =>
            d.Get(key) is double v ? v : fallback;

        public static double? NumOrNull(this Dictionary<string, object> d, string key) =>
            d.Get(key) is double v ? v : (double?)null;

        public static List<object> ArrOrEmpty(this Dictionary<string, object> d, string key) =>
            d.Get(key) as List<object> ?? new List<object>();

        public static List<string> Strings(this Dictionary<string, object> d, string key)
        {
            var result = new List<string>();
            foreach (var item in d.ArrOrEmpty(key))
                if (item is string s) result.Add(s);
            return result;
        }
    }
}
