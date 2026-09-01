using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace LatticeVeilMonoGame.Core.Modding;

public sealed class ModPackageInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = "Unnamed Mod";
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = "Unknown";
    public string Version { get; set; } = "1.0.0";
    public string GameVersion { get; set; } = "1.0.0";
    public string RootPath { get; set; } = string.Empty;
    public bool IsZip { get; set; }
    public List<ModDiagnostic> Diagnostics { get; } = new();
}

public sealed class ModContentLoader
{
    public static readonly ModContentLoader Instance = new();

    private readonly List<ModPackageInfo> _loadedMods = new();
    private readonly List<ModDiagnostic> _allDiagnostics = new();

    public IReadOnlyList<ModPackageInfo> LoadedMods => _loadedMods;
    public IReadOnlyList<ModDiagnostic> AllDiagnostics => _allDiagnostics;

    public void Clear()
    {
        _loadedMods.Clear();
        _allDiagnostics.Clear();
    }

    public bool TryLoadItemFromKeyValueFile(string filePath, byte dynamicItemId, out ItemDef? createdDef, Logger? log = null)
    {
        createdDef = null;
        var doc = ModFileParser.ParseFile(filePath);
        _allDiagnostics.AddRange(doc.Diagnostics);

        // Validation Rules
        if (!doc.TryGetString("id", out var itemIdStr, out var idLine))
        {
            doc.Diagnostics.Add(new ModDiagnostic(
                filePath,
                1,
                DiagnosticSeverity.Error,
                "LV_ITEM_MISSING_ID",
                "Item definition is missing required field 'id'.",
                "Add 'id=your_item_id' (e.g. 'id=wild_strawberry') to line 1."));
        }

        var name = doc.GetString("name", itemIdStr);
        if (string.IsNullOrWhiteSpace(name))
        {
            doc.Diagnostics.Add(new ModDiagnostic(
                filePath,
                1,
                DiagnosticSeverity.Warning,
                "LV_ITEM_MISSING_NAME",
                "Item is missing a display 'name'. Defaulting to ID.",
                "Add 'name=Your Item Name' (e.g. 'name=Wild Strawberry')."));
            name = itemIdStr;
        }

        var itemType = doc.GetString("type", string.Empty);
        var tags = doc.GetStringList("tags");
        if (!string.IsNullOrEmpty(itemType) && !tags.Contains(itemType, StringComparer.OrdinalIgnoreCase))
            tags.Add(itemType);

        // Ultra-simple aliases: hunger= / food= / hunger_restore=
        var hunger = doc.GetInt("hunger", doc.GetInt("food", doc.GetInt("hunger_restore", 0)));
        var health = doc.GetInt("health", doc.GetInt("health_restore", 0));
        var maxStack = doc.GetInt("stack", doc.GetInt("max_stack", 60));
        var durability = doc.GetInt("durability", -1);
        var texture = doc.GetString("texture", itemIdStr);

        // Tool / Weapon stats
        var toolTier = LootRegistry.HarvestToolTier.None;
        if (durability > 0)
        {
            // Infer tier from numerical durability if not explicitly specified
            toolTier = durability switch
            {
                >= 1500 => LootRegistry.HarvestToolTier.Diamond,
                >= 250 => LootRegistry.HarvestToolTier.Iron,
                >= 130 => LootRegistry.HarvestToolTier.Stone,
                _ => LootRegistry.HarvestToolTier.Wood
            };
        }

        if (doc.TryGetString("tool_tier", out var tierStr, out _))
        {
            toolTier = tierStr.ToLowerInvariant() switch
            {
                "wood" => LootRegistry.HarvestToolTier.Wood,
                "stone" => LootRegistry.HarvestToolTier.Stone,
                "iron" => LootRegistry.HarvestToolTier.Iron,
                "diamond" => LootRegistry.HarvestToolTier.Diamond,
                _ => toolTier
            };
        }

        // Model aliases: model= / hand_model= (tool, flat, block, custom)
        var handModel = ItemHandModelKind.FlatSprite;
        var modelStr = doc.GetString("model", doc.GetString("hand_model", string.Empty)).ToLowerInvariant();
        if (!string.IsNullOrEmpty(modelStr))
        {
            handModel = modelStr switch
            {
                "tool" => ItemHandModelKind.Tool,
                "block" => ItemHandModelKind.Block,
                "custom" or "3d" => ItemHandModelKind.CustomModel,
                _ => ItemHandModelKind.FlatSprite
            };
        }
        else if (tags.Contains("tool") || tags.Contains("weapon") || tags.Contains("mattock") || tags.Contains("hatchet") || tags.Contains("spade") || tags.Contains("blade") || tags.Contains("plow") || tags.Contains("javelin"))
        {
            handModel = ItemHandModelKind.Tool;
        }

        // Event hooks (functions / commands)
        var onUse = doc.GetString("on_use", string.Empty);
        var onHit = doc.GetString("on_hit", string.Empty);
        var onBreak = doc.GetString("on_break", string.Empty);
        var onConsume = doc.GetString("on_consume", string.Empty);

        if (doc.HasErrors)
        {
            foreach (var diag in doc.Diagnostics)
                log?.Error(diag.ToString());
            return false;
        }

        try
        {
            var def = new ItemDef(
                (ItemId)dynamicItemId,
                name,
                maxStack: maxStack,
                textureName: texture,
                inventoryModel: handModel == ItemHandModelKind.Block ? ItemInventoryModelKind.BlockIcon : ItemInventoryModelKind.FlatSprite,
                handModel: handModel,
                key: $"mod:{itemIdStr}",
                toolTier: toolTier,
                hungerRestore: hunger,
                healthRestore: health,
                customTags: tags,
                itemType: itemType);

            ItemRegistry.Register(def);
            createdDef = def;
            log?.Info($"[ModLoader] Registered '{def.Name}' (Durability: {durability}, Model: {handModel}, Tags: [{string.Join(", ", def.StringTags)}])");
            return true;
        }
        catch (Exception ex)
        {
            doc.Diagnostics.Add(new ModDiagnostic(
                filePath,
                0,
                DiagnosticSeverity.Error,
                "LV_REGISTRY_ERROR",
                $"Failed to register item: {ex.Message}",
                "Ensure ID is unique and properties are valid."));
            log?.Error(doc.Diagnostics[^1].ToString());
            return false;
        }
    }

    public void ScanAndLoadDirectory(string modsDirectory, Logger? log = null)
    {
        if (!Directory.Exists(modsDirectory))
            return;

        // 1. Scan unpacked mod directories
        foreach (var dir in Directory.GetDirectories(modsDirectory))
        {
            LoadModFolder(dir, log);
        }

        // 2. Scan .lvmod ZIP packages
        foreach (var file in Directory.GetFiles(modsDirectory, "*.lvmod"))
        {
            LoadModZip(file, log);
        }
    }

    private void LoadModFolder(string folderPath, Logger? log)
    {
        var manifestPath = Path.Combine(folderPath, "manifest.lvc");
        var modInfo = new ModPackageInfo
        {
            RootPath = folderPath,
            IsZip = false
        };

        if (File.Exists(manifestPath))
        {
            var manifestDoc = ModFileParser.ParseFile(manifestPath);
            modInfo.Name = manifestDoc.GetString("name", Path.GetFileName(folderPath));
            modInfo.Description = manifestDoc.GetString("description", string.Empty);
            modInfo.Author = manifestDoc.GetString("author", "Unknown");
            modInfo.Version = manifestDoc.GetString("version", "1.0.0");
            modInfo.GameVersion = manifestDoc.GetString("game_version", "1.0.0");
            modInfo.Id = manifestDoc.GetString("id", Path.GetFileName(folderPath).ToLowerInvariant());
            modInfo.Diagnostics.AddRange(manifestDoc.Diagnostics);
        }
        else
        {
            modInfo.Id = Path.GetFileName(folderPath).ToLowerInvariant();
            modInfo.Name = Path.GetFileName(folderPath);
            modInfo.Diagnostics.Add(new ModDiagnostic(
                manifestPath,
                0,
                DiagnosticSeverity.Warning,
                "LV_MISSING_MANIFEST",
                $"Mod folder '{Path.GetFileName(folderPath)}' is missing a 'manifest.lvc'.",
                "Create a 'manifest.lvc' with 'name=...', 'author=...', 'version=...' in the mod root."));
        }

        _loadedMods.Add(modInfo);
        _allDiagnostics.AddRange(modInfo.Diagnostics);

        // Load functions from functions/*.lvf or *.lvc
        var funcsDir = Path.Combine(folderPath, "functions");
        if (Directory.Exists(funcsDir))
        {
            foreach (var funcFile in Directory.GetFiles(funcsDir, "*.lvf"))
                ModFunctionRunner.ParseFunctionFile(funcFile, log);
            foreach (var funcFile in Directory.GetFiles(funcsDir, "*.lvc"))
                ModFunctionRunner.ParseFunctionFile(funcFile, log);
        }

        // Load items from content/items/*.lvc or *.lvi
        var itemsDir = Path.Combine(folderPath, "content", "items");
        if (Directory.Exists(itemsDir))
        {
            byte nextDynamicId = 140;
            foreach (var itemFile in Directory.GetFiles(itemsDir, "*.lvc"))
            {
                if (TryLoadItemFromKeyValueFile(itemFile, nextDynamicId, out _, log))
                    nextDynamicId++;
            }
            foreach (var itemFile in Directory.GetFiles(itemsDir, "*.lvi"))
            {
                if (TryLoadItemFromKeyValueFile(itemFile, nextDynamicId, out _, log))
                    nextDynamicId++;
            }
        }
    }

    private void LoadModZip(string zipPath, Logger? log)
    {
        var tempExtract = Path.Combine(Path.GetTempPath(), "LatticeVeil_Mods", Path.GetFileNameWithoutExtension(zipPath));
        try
        {
            if (Directory.Exists(tempExtract))
                Directory.Delete(tempExtract, recursive: true);

            ZipFile.ExtractToDirectory(zipPath, tempExtract);
            LoadModFolder(tempExtract, log);
        }
        catch (Exception ex)
        {
            _allDiagnostics.Add(new ModDiagnostic(
                zipPath,
                0,
                DiagnosticSeverity.Error,
                "LV_ZIP_EXTRACT_ERROR",
                $"Failed to extract mod archive: {ex.Message}",
                "Ensure the .lvmod file is a valid, uncorrupted ZIP archive."));
            log?.Error(_allDiagnostics[^1].ToString());
        }
    }
}
