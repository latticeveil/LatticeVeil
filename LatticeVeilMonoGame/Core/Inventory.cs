using System;
using System.Collections.Generic;
using System.Linq;

namespace LatticeVeilMonoGame.Core;

public struct HotbarSlot
{
    public BlockId Id;
    public int Count;
}

public sealed class Inventory
{
    public const int HotbarSize = 9;
    public const int GridCols = 9;
    public const int GridRows = 3;
    public const int GridSize = GridCols * GridRows;
    private const int DefaultStackSize = 60;
    private static readonly string[] EmptySearchTerms = Array.Empty<string>();

    private readonly HotbarSlot[] _hotbar = new HotbarSlot[HotbarSize];
    private readonly HotbarSlot[] _grid = new HotbarSlot[GridSize];
    private readonly HotbarSlot[] _sandboxCatalogSlots = new HotbarSlot[GridSize];
    private readonly List<int> _sandboxCatalogFilteredIndices = new();
    private readonly HashSet<BlockId> _sandboxCatalogFavorites = new();
    private bool _sandboxCatalogBuilt;
    private string _sandboxCatalogSearchQuery = string.Empty;
    private bool _sandboxCatalogFavoritesOnly;
    private string[] _sandboxCatalogSearchTerms = EmptySearchTerms;
    private static BlockId[]? _sandboxCatalogEntriesCache;

    public GameMode Mode { get; private set; } = GameMode.Artificer;

    public int SelectedIndex { get; set; }

    public HotbarSlot[] Hotbar => _hotbar;
    public HotbarSlot[] Grid => _grid;

    // Compatibility snapshot only; active Artificer catalog rendering uses filtered list APIs.
    public HotbarSlot[] SandboxCatalogSlots => _sandboxCatalogSlots;

    public BlockId SelectedId => _hotbar[SelectedIndex].Id;

    public int SelectedCount => _hotbar[SelectedIndex].Count;

    public int SandboxCatalogPage => 1;
    public int SandboxCatalogPageCount => 1;
    public string SandboxCatalogSearchQuery => _sandboxCatalogSearchQuery;
    public bool SandboxCatalogFavoritesOnly => _sandboxCatalogFavoritesOnly;

    public void SetMode(GameMode mode)
    {
        Mode = mode;
        if (Mode == GameMode.Artificer)
            EnsureSandboxCatalog();
    }

    public bool TryAdvanceSandboxCatalogPage(int delta)
    {
        _ = delta;
        return false;
    }

    public void SetSandboxCatalogSearchQuery(string query)
    {
        var normalized = NormalizeSearchQuery(query);
        if (string.Equals(_sandboxCatalogSearchQuery, normalized, StringComparison.Ordinal))
            return;

        _sandboxCatalogSearchQuery = normalized;
        _sandboxCatalogSearchTerms = BuildSearchTerms(normalized);
        RebuildSandboxCatalogFilteredEntries();
    }

    public void ClearSandboxCatalogSearchQuery()
    {
        SetSandboxCatalogSearchQuery(string.Empty);
    }

    public void SetSandboxCatalogFavoritesOnly(bool enabled)
    {
        if (_sandboxCatalogFavoritesOnly == enabled)
            return;

        _sandboxCatalogFavoritesOnly = enabled;
        RebuildSandboxCatalogFilteredEntries();
    }

    public bool ToggleSandboxCatalogFavorite(int blockId)
    {
        if (!TryConvertCatalogBlockId(blockId, out var parsed))
            return false;

        var changed = _sandboxCatalogFavorites.Remove(parsed);
        if (!changed)
            changed = _sandboxCatalogFavorites.Add(parsed);

        if (changed)
            RebuildSandboxCatalogFilteredEntries();

        return changed;
    }

    public bool IsSandboxCatalogFavorite(int blockId)
    {
        if (!TryConvertCatalogBlockId(blockId, out var parsed))
            return false;

        return _sandboxCatalogFavorites.Contains(parsed);
    }

    public void SetSandboxCatalogFavorites(IEnumerable<int>? favoriteBlockIds)
    {
        _sandboxCatalogFavorites.Clear();
        if (favoriteBlockIds != null)
        {
            foreach (var id in favoriteBlockIds)
            {
                if (!TryConvertCatalogBlockId(id, out var parsed))
                    continue;

                _sandboxCatalogFavorites.Add(parsed);
            }
        }

        RebuildSandboxCatalogFilteredEntries();
    }

    public int[] GetSandboxCatalogFavoriteBlockIds()
    {
        EnsureSandboxCatalog();
        if (_sandboxCatalogFavorites.Count == 0)
            return Array.Empty<int>();

        var all = GetSandboxCatalogEntries();
        var output = new List<int>(_sandboxCatalogFavorites.Count);
        for (var i = 0; i < all.Length; i++)
        {
            if (_sandboxCatalogFavorites.Contains(all[i]))
                output.Add((int)all[i]);
        }

        return output.ToArray();
    }

    public int GetSandboxCatalogFilteredCount()
    {
        EnsureSandboxCatalog();
        return _sandboxCatalogFilteredIndices.Count;
    }

    public int GetSandboxCatalogTotalCount()
    {
        return GetSandboxCatalogEntries().Length;
    }

    public HotbarSlot GetSandboxCatalogFilteredEntryAt(int filteredIndex)
    {
        EnsureSandboxCatalog();
        if (filteredIndex < 0 || filteredIndex >= _sandboxCatalogFilteredIndices.Count)
            return default;

        var all = GetSandboxCatalogEntries();
        var sourceIndex = _sandboxCatalogFilteredIndices[filteredIndex];
        if (sourceIndex < 0 || sourceIndex >= all.Length)
            return default;

        return new HotbarSlot { Id = all[sourceIndex], Count = 1 };
    }

    public void Select(int index)
    {
        SelectedIndex = Math.Clamp(index, 0, HotbarSize - 1);
    }

    public void Scroll(int delta)
    {
        if (delta == 0)
            return;
        var next = (SelectedIndex + delta) % HotbarSize;
        if (next < 0)
            next += HotbarSize;
        SelectedIndex = next;
    }

    public void PickBlock(BlockId id)
    {
        if (id == BlockId.Air)
            return;

        // 1. Is it already in the hotbar?
        for (int i = 0; i < HotbarSize; i++)
        {
            if (_hotbar[i].Id == id && _hotbar[i].Count > 0)
            {
                SelectedIndex = i;
                return;
            }
        }

        // 2. Is it in the grid?
        for (int i = 0; i < GridSize; i++)
        {
            if (_grid[i].Id == id && _grid[i].Count > 0)
            {
                // Swap with current hotbar slot
                var temp = _hotbar[SelectedIndex];
                _hotbar[SelectedIndex] = _grid[i];
                _grid[i] = temp;
                return;
            }
        }

        // 3. Sandbox mode: just set it in current slot if not found
        if (Mode == GameMode.Artificer)
        {
            _hotbar[SelectedIndex].Id = id;
            _hotbar[SelectedIndex].Count = 1;
        }
    }

    public bool CanAdd(BlockId id, int amount)
    {
        if (id == BlockId.Air || amount <= 0)
            return false;

        var max = GetMaxStack(id, Mode);
        if (max <= 0)
            return false;

        var space = GetAvailableSpace(_hotbar, id, max) + GetAvailableSpace(_grid, id, max);
        return space >= amount;
    }

    public int Add(BlockId id, int amount)
    {
        if (id == BlockId.Air || amount <= 0)
            return amount;

        var max = GetMaxStack(id, Mode);
        if (max <= 0)
            return amount;

        amount = FillExistingStacks(_hotbar, id, amount, max);
        amount = FillExistingStacks(_grid, id, amount, max);
        amount = FillEmptyStacks(_hotbar, id, amount, max, selectIfEmpty: true);
        amount = FillEmptyStacks(_grid, id, amount, max, selectIfEmpty: false);
        return amount;
    }

    public bool Contains(BlockId id)
    {
        if (id == BlockId.Air)
            return false;

        for (var i = 0; i < _hotbar.Length; i++)
        {
            if (_hotbar[i].Id == id && _hotbar[i].Count > 0)
                return true;
        }

        for (var i = 0; i < _grid.Length; i++)
        {
            if (_grid[i].Id == id && _grid[i].Count > 0)
                return true;
        }

        return false;
    }

    public int Count(BlockId id)
    {
        if (id == BlockId.Air)
            return 0;

        var total = 0;
        for (var i = 0; i < _hotbar.Length; i++)
        {
            if (_hotbar[i].Id == id && _hotbar[i].Count > 0)
                total += _hotbar[i].Count;
        }

        for (var i = 0; i < _grid.Length; i++)
        {
            if (_grid[i].Id == id && _grid[i].Count > 0)
                total += _grid[i].Count;
        }

        return total;
    }

    public int CountMatching(Func<BlockId, bool> matches)
    {
        var total = 0;
        for (var i = 0; i < _hotbar.Length; i++)
        {
            if (_hotbar[i].Count > 0 && _hotbar[i].Id != BlockId.Air && matches(_hotbar[i].Id))
                total += _hotbar[i].Count;
        }

        for (var i = 0; i < _grid.Length; i++)
        {
            if (_grid[i].Count > 0 && _grid[i].Id != BlockId.Air && matches(_grid[i].Id))
                total += _grid[i].Count;
        }

        return total;
    }

    public int GetMaxStackSize(BlockId id) => GetMaxStack(id, Mode);

    public bool TryConsume(BlockId id, int amount)
    {
        if (amount <= 0)
            return true;
        if (id == BlockId.Air || Count(id) < amount)
            return false;

        amount = ConsumeFromSlots(_hotbar, id, amount);
        amount = ConsumeFromSlots(_grid, id, amount);
        return amount <= 0;
    }

    public bool TryConsumeMatching(Func<BlockId, bool> matches, int amount)
    {
        if (amount <= 0)
            return true;
        if (CountMatching(matches) < amount)
            return false;

        amount = ConsumeMatchingFromSlots(_hotbar, matches, amount);
        amount = ConsumeMatchingFromSlots(_grid, matches, amount);
        return amount <= 0;
    }

    public bool TryConsumeSelected(int amount)
    {
        if (amount <= 0)
            return true;
        if (Mode == GameMode.Artificer)
            return true;

        ref var slot = ref _hotbar[SelectedIndex];
        if (slot.Count < amount || slot.Id == BlockId.Air)
            return false;

        slot.Count -= amount;
        if (slot.Count <= 0)
        {
            slot.Count = 0;
            slot.Id = BlockId.Air;
        }
        return true;
    }

    private static int ConsumeFromSlots(HotbarSlot[] slots, BlockId id, int amount)
    {
        for (var i = 0; i < slots.Length && amount > 0; i++)
        {
            if (slots[i].Id != id || slots[i].Count <= 0)
                continue;

            var take = Math.Min(slots[i].Count, amount);
            slots[i].Count -= take;
            amount -= take;
            if (slots[i].Count <= 0)
            {
                slots[i].Count = 0;
                slots[i].Id = BlockId.Air;
            }
        }

        return amount;
    }

    private static int ConsumeMatchingFromSlots(HotbarSlot[] slots, Func<BlockId, bool> matches, int amount)
    {
        for (var i = 0; i < slots.Length && amount > 0; i++)
        {
            if (slots[i].Id == BlockId.Air || slots[i].Count <= 0 || !matches(slots[i].Id))
                continue;

            var take = Math.Min(slots[i].Count, amount);
            slots[i].Count -= take;
            amount -= take;
            if (slots[i].Count <= 0)
            {
                slots[i].Count = 0;
                slots[i].Id = BlockId.Air;
            }
        }

        return amount;
    }

    private static int GetMaxStack(BlockId id, GameMode mode)
    {
        var item = ItemRegistry.Get((byte)id);
        if (item.Id != ItemId.None)
            return item.MaxStack;

        if (mode == GameMode.Artificer)
            return ToolIds.Contains(id) ? 1 : DefaultStackSize;
        return ToolIds.Contains(id) ? 1 : DefaultStackSize;
    }

    private static int GetAvailableSpace(HotbarSlot[] slots, BlockId id, int max)
    {
        var space = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            if (slot.Count <= 0 || slot.Id == BlockId.Air)
                space += max;
            else if (slot.Id == id && slot.Count < max)
                space += max - slot.Count;
        }
        return space;
    }

    private static int FillExistingStacks(HotbarSlot[] slots, BlockId id, int amount, int max)
    {
        if (amount <= 0)
            return 0;

        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].Count <= 0 || slots[i].Id != id)
                continue;

            var add = Math.Min(max - slots[i].Count, amount);
            if (add <= 0)
                continue;

            slots[i].Count += add;
            amount -= add;
            if (amount <= 0)
                break;
        }
        return amount;
    }

    private int FillEmptyStacks(HotbarSlot[] slots, BlockId id, int amount, int max, bool selectIfEmpty)
    {
        if (amount <= 0)
            return 0;

        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].Count > 0)
                continue;

            var add = Math.Min(max, amount);
            slots[i].Id = id;
            slots[i].Count = add;
            if (selectIfEmpty && _hotbar[SelectedIndex].Count == 0)
                SelectedIndex = i;
            amount -= add;
            if (amount <= 0)
                break;
        }
        return amount;
    }

    // Tool IDs: stack size = 1. Locked names: Excavator, Shovel, Woodcutter (add when item IDs exist).
    private static readonly HashSet<BlockId> ToolIds = new()
    {
        BlockId.CinderbranchStaff,
        BlockId.StormreedStaff,
        BlockId.EmptyBucket,
        BlockId.WaterBucket
    };

    private void EnsureSandboxCatalog()
    {
        if (_sandboxCatalogBuilt)
            return;

        RebuildSandboxCatalogFilteredEntries();
        _sandboxCatalogBuilt = true;
    }

    private void RebuildSandboxCatalogFilteredEntries()
    {
        var all = GetSandboxCatalogEntries();
        _sandboxCatalogFilteredIndices.Clear();

        var favoriteMatches = new List<int>();
        var standardMatches = new List<int>();

        for (var sourceIndex = 0; sourceIndex < all.Length; sourceIndex++)
        {
            var id = all[sourceIndex];
            var isFavorite = _sandboxCatalogFavorites.Contains(id);
            if (_sandboxCatalogFavoritesOnly && !isFavorite)
                continue;

            if (!MatchesCatalogSearch(id, _sandboxCatalogSearchTerms))
                continue;

            if (isFavorite)
                favoriteMatches.Add(sourceIndex);
            else
                standardMatches.Add(sourceIndex);
        }

        _sandboxCatalogFilteredIndices.AddRange(favoriteMatches);
        _sandboxCatalogFilteredIndices.AddRange(standardMatches);
        RebuildSandboxCatalogSlotPreview(all);
    }

    private void RebuildSandboxCatalogSlotPreview(BlockId[] allEntries)
    {
        for (var i = 0; i < _sandboxCatalogSlots.Length; i++)
        {
            if (i >= _sandboxCatalogFilteredIndices.Count)
            {
                _sandboxCatalogSlots[i] = default;
                continue;
            }

            var sourceIndex = _sandboxCatalogFilteredIndices[i];
            if (sourceIndex < 0 || sourceIndex >= allEntries.Length)
            {
                _sandboxCatalogSlots[i] = default;
                continue;
            }

            _sandboxCatalogSlots[i].Id = allEntries[sourceIndex];
            _sandboxCatalogSlots[i].Count = 1;
        }
    }

    private static string NormalizeSearchQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        return string.Join(' ', query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string[] BuildSearchTerms(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return EmptySearchTerms;

        return query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(term => term.Trim().ToLowerInvariant())
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool MatchesCatalogSearch(BlockId id, string[] terms)
    {
        if (terms == null || terms.Length == 0)
            return true;

        var item = ItemRegistry.Get((byte)id);
        var name = item.Name.ToLowerInvariant();
        var key = item.Key.ToLowerInvariant();
        var token = item.Id.ToString().ToLowerInvariant();
        var texture = (item.TextureName ?? string.Empty).ToLowerInvariant();
        var placeToken = item.PlacesBlock.HasValue
            ? item.PlacesBlock.Value.ToString().ToLowerInvariant()
            : string.Empty;

        for (var i = 0; i < terms.Length; i++)
        {
            var term = terms[i];
            if (name.Contains(term, StringComparison.Ordinal))
                continue;
            if (key.Contains(term, StringComparison.Ordinal))
                continue;
            if (token.Contains(term, StringComparison.Ordinal))
                continue;
            if (texture.Contains(term, StringComparison.Ordinal))
                continue;
            if (placeToken.Contains(term, StringComparison.Ordinal))
                continue;
            return false;
        }

        return true;
    }

    private static bool TryConvertCatalogBlockId(int rawValue, out BlockId blockId)
    {
        blockId = BlockId.Air;
        if (rawValue <= 0 || rawValue > byte.MaxValue)
            return false;

        blockId = (BlockId)rawValue;
        var item = ItemRegistry.Get((byte)blockId);
        return item.Id != ItemId.None && item.IsVisibleInCatalog;
    }

    private static BlockId[] GetSandboxCatalogEntries()
    {
        if (_sandboxCatalogEntriesCache is { Length: > 0 })
            return _sandboxCatalogEntriesCache;

        _sandboxCatalogEntriesCache = ItemRegistry.All
            .Where(def => def.Id != ItemId.None && def.IsVisibleInCatalog)
            .OrderBy(def => (byte)def.Id)
            .Select(def => ItemRegistry.ToLegacyBlockId(def.Id))
            .ToArray();

        return _sandboxCatalogEntriesCache;
    }

    /// <summary>
    /// Gets a copy of the hotbar data for saving
    /// </summary>
    public HotbarSlot[] GetHotbarData()
    {
        var data = new HotbarSlot[HotbarSize];
        Array.Copy(_hotbar, data, HotbarSize);
        return data;
    }

    /// <summary>
    /// Sets the hotbar data from loaded save data
    /// </summary>
    public void SetHotbarData(HotbarSlot[] data)
    {
        if (data == null || data.Length < HotbarSize)
            return;

        Array.Copy(data, _hotbar, HotbarSize);

        // Clamp selected index to valid range
        if (SelectedIndex >= HotbarSize)
            SelectedIndex = HotbarSize - 1;
    }

    /// <summary>
    /// Gets a copy of the full inventory grid (excluding hotbar) for persistence.
    /// </summary>
    public HotbarSlot[] GetGridData()
    {
        var data = new HotbarSlot[GridSize];
        Array.Copy(_grid, data, GridSize);
        return data;
    }

    /// <summary>
    /// Restores the full inventory grid (excluding hotbar) from persisted data.
    /// </summary>
    public void SetGridData(HotbarSlot[] data)
    {
        if (data == null || data.Length == 0)
            return;

        Array.Clear(_grid, 0, _grid.Length);
        var copy = Math.Min(_grid.Length, data.Length);
        Array.Copy(data, _grid, copy);
    }

    public int ClearAll(bool clearGrid = true)
    {
        var removed = 0;
        for (int i = 0; i < _hotbar.Length; i++)
        {
            removed += Math.Max(0, _hotbar[i].Count);
            _hotbar[i].Id = BlockId.Air;
            _hotbar[i].Count = 0;
        }

        if (clearGrid)
        {
            for (int i = 0; i < _grid.Length; i++)
            {
                removed += Math.Max(0, _grid[i].Count);
                _grid[i].Id = BlockId.Air;
                _grid[i].Count = 0;
            }
        }

        SelectedIndex = Math.Clamp(SelectedIndex, 0, HotbarSize - 1);
        return removed;
    }
}
