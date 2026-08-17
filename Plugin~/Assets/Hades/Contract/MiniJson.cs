// Hades.Contract is compiled twice: once into the .NET 10 app (this build), and once more
// inside a Unity Editor plugin. Unity 6000.3's C# compiler caps out at C# 9, so every file in
// this project is pinned to that language level via <LangVersion>9.0</LangVersion> in
// Hades.Contract.csproj - required members, file-scoped namespaces, raw string literals, list
// patterns, and primary constructors on non-record types are all off the table here even
// though the rest of the app uses them freely.
//
// That is why this file uses a block-scoped namespace and ordinary mutable { get; set; }
// properties instead of the file-scoped namespaces and `required` init-only properties used
// everywhere else in Hades. This is deliberate, not an oversight: a later "cleanup" pass that
// modernizes these files to match the rest of the app would compile fine here and then fail,
// silently, only inside Unity - where nobody is watching the build output. Leave this project
// on C# 9 style even when it looks inconsistent with its neighbors.
//
// MiniJson is the one JSON codec both sides compile - the app does not use System.Text.Json on
// this path, on purpose, so there is exactly one place that knows how to read and write the
// wire format instead of two implementations that can quietly drift apart.
//
// Two properties this codec is held to, because the audit that motivated this design found
// exactly these bugs before:
//   1. Parsing NEVER throws. The public entry points (MiniJson.TryParse) return a bool plus an
//      error string for any malformed input - truncated lines, unterminated strings, invalid
//      UTF-8, whatever a corrupt or hostile peer sends. This codec sits directly on the stdio
//      pipe to the Unity Editor; an unhandled exception here takes down the I/O thread and the
//      connection with it.
//   2. Numbers never lose precision. Unity file IDs are `long` and routinely exceed what a
//      `double` can represent exactly, so integer-looking tokens are parsed as `long`, not as
//      `double`, and only fall back to `double` when they do not fit (or contain a decimal
//      point / exponent).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Hades.Contract.Wire
{
    public enum JsonValueKind
    {
        Null,
        Boolean,
        Integer,
        Float,
        String,
        Array,
        Object
    }

    /// <summary>
    /// A parsed (or hand-built) JSON value tree. Numbers keep whichever of <see cref="long"/> or
    /// <see cref="double"/> the token on the wire actually was - see <see cref="JsonValueKind.Integer"/>
    /// vs <see cref="JsonValueKind.Float"/> - so a Unity file ID round-trips exactly instead of being
    /// silently widened into a double and losing precision.
    ///
    /// Construction and the "as" accessors below are for code we control end to end (building an
    /// outgoing message, reading a value whose shape we just validated) - a Kind mismatch there is a
    /// programmer error and throws <see cref="InvalidOperationException"/> immediately, same as
    /// System.Text.Json's JsonElement. That is a different contract from <see cref="MiniJson.TryParse(string,out JsonValue,out string)"/>,
    /// which is the boundary that faces the untrusted wire and therefore never throws.
    /// </summary>
    public sealed class JsonValue
    {
        public JsonValueKind Kind { get; }

        readonly bool _boolValue;
        readonly long _longValue;
        readonly double _doubleValue;
        readonly string? _stringValue;
        readonly List<JsonValue>? _items;
        readonly List<KeyValuePair<string, JsonValue>>? _members;

        JsonValue(JsonValueKind kind, bool boolValue = false, long longValue = 0, double doubleValue = 0,
            string? stringValue = null, List<JsonValue>? items = null, List<KeyValuePair<string, JsonValue>>? members = null)
        {
            Kind = kind;
            _boolValue = boolValue;
            _longValue = longValue;
            _doubleValue = doubleValue;
            _stringValue = stringValue;
            _items = items;
            _members = members;
        }

        public static readonly JsonValue Null = new JsonValue(JsonValueKind.Null);
        public static readonly JsonValue True = new JsonValue(JsonValueKind.Boolean, boolValue: true);
        public static readonly JsonValue False = new JsonValue(JsonValueKind.Boolean, boolValue: false);

        public static JsonValue Bool(bool value) => value ? True : False;

        public static JsonValue Integer(long value) => new JsonValue(JsonValueKind.Integer, longValue: value);

        public static JsonValue Float(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentException("JSON has no representation for NaN or Infinity.", nameof(value));
            return new JsonValue(JsonValueKind.Float, doubleValue: value);
        }

        public static JsonValue String(string? value) => value is null ? Null : new JsonValue(JsonValueKind.String, stringValue: value);

        public static JsonValue NewArray() => new JsonValue(JsonValueKind.Array, items: new List<JsonValue>());

        public static JsonValue NewObject() => new JsonValue(JsonValueKind.Object, members: new List<KeyValuePair<string, JsonValue>>());

        /// <summary>Appends to an array-kind value and returns <c>this</c>, for fluent building. A null item is stored as JSON null.</summary>
        public JsonValue Add(JsonValue? item)
        {
            if (Kind != JsonValueKind.Array || _items is null)
                throw new InvalidOperationException($"Add is only valid on an array JsonValue, not {Kind}.");
            _items.Add(item ?? Null);
            return this;
        }

        /// <summary>Appends a member to an object-kind value and returns <c>this</c>, for fluent building. A null value is stored as JSON null.</summary>
        public JsonValue SetProperty(string name, JsonValue? value)
        {
            if (Kind != JsonValueKind.Object || _members is null)
                throw new InvalidOperationException($"SetProperty is only valid on an object JsonValue, not {Kind}.");
            _members.Add(new KeyValuePair<string, JsonValue>(name, value ?? Null));
            return this;
        }

        public bool AsBoolean() =>
            Kind == JsonValueKind.Boolean ? _boolValue : throw new InvalidOperationException($"Value is {Kind}, not Boolean.");

        /// <summary>Valid only for <see cref="JsonValueKind.Integer"/> - use this for anything that must keep exact long precision (Unity file IDs).</summary>
        public long AsInteger() =>
            Kind == JsonValueKind.Integer ? _longValue : throw new InvalidOperationException($"Value is {Kind}, not Integer.");

        /// <summary>Valid for <see cref="JsonValueKind.Float"/> or <see cref="JsonValueKind.Integer"/> (widened) - use this when any JSON number will do.</summary>
        public double AsDouble() => Kind switch
        {
            JsonValueKind.Float => _doubleValue,
            JsonValueKind.Integer => _longValue,
            _ => throw new InvalidOperationException($"Value is {Kind}, not a number.")
        };

        public string AsString() =>
            Kind == JsonValueKind.String && _stringValue is not null ? _stringValue : throw new InvalidOperationException($"Value is {Kind}, not String.");

        public IReadOnlyList<JsonValue> Items =>
            Kind == JsonValueKind.Array && _items is not null ? _items : throw new InvalidOperationException($"Value is {Kind}, not Array.");

        public IReadOnlyList<KeyValuePair<string, JsonValue>> Members =>
            Kind == JsonValueKind.Object && _members is not null ? _members : throw new InvalidOperationException($"Value is {Kind}, not Object.");

        /// <summary>First member matching <paramref name="name"/> (ordinal match), or false when this is not an object or has no such member.</summary>
        public bool TryGetProperty(string name, out JsonValue? value)
        {
            if (Kind == JsonValueKind.Object && _members is not null)
            {
                for (var i = 0; i < _members.Count; i++)
                {
                    if (string.Equals(_members[i].Key, name, StringComparison.Ordinal))
                    {
                        value = _members[i].Value;
                        return true;
                    }
                }
            }

            value = null;
            return false;
        }
    }

    /// <summary>
    /// A hand-rolled, non-throwing JSON codec for the MCP stdio wire format: one JSON value per
    /// line. See the file banner above for why this exists instead of System.Text.Json, and why
    /// it looks the way it does.
    /// </summary>
    public static class MiniJson
    {
        /// <summary>
        /// Maximum container nesting depth the parser will follow before failing gracefully.
        /// Matches the long-standing default MaxDepth in both System.Text.Json and
        /// Newtonsoft.Json (64), so it rejects the same inputs a caller migrating from either
        /// would already expect to be rejected. The point is not the exact number - it is that
        /// there is a bound at all, so a hostile or corrupt peer sending "[[[[[...” cannot grow
        /// the recursive-descent parser's call stack without limit and crash the Editor.
        /// </summary>
        public const int MaxDepth = 64;

        public static bool TryParse(string? text, out JsonValue? value, out string? error)
        {
            if (text is null)
            {
                value = null;
                error = "Input is null.";
                return false;
            }

            try
            {
                var parser = new Parser(text);
                if (!parser.ParseValue(0, out value, out error)) return false;

                parser.SkipWhitespace();
                if (!parser.AtEnd)
                {
                    value = null;
                    error = $"Unexpected trailing content at position {parser.Position}.";
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                // Belt and suspenders: the parser above is written to fail gracefully on every
                // malformed input it was designed against, but this boundary faces an untrusted
                // peer directly, so an unanticipated bug here must still turn into a parse
                // failure instead of an exception that could take down the I/O thread.
                value = null;
                error = "Unexpected parser failure: " + e.Message;
                return false;
            }
        }

        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        public static bool TryParse(byte[]? utf8Bytes, out JsonValue? value, out string? error)
        {
            if (utf8Bytes is null)
            {
                value = null;
                error = "Input is null.";
                return false;
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(utf8Bytes);
            }
            catch (Exception e)
            {
                value = null;
                error = "Input is not valid UTF-8: " + e.Message;
                return false;
            }

            return TryParse(text, out value, out error);
        }

        public static string Write(JsonValue value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, JsonValue value, int depth)
        {
            if (depth > MaxDepth)
                throw new InvalidOperationException("Maximum nesting depth exceeded while writing JSON - is the value graph self-referential?");

            switch (value.Kind)
            {
                case JsonValueKind.Null:
                    sb.Append("null");
                    break;
                case JsonValueKind.Boolean:
                    sb.Append(value.AsBoolean() ? "true" : "false");
                    break;
                case JsonValueKind.Integer:
                    sb.Append(value.AsInteger().ToString(CultureInfo.InvariantCulture));
                    break;
                case JsonValueKind.Float:
                    WriteFloat(sb, value.AsDouble());
                    break;
                case JsonValueKind.String:
                    WriteString(sb, value.AsString());
                    break;
                case JsonValueKind.Array:
                    WriteArray(sb, value, depth);
                    break;
                case JsonValueKind.Object:
                    WriteObject(sb, value, depth);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown JsonValueKind: {value.Kind}.");
            }
        }

        static void WriteFloat(StringBuilder sb, double d)
        {
            // .NET's round-trippable default formatting drops the fractional part entirely for
            // whole-number doubles (100.0 -> "100"), which would silently reparse as
            // JsonValueKind.Integer. Force at least one fractional digit so Float survives as
            // Float.
            var text = d.ToString(CultureInfo.InvariantCulture);
            if (text.IndexOf('.') < 0 && text.IndexOf('e') < 0 && text.IndexOf('E') < 0)
                text += ".0";
            sb.Append(text);
        }

        static void WriteArray(StringBuilder sb, JsonValue array, int depth)
        {
            sb.Append('[');
            var items = array.Items;
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteValue(sb, items[i], depth + 1);
            }
            sb.Append(']');
        }

        static void WriteObject(StringBuilder sb, JsonValue obj, int depth)
        {
            sb.Append('{');
            var members = obj.Members;
            for (var i = 0; i < members.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteString(sb, members[i].Key);
                sb.Append(':');
                WriteValue(sb, members[i].Value, depth + 1);
            }
            sb.Append('}');
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
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            // Valid non-ASCII characters (including surrogate pair halves) pass
                            // through untouched - .NET strings are UTF-16 already, so copying each
                            // char verbatim reproduces the original text exactly once this is
                            // UTF-8 encoded for the wire.
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>
        /// Hand-written recursive-descent parser over a string. Never throws: every failure path
        /// sets an error message and returns false instead. Depth is checked before each
        /// container is entered, so a deeply-nested input fails fast instead of growing the
        /// native call stack without bound.
        /// </summary>
        struct Parser
        {
            readonly string _text;
            int _pos;

            public Parser(string text)
            {
                _text = text;
                _pos = 0;
            }

            public int Position => _pos;
            public bool AtEnd => _pos >= _text.Length;

            public void SkipWhitespace()
            {
                while (_pos < _text.Length)
                {
                    var c = _text[_pos];
                    if (c != ' ' && c != '\t' && c != '\n' && c != '\r') break;
                    _pos++;
                }
            }

            public bool ParseValue(int depth, out JsonValue? value, out string? error)
            {
                value = null;
                error = null;
                SkipWhitespace();

                if (AtEnd)
                {
                    error = "Unexpected end of input.";
                    return false;
                }

                var c = _text[_pos];
                switch (c)
                {
                    case '{': return ParseObject(depth, out value, out error);
                    case '[': return ParseArray(depth, out value, out error);
                    case '"':
                        if (!ParseString(out var s, out error)) return false;
                        value = JsonValue.String(s);
                        return true;
                    case 't': return ParseLiteral("true", JsonValue.True, out value, out error);
                    case 'f': return ParseLiteral("false", JsonValue.False, out value, out error);
                    case 'n': return ParseLiteral("null", JsonValue.Null, out value, out error);
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(out value, out error);
                        error = $"Unexpected character '{c}' at position {_pos}.";
                        return false;
                }
            }

            bool ParseLiteral(string literal, JsonValue result, out JsonValue? value, out string? error)
            {
                value = null;
                if (_pos + literal.Length > _text.Length || string.CompareOrdinal(_text, _pos, literal, 0, literal.Length) != 0)
                {
                    error = $"Invalid literal at position {_pos}.";
                    return false;
                }
                _pos += literal.Length;
                value = result;
                error = null;
                return true;
            }

            bool ParseObject(int depth, out JsonValue? value, out string? error)
            {
                value = null;
                error = null;

                if (depth >= MaxDepth)
                {
                    error = $"Maximum nesting depth ({MaxDepth}) exceeded at position {_pos}.";
                    return false;
                }

                _pos++; // consume '{'
                var obj = JsonValue.NewObject();
                SkipWhitespace();

                if (!AtEnd && _text[_pos] == '}')
                {
                    _pos++;
                    value = obj;
                    return true;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || _text[_pos] != '"')
                    {
                        error = $"Expected string key at position {_pos}.";
                        return false;
                    }
                    if (!ParseString(out var key, out error)) return false;

                    SkipWhitespace();
                    if (AtEnd || _text[_pos] != ':')
                    {
                        error = $"Expected ':' at position {_pos}.";
                        return false;
                    }
                    _pos++;

                    if (!ParseValue(depth + 1, out var propValue, out error)) return false;
                    obj.SetProperty(key!, propValue);

                    SkipWhitespace();
                    if (AtEnd)
                    {
                        error = "Unexpected end of input inside object.";
                        return false;
                    }

                    var next = _text[_pos];
                    if (next == ',') { _pos++; continue; }
                    if (next == '}') { _pos++; break; }

                    error = $"Expected ',' or '}}' at position {_pos}.";
                    return false;
                }

                value = obj;
                return true;
            }

            bool ParseArray(int depth, out JsonValue? value, out string? error)
            {
                value = null;
                error = null;

                if (depth >= MaxDepth)
                {
                    error = $"Maximum nesting depth ({MaxDepth}) exceeded at position {_pos}.";
                    return false;
                }

                _pos++; // consume '['
                var array = JsonValue.NewArray();
                SkipWhitespace();

                if (!AtEnd && _text[_pos] == ']')
                {
                    _pos++;
                    value = array;
                    return true;
                }

                while (true)
                {
                    if (!ParseValue(depth + 1, out var item, out error)) return false;
                    array.Add(item);

                    SkipWhitespace();
                    if (AtEnd)
                    {
                        error = "Unexpected end of input inside array.";
                        return false;
                    }

                    var next = _text[_pos];
                    if (next == ',') { _pos++; continue; }
                    if (next == ']') { _pos++; break; }

                    error = $"Expected ',' or ']' at position {_pos}.";
                    return false;
                }

                value = array;
                return true;
            }

            bool ParseString(out string? value, out string? error)
            {
                value = null;
                error = null;
                _pos++; // consume opening '"'

                var sb = new StringBuilder();
                while (true)
                {
                    if (AtEnd)
                    {
                        error = "Unterminated string: reached end of input before closing '\"'.";
                        return false;
                    }

                    var c = _text[_pos];

                    if (c == '"')
                    {
                        _pos++;
                        value = sb.ToString();
                        return true;
                    }

                    if (c < 0x20)
                    {
                        error = $"Unescaped control character at position {_pos} inside string.";
                        return false;
                    }

                    if (c == '\\')
                    {
                        _pos++;
                        if (AtEnd)
                        {
                            error = "Unterminated string: input ends after an escape character.";
                            return false;
                        }

                        var escape = _text[_pos];
                        switch (escape)
                        {
                            case '"': sb.Append('"'); _pos++; break;
                            case '\\': sb.Append('\\'); _pos++; break;
                            case '/': sb.Append('/'); _pos++; break;
                            case 'b': sb.Append('\b'); _pos++; break;
                            case 'f': sb.Append('\f'); _pos++; break;
                            case 'n': sb.Append('\n'); _pos++; break;
                            case 'r': sb.Append('\r'); _pos++; break;
                            case 't': sb.Append('\t'); _pos++; break;
                            case 'u':
                                _pos++;
                                if (_pos + 4 > _text.Length)
                                {
                                    error = "Unterminated \\u escape.";
                                    return false;
                                }
                                if (!ushort.TryParse(_text.AsSpan(_pos, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                                {
                                    error = $"Invalid \\u escape at position {_pos}.";
                                    return false;
                                }
                                _pos += 4;

                                // A lone (unpaired) UTF-16 surrogate half decodes into an
                                // ill-formed .NET string - accepted silently before this check, but
                                // a strict downstream consumer (System.Text.Json) throws on it
                                // later, far from this parse call. Rejected here instead, the same
                                // treatment every other malformed escape above already gets.
                                if (char.IsLowSurrogate((char)code))
                                {
                                    error = $"Unpaired low surrogate \\u{code:x4} at position {_pos - 4}.";
                                    return false;
                                }

                                if (char.IsHighSurrogate((char)code))
                                {
                                    // Well-formed only when immediately followed by a \u escape for
                                    // its matching low surrogate - anything else (plain text, a
                                    // different escape, end of string) leaves it unpaired.
                                    if (_pos + 6 > _text.Length || _text[_pos] != '\\' || _text[_pos + 1] != 'u'
                                        || !ushort.TryParse(_text.AsSpan(_pos + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var low)
                                        || !char.IsLowSurrogate((char)low))
                                    {
                                        error = $"Unpaired high surrogate \\u{code:x4} at position {_pos - 4}.";
                                        return false;
                                    }

                                    sb.Append((char)code);
                                    sb.Append((char)low);
                                    _pos += 6;
                                    break;
                                }

                                sb.Append((char)code);
                                break;
                            default:
                                error = $"Invalid escape sequence '\\{escape}' at position {_pos}.";
                                return false;
                        }
                        continue;
                    }

                    sb.Append(c);
                    _pos++;
                }
            }

            bool ParseNumber(out JsonValue? value, out string? error)
            {
                value = null;
                error = null;
                var start = _pos;
                var isFloat = false;

                if (!AtEnd && _text[_pos] == '-') _pos++;

                if (AtEnd || !IsDigit(_text[_pos]))
                {
                    error = $"Invalid number at position {start}.";
                    return false;
                }

                if (_text[_pos] == '0')
                {
                    // JSON's grammar only allows a single leading zero ("0", "0.5") - "01" is not
                    // a valid number token, so unlike below we deliberately do not loop here.
                    _pos++;
                }
                else
                {
                    while (!AtEnd && IsDigit(_text[_pos])) _pos++;
                }

                if (!AtEnd && _text[_pos] == '.')
                {
                    isFloat = true;
                    _pos++;
                    if (AtEnd || !IsDigit(_text[_pos]))
                    {
                        error = $"Invalid number at position {start}: expected digit after '.'.";
                        return false;
                    }
                    while (!AtEnd && IsDigit(_text[_pos])) _pos++;
                }

                if (!AtEnd && (_text[_pos] == 'e' || _text[_pos] == 'E'))
                {
                    isFloat = true;
                    _pos++;
                    if (!AtEnd && (_text[_pos] == '+' || _text[_pos] == '-')) _pos++;
                    if (AtEnd || !IsDigit(_text[_pos]))
                    {
                        error = $"Invalid number at position {start}: expected digit in exponent.";
                        return false;
                    }
                    while (!AtEnd && IsDigit(_text[_pos])) _pos++;
                }

                var token = _text.Substring(start, _pos - start);

                if (!isFloat)
                {
                    if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    {
                        value = JsonValue.Integer(l);
                        return true;
                    }
                    // Doesn't fit in a long (e.g. bigger than long.MaxValue) - fall back to double
                    // rather than failing outright.
                }

                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    value = JsonValue.Float(d);
                    return true;
                }

                error = $"Invalid number '{token}' at position {start}.";
                return false;
            }

            static bool IsDigit(char c) => c >= '0' && c <= '9';
        }
    }
}
