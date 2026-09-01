using System;
using System.Collections.Generic;
using System.IO;

namespace LatticeVeilMonoGame.Core.Modding;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed class ModDiagnostic
{
    public ModDiagnostic(
        string filePath,
        int lineNumber,
        DiagnosticSeverity severity,
        string code,
        string message,
        string fixHint)
    {
        FilePath = filePath;
        LineNumber = lineNumber;
        Severity = severity;
        Code = code;
        Message = message;
        FixHint = fixHint;
    }

    public string FilePath { get; }
    public int LineNumber { get; }
    public DiagnosticSeverity Severity { get; }
    public string Code { get; }
    public string Message { get; }
    public string FixHint { get; }

    public override string ToString()
    {
        var tag = Severity switch
        {
            DiagnosticSeverity.Error => "[ERROR]",
            DiagnosticSeverity.Warning => "[WARN]",
            _ => "[INFO]"
        };

        var lineInfo = LineNumber > 0 ? $" (Line {LineNumber})" : string.Empty;
        var hintInfo = !string.IsNullOrWhiteSpace(FixHint) ? $"\n  -> How to fix: {FixHint}" : string.Empty;
        return $"{tag} {Path.GetFileName(FilePath)}{lineInfo} [{Code}]: {Message}{hintInfo}";
    }
}

public sealed class ParsedKeyValueDocument
{
    public ParsedKeyValueDocument(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }
    public Dictionary<string, (string Value, int LineNumber)> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ModDiagnostic> Diagnostics { get; } = new();

    public bool HasErrors => Diagnostics.Exists(d => d.Severity == DiagnosticSeverity.Error);

    public bool TryGetString(string key, out string value, out int line)
    {
        if (Entries.TryGetValue(key, out var entry) && !string.IsNullOrWhiteSpace(entry.Value))
        {
            value = entry.Value;
            line = entry.LineNumber;
            return true;
        }

        value = string.Empty;
        line = 0;
        return false;
    }

    public string GetString(string key, string defaultValue = "")
    {
        return TryGetString(key, out var val, out _) ? val : defaultValue;
    }

    public int GetInt(string key, int defaultValue = 0)
    {
        if (TryGetString(key, out var val, out var line))
        {
            if (int.TryParse(val, out var result))
                return result;

            Diagnostics.Add(new ModDiagnostic(
                FilePath,
                line,
                DiagnosticSeverity.Warning,
                "LV_INVALID_INT",
                $"Value '{val}' for key '{key}' is not a valid integer.",
                $"Change '{key}={val}' to an integer number (e.g. '{key}={defaultValue}')."));
        }

        return defaultValue;
    }

    public bool GetBool(string key, bool defaultValue = false)
    {
        if (TryGetString(key, out var val, out var line))
        {
            if (bool.TryParse(val, out var result))
                return result;

            if (string.Equals(val, "1", StringComparison.OrdinalIgnoreCase) || string.Equals(val, "yes", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(val, "0", StringComparison.OrdinalIgnoreCase) || string.Equals(val, "no", StringComparison.OrdinalIgnoreCase))
                return false;

            Diagnostics.Add(new ModDiagnostic(
                FilePath,
                line,
                DiagnosticSeverity.Warning,
                "LV_INVALID_BOOL",
                $"Value '{val}' for key '{key}' is not a valid boolean (true/false).",
                $"Use 'true' or 'false' (e.g. '{key}=true')."));
        }

        return defaultValue;
    }

    public List<string> GetStringList(string key)
    {
        var list = new List<string>();
        if (TryGetString(key, out var val, out _))
        {
            var parts = val.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    list.Add(trimmed);
            }
        }
        return list;
    }
}

public static class ModFileParser
{
    public static ParsedKeyValueDocument ParseFile(string filePath)
    {
        var doc = new ParsedKeyValueDocument(filePath);
        if (!File.Exists(filePath))
        {
            doc.Diagnostics.Add(new ModDiagnostic(
                filePath,
                0,
                DiagnosticSeverity.Error,
                "LV_FILE_NOT_FOUND",
                "File could not be found on disk.",
                "Ensure the file exists in your mod directory."));
            return doc;
        }

        try
        {
            var lines = File.ReadAllLines(filePath);
            for (var i = 0; i < lines.Length; i++)
            {
                var lineNum = i + 1;
                var rawLine = lines[i];
                var trimmed = rawLine.Trim();

                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith("//") || trimmed.StartsWith(';'))
                    continue;

                var equalsIdx = trimmed.IndexOf('=');
                if (equalsIdx < 0)
                {
                    doc.Diagnostics.Add(new ModDiagnostic(
                        filePath,
                        lineNum,
                        DiagnosticSeverity.Warning,
                        "LV_SYNTAX_NO_EQUALS",
                        $"Line '{rawLine}' is missing an '=' separator.",
                        "Use the format 'key=value' (e.g. 'name=My Cool Item')."));
                    continue;
                }

                var key = trimmed[..equalsIdx].Trim();
                var value = trimmed[(equalsIdx + 1)..].Trim();

                if (string.IsNullOrEmpty(key))
                {
                    doc.Diagnostics.Add(new ModDiagnostic(
                        filePath,
                        lineNum,
                        DiagnosticSeverity.Error,
                        "LV_SYNTAX_EMPTY_KEY",
                        "Key name cannot be empty before the '=' sign.",
                        "Specify a valid property name before '=' (e.g. 'type=food')."));
                    continue;
                }

                if (doc.Entries.ContainsKey(key))
                {
                    doc.Diagnostics.Add(new ModDiagnostic(
                        filePath,
                        lineNum,
                        DiagnosticSeverity.Warning,
                        "LV_DUPLICATE_KEY",
                        $"Duplicate key '{key}'. The earlier value will be overwritten.",
                        $"Remove duplicate '{key}' lines."));
                }

                doc.Entries[key] = (value, lineNum);
            }
        }
        catch (Exception ex)
        {
            doc.Diagnostics.Add(new ModDiagnostic(
                filePath,
                0,
                DiagnosticSeverity.Error,
                "LV_READ_EXCEPTION",
                $"Failed to read file: {ex.Message}",
                "Check file permissions and ensure the file is not locked by another program."));
        }

        return doc;
    }

    public static ParsedKeyValueDocument ParseString(string text, string virtualPath = "inline.lvc")
    {
        var doc = new ParsedKeyValueDocument(virtualPath);
        using var reader = new StringReader(text);
        string? line;
        var lineNum = 0;

        while ((line = reader.ReadLine()) != null)
        {
            lineNum++;
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith("//") || trimmed.StartsWith(';'))
                continue;

            var equalsIdx = trimmed.IndexOf('=');
            if (equalsIdx < 0)
            {
                doc.Diagnostics.Add(new ModDiagnostic(
                    virtualPath,
                    lineNum,
                    DiagnosticSeverity.Warning,
                    "LV_SYNTAX_NO_EQUALS",
                    $"Line '{line}' is missing an '=' separator.",
                    "Use key=value syntax."));
                continue;
            }

            var key = trimmed[..equalsIdx].Trim();
            var value = trimmed[(equalsIdx + 1)..].Trim();
            if (string.IsNullOrEmpty(key))
                continue;

            doc.Entries[key] = (value, lineNum);
        }

        return doc;
    }
}
