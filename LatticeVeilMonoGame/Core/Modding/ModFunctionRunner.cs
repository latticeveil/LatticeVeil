using System;
using System.Collections.Generic;
using System.IO;

namespace LatticeVeilMonoGame.Core.Modding;

public enum FunctionActionKind
{
    Command,
    SpawnParticle,
    PlaySound,
    GiveItem,
    TransformItem,
    DamageArea,
    ApplyStatus,
    Custom
}

public sealed class FunctionAction
{
    public FunctionActionKind Kind { get; set; }
    public string RawParameter { get; set; } = string.Empty;
    public int LineNumber { get; set; }
}

public sealed class ModFunction
{
    public string Id { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public List<FunctionAction> Actions { get; } = new();
}

public static class ModFunctionRunner
{
    private static readonly Dictionary<string, ModFunction> Functions = new(StringComparer.OrdinalIgnoreCase);

    public static void RegisterFunction(string id, ModFunction func)
    {
        Functions[id] = func;
    }

    public static ModFunction? ParseFunctionFile(string filePath, Logger? log = null)
    {
        if (!File.Exists(filePath))
            return null;

        var func = new ModFunction
        {
            Id = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant(),
            FilePath = filePath
        };

        var lines = File.ReadAllLines(filePath);
        for (var i = 0; i < lines.Length; i++)
        {
            var lineNum = i + 1;
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith("//"))
                continue;

            var equalsIdx = line.IndexOf('=');
            var colonIdx = line.IndexOf(':');
            var sepIdx = (equalsIdx > 0 && colonIdx > 0) ? Math.Min(equalsIdx, colonIdx) : Math.Max(equalsIdx, colonIdx);

            string actionType;
            string param;

            if (sepIdx > 0)
            {
                actionType = line[..sepIdx].Trim().ToLowerInvariant();
                param = line[(sepIdx + 1)..].Trim();
            }
            else
            {
                // Direct command shortcut, e.g. /heal or /say Hello
                if (line.StartsWith('/'))
                {
                    actionType = "command";
                    param = line;
                }
                else
                {
                    actionType = line.ToLowerInvariant();
                    param = string.Empty;
                }
            }

            var kind = actionType switch
            {
                "command" or "cmd" or "exec" => FunctionActionKind.Command,
                "particle" or "spawn_particle" => FunctionActionKind.SpawnParticle,
                "sound" or "play_sound" => FunctionActionKind.PlaySound,
                "give" or "give_item" => FunctionActionKind.GiveItem,
                "transform" or "transform_item" => FunctionActionKind.TransformItem,
                "damage_area" or "damage" => FunctionActionKind.DamageArea,
                "status" or "apply_status" => FunctionActionKind.ApplyStatus,
                _ => FunctionActionKind.Custom
            };

            func.Actions.Add(new FunctionAction
            {
                Kind = kind,
                RawParameter = param,
                LineNumber = lineNum
            });
        }

        RegisterFunction(func.Id, func);
        log?.Info($"[ModLoader] Loaded function '{func.Id}' with {func.Actions.Count} actions from {Path.GetFileName(filePath)}");
        return func;
    }

    public static void Execute(string functionIdOrInline, Action<string>? commandRunner = null, Logger? log = null)
    {
        if (string.IsNullOrWhiteSpace(functionIdOrInline))
            return;

        // Check if inline command
        if (functionIdOrInline.StartsWith('/'))
        {
            commandRunner?.Invoke(functionIdOrInline);
            return;
        }

        // Check registered function by ID
        if (Functions.TryGetValue(functionIdOrInline, out var func))
        {
            foreach (var act in func.Actions)
            {
                switch (act.Kind)
                {
                    case FunctionActionKind.Command:
                        commandRunner?.Invoke(act.RawParameter);
                        break;
                    default:
                        log?.Info($"[ModFunction] Executing action {act.Kind} ({act.RawParameter})");
                        break;
                }
            }
        }
    }
}
