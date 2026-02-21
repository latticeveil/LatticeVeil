using System;
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
    private readonly HotbarSlot[] _hotbar = new HotbarSlot[HotbarSize];
    private readonly HotbarSlot[] _grid = new HotbarSlot[GridSize];
    private readonly HotbarSlot[] _sandboxCatalogSlots = new HotbarSlot[GridSize];
    private bool _sandboxCatalogBuilt;
    private int _sandboxCatalogPage;
    private static BlockId[]? _sandboxCatalogEntriesCache;

    public GameMode Mode { get; private set; } = GameMode.Artificer;

    public int SelectedIndex { get; set; }

    public HotbarSlot[] Hotbar => _hotbar;
    public HotbarSlot[] Grid => _grid;
    public HotbarSlot[] SandboxCatalogSlots => _sandboxCatalogSlots;

    public BlockId SelectedId => _hotbar[SelectedIndex].Id;

    public int SelectedCount => _hotbar[SelectedIndex].Count;

    public int SandboxCatalogPage => _sandboxCatalogPage + 1;

    public int SandboxCatalogPageCount
    {
        get
        {
            var total = GetSandboxCatalogEntries().Length;
            return Math.Max(1, (int)Math.Ceiling(total / (double)GridSize));
        }
    }

    public void SetMode(GameMode mode)
    {
        Mode = mode;
        if (Mode == GameMode.Artificer)
            EnsureSandboxCatalog();
    }

    public bool TryAdvanceSandboxCatalogPage(int delta)
    {
        if (Mode != GameMode.Artificer)
            return false;

        var pageCount = SandboxCatalogPageCount;
        if (pageCount <= 1)
            return false;

        var next = (_sandboxCatalogPage + delta) % pageCount;
        if (next < 0)
            next += pageCount;

        if (next == _sandboxCatalogPage && _sandboxCatalogBuilt)
            return false;

        _sandboxCatalogPage = next;
        RebuildSandboxCatalog();
        return true;
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

    private static int GetMaxStack(BlockId id, GameMode mode)
    {
        if (mode == GameMode.Artificer)
            return 1;
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
    private static readonly System.Collections.Generic.HashSet<BlockId> ToolIds = new()
    {
        BlockId.CinderbranchStaff,
        BlockId.StormreedStaff
    };

    private void EnsureSandboxCatalog()
    {
        if (_sandboxCatalogBuilt)
            return;

        RebuildSandboxCatalog();
        _sandboxCatalogBuilt = true;
    }

    private void RebuildSandboxCatalog()
    {
        var all = GetSandboxCatalogEntries();
        var pageStart = _sandboxCatalogPage * _sandboxCatalogSlots.Length;
        for (var i = 0; i < _sandboxCatalogSlots.Length; i++)
        {
            var source = pageStart + i;
            if (source >= 0 && source < all.Length)
            {
                _sandboxCatalogSlots[i].Id = all[source];
                _sandboxCatalogSlots[i].Count = 1;
            }
            else
            {
                _sandboxCatalogSlots[i].Id = BlockId.Air;
                _sandboxCatalogSlots[i].Count = 0;
            }
        }
    }

    private static BlockId[] GetSandboxCatalogEntries()
    {
        if (_sandboxCatalogEntriesCache is { Length: > 0 })
            return _sandboxCatalogEntriesCache;

        _sandboxCatalogEntriesCache = BlockRegistry.All
            .Where(def => def.Id != BlockId.Air)
            .OrderBy(def => def.AtlasIndex)
            .Select(def => def.Id)
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
