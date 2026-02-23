using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Simple key=value format for .lvc files
/// Replaces JSON for all .lvc file types
/// </summary>
public static class LvcSerializer
{
    /// <summary>
    /// Read key=value pairs from an .lvc file
    /// </summary>
    public static Dictionary<string, string> Read(string path)
    {
        var result = new Dictionary<string, string>();
        
        if (!File.Exists(path))
            return result;

        try
        {
            var lines = File.ReadAllLines(path);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                    continue;

                var eqIndex = trimmed.IndexOf('=');
                if (eqIndex <= 0)
                    continue;

                var key = trimmed.Substring(0, eqIndex).Trim();
                var value = trimmed.Substring(eqIndex + 1).Trim();
                
                // Remove quotes if present
                if (value.StartsWith('"') && value.EndsWith('"') && value.Length > 1)
                    value = value.Substring(1, value.Length - 2);
                
                result[key] = value;
            }
        }
        catch (Exception ex)
        {
            // Log but return empty dict
            Console.WriteLine($"Failed to read LVC file {path}: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Write key=value pairs to an .lvc file
    /// </summary>
    public static void Write(string path, Dictionary<string, string> data)
    {
        try
        {
            var lines = new List<string>();
            foreach (var kvp in data)
            {
                var value = kvp.Value;
                // Quote values containing spaces or special chars
                if (value.Contains(' ') || value.Contains('\t') || value.Contains('"') || value.Contains('\n'))
                {
                    value = $"\"{value.Replace("\"", "\\\"")}\"";
                }
                lines.Add($"{kvp.Key}={value}");
            }
            
            File.WriteAllLines(path, lines);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to write LVC file {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if file is JSON format (starts with { or [)
    /// </summary>
    public static bool IsJsonFormat(string path)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            var content = File.ReadAllText(path);
            content = content.TrimStart();
            return content.StartsWith('{') || content.StartsWith('[');
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Convert JSON .lvc to key=value format
    /// </summary>
    public static void ConvertJsonToLvc(string jsonPath, string lvcPath)
    {
        try
        {
            if (!File.Exists(jsonPath))
                return;

            var json = File.ReadAllText(jsonPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var data = new Dictionary<string, string>();

            // Convert JSON properties to key=value pairs
            foreach (var property in root.EnumerateObject())
            {
                var key = property.Name;
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? "",
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => property.Value.GetRawText()
                };
                
                data[key] = value;
            }

            Write(lvcPath, data);
            
            // Create backup of original JSON
            var backupPath = jsonPath + ".bak";
            File.Copy(jsonPath, backupPath, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to convert JSON to LVC {jsonPath}: {ex.Message}");
        }
    }
}
