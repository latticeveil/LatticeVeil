using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Key=value serializer for .lvc files (human-readable). This is the ONLY allowed
/// on-disk format for local config/world metadata.
/// </summary>
public static class LvcSerializer
{
    public sealed class LegacyFormatException : Exception
    {
        public LegacyFormatException(string message) : base(message) { }
    }

    public static bool IsJsonFormat(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var fs = File.OpenRead(path);
            int b;
            do { b = fs.ReadByte(); } while (b != -1 && char.IsWhiteSpace((char)b));
            if (b == '{')
                return true;

            if (b != '[')
                return false;

            // Sectioned .lvc manifests start with headers like [WORLD], not JSON arrays.
            var sectionProbe = new StringBuilder();
            while (true)
            {
                b = fs.ReadByte();
                if (b == -1 || b == '\r' || b == '\n')
                    break;

                sectionProbe.Append((char)b);
                if (sectionProbe.Length >= 64)
                    break;
            }

            var probeText = sectionProbe.ToString().Trim();
            if (probeText.Length > 0 && probeText.EndsWith("]", StringComparison.Ordinal))
            {
                var sectionName = probeText.Substring(0, probeText.Length - 1).Trim();
                if (sectionName.Length > 0 && sectionName.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.'))
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static Dictionary<string, string> Read(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;

        if (IsJsonFormat(path))
            throw new LegacyFormatException($"Legacy JSON config detected: {Paths.ToUiPath(path)}");

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#') || line.StartsWith(';')) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line.Substring(0, eq).Trim();
            if (key.Length == 0) continue;

            var value = line.Substring(eq + 1).Trim();
            result[key] = Unquote(value);
        }

        return result;
    }

    public static void Write(string path, IDictionary<string, string> data)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var lines = new List<string>(data.Count);
        foreach (var kv in data.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var key = (kv.Key ?? string.Empty).Trim();
            if (key.Length == 0) continue;

            var value = kv.Value ?? string.Empty;
            lines.Add($"{key}={QuoteIfNeeded(value)}");
        }

        File.WriteAllLines(path, lines);
    }

    public static Dictionary<string, string> SerializeObject(object obj, string? prefix = null)
    {
        prefix ??= string.Empty;
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in obj.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            if (prop.GetIndexParameters().Length != 0) continue;

            var name = prefix + prop.Name;
            var value = prop.GetValue(obj);
            if (value == null) continue;

            var t = prop.PropertyType;

            if (t == typeof(string)) { dict[name] = (string)value; continue; }
            if (t == typeof(bool)) { dict[name] = ((bool)value) ? "true" : "false"; continue; }
            if (t == typeof(int)) { dict[name] = ((int)value).ToString(CultureInfo.InvariantCulture); continue; }
            if (t == typeof(long)) { dict[name] = ((long)value).ToString(CultureInfo.InvariantCulture); continue; }
            if (t == typeof(float)) { dict[name] = ((float)value).ToString("R", CultureInfo.InvariantCulture); continue; }
            if (t == typeof(double)) { dict[name] = ((double)value).ToString("R", CultureInfo.InvariantCulture); continue; }
            if (t.IsEnum) { dict[name] = value.ToString() ?? string.Empty; continue; }

            if (t == typeof(List<string>))
            {
                var list = (List<string>)value;
                // semicolon-separated with escaping \; and \\
                dict[name] = string.Join(";", list.Select(EscapeListItem));
                continue;
            }

            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var args = t.GetGenericArguments();
                if (args[0] == typeof(string))
                {
                    var idict = (System.Collections.IDictionary)value;
                    foreach (var k in idict.Keys)
                    {
                        if (k is not string sk || string.IsNullOrWhiteSpace(sk)) continue;
                        var v = idict[k];
                        if (v == null) continue;

                        if (args[1].IsEnum)
                            dict[$"{name}.{sk}"] = v.ToString() ?? string.Empty;
                        else if (args[1] == typeof(string))
                            dict[$"{name}.{sk}"] = (string)v;
                    }
                    continue;
                }
            }

            // Nested POCO: recurse (only if it has parameterless ctor)
            if (t.IsClass && t != typeof(string) && t.GetConstructor(Type.EmptyTypes) != null)
            {
                var nested = SerializeObject(value, name + ".");
                foreach (var kvp in nested)
                    dict[kvp.Key] = kvp.Value;
            }
        }

        return dict;
    }

    public static void ApplyObject(object obj, Dictionary<string, string> data, string? prefix = null)
    {
        prefix ??= string.Empty;

        foreach (var prop in obj.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            if (prop.GetIndexParameters().Length != 0) continue;

            var name = prefix + prop.Name;
            var t = prop.PropertyType;

            if (t == typeof(string))
            {
                if (data.TryGetValue(name, out var s)) prop.SetValue(obj, s);
                continue;
            }

            if (t == typeof(bool))
            {
                if (data.TryGetValue(name, out var s) && TryParseBool(s, out var b)) prop.SetValue(obj, b);
                continue;
            }

            if (t == typeof(int))
            {
                if (data.TryGetValue(name, out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    prop.SetValue(obj, i);
                continue;
            }

            if (t == typeof(long))
            {
                if (data.TryGetValue(name, out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    prop.SetValue(obj, l);
                continue;
            }

            if (t == typeof(float))
            {
                if (data.TryGetValue(name, out var s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                    prop.SetValue(obj, f);
                continue;
            }

            if (t == typeof(double))
            {
                if (data.TryGetValue(name, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    prop.SetValue(obj, d);
                continue;
            }

            if (t.IsEnum)
            {
                if (data.TryGetValue(name, out var s))
                {
                    try
                    {
                        var ev = Enum.Parse(t, s, ignoreCase: true);
                        prop.SetValue(obj, ev);
                    }
                    catch { /* ignore bad value */ }
                }
                continue;
            }

            if (t == typeof(List<string>))
            {
                if (data.TryGetValue(name, out var s))
                {
                    var list = SplitList(s);
                    prop.SetValue(obj, list);
                }
                continue;
            }

            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var args = t.GetGenericArguments();
                if (args[0] == typeof(string))
                {
                    var ctor = t.GetConstructor(Type.EmptyTypes);
                    if (ctor == null) continue;

                    var dictObj = (System.Collections.IDictionary)(prop.GetValue(obj) ?? ctor.Invoke(null));
                    var prefixKey = name + ".";

                    foreach (var kv in data)
                    {
                        if (!kv.Key.StartsWith(prefixKey, StringComparison.OrdinalIgnoreCase)) continue;
                        var subKey = kv.Key.Substring(prefixKey.Length);
                        if (string.IsNullOrWhiteSpace(subKey)) continue;

                        if (args[1].IsEnum)
                        {
                            try
                            {
                                var ev = Enum.Parse(args[1], kv.Value, ignoreCase: true);
                                dictObj[subKey] = ev;
                            }
                            catch { }
                        }
                        else if (args[1] == typeof(string))
                        {
                            dictObj[subKey] = kv.Value;
                        }
                    }

                    prop.SetValue(obj, dictObj);
                    continue;
                }
            }

            // Nested POCO
            if (t.IsClass && t != typeof(string) && t.GetConstructor(Type.EmptyTypes) != null)
            {
                var nestedObj = prop.GetValue(obj) ?? Activator.CreateInstance(t)!;
                ApplyObject(nestedObj, data, name + ".");
                prop.SetValue(obj, nestedObj);
            }
        }
    }

    private static bool TryParseBool(string s, out bool value)
    {
        s = (s ?? string.Empty).Trim();
        if (bool.TryParse(s, out value)) return true;

        if (s == "1") { value = true; return true; }
        if (s == "0") { value = false; return true; }
        if (string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase)) { value = true; return true; }
        if (string.Equals(s, "no", StringComparison.OrdinalIgnoreCase)) { value = false; return true; }
        value = false;
        return false;
    }

    private static string QuoteIfNeeded(string value)
    {
        if (!NeedsQuotes(value)) return value;
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return "\"" + escaped + "\"";
    }

    private static bool NeedsQuotes(string value)
    {
        if (value.Length == 0) return true;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch)) return true;
            if (ch == '"' || ch == '=' || ch == '#' || ch == ';') return true;
        }
        return false;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
        {
            var inner = value.Substring(1, value.Length - 2);
            // unescape \" and \\
            return inner.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
        return value;
    }

    private static string EscapeListItem(string s)
    {
        s ??= string.Empty;
        return s.Replace("\\", "\\\\").Replace(";", "\\;");
    }

    private static List<string> SplitList(string s)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(s)) return list;

        var cur = new System.Text.StringBuilder();
        bool escape = false;
        foreach (var ch in s)
        {
            if (escape)
            {
                cur.Append(ch);
                escape = false;
                continue;
            }

            if (ch == '\\')
            {
                escape = true;
                continue;
            }

            if (ch == ';')
            {
                list.Add(cur.ToString());
                cur.Clear();
                continue;
            }

            cur.Append(ch);
        }

        list.Add(cur.ToString());
        return list;
    }
}
