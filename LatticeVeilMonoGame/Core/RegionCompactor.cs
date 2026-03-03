using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LatticeVeilMonoGame.Core;

public static class RegionCompactor
{
    private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes("LVREGION");

    public static long CompactIfNeeded(string worldPath, WorldStorageBudgetThresholds thresholds, Logger log)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(worldPath) || !Directory.Exists(worldPath))
                return 0L;

            var budget = WorldStorageBudgetService.GetBudgetState(worldPath, thresholds);
            if (!budget.IsConserve && !budget.IsOverTarget)
                return 0L;

            var regionsDir = Path.Combine(worldPath, FileConventions.RegionsDirName);
            if (!Directory.Exists(regionsDir))
                return 0L;

            var reclaimedBytes = 0L;
            var files = Directory.GetFiles(regionsDir, $"*{FileConventions.RegionExtension}", SearchOption.TopDirectoryOnly);
            for (var i = 0; i < files.Length; i++)
            {
                reclaimedBytes += CompactRegionFile(files[i], log);
            }

            if (reclaimedBytes > 0)
            {
                var post = WorldStorageBudgetService.GetBudgetState(worldPath, thresholds);
                log.Info($"Region compaction reclaimed {WorldStorageBudgetService.FormatBytes(reclaimedBytes)}; world now {WorldStorageBudgetService.FormatBytes(post.SizeBytes)} ({post.State}).");
            }

            return reclaimedBytes;
        }
        catch (Exception ex)
        {
            log.Warn($"Region compaction failed: {ex.Message}");
            return 0L;
        }
    }

    private static long CompactRegionFile(string path, Logger log)
    {
        try
        {
            var originalInfo = new FileInfo(path);
            if (!originalInfo.Exists || originalInfo.Length <= 0)
                return 0L;

            if (!TryReadRegion(path, out var region))
                return 0L;

            if (region.LiveEntries.Count == 0)
                return 0L;

            var tmpPath = path + ".compact.tmp";
            WriteCompactedRegion(tmpPath, region);
            var compactedInfo = new FileInfo(tmpPath);
            if (!compactedInfo.Exists)
                return 0L;

            // Only replace when compaction meaningfully reduces size.
            if (compactedInfo.Length >= originalInfo.Length)
            {
                File.Delete(tmpPath);
                return 0L;
            }

            File.Replace(tmpPath, path, null, ignoreMetadataErrors: true);
            return Math.Max(0L, originalInfo.Length - compactedInfo.Length);
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to compact region {Path.GetFileName(path)}: {ex.Message}");
            return 0L;
        }
    }

    private static bool TryReadRegion(string path, out RegionDocument region)
    {
        region = default;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        var magic = br.ReadBytes(8);
        if (magic.Length != 8 || !MatchesMagic(magic))
            return false;

        var version = br.ReadInt32();
        var regionX = br.ReadInt32();
        var regionZ = br.ReadInt32();
        var regionSize = br.ReadInt32();
        var indexOffset = br.ReadInt64();
        if (regionSize <= 0 || indexOffset < 0 || indexOffset >= fs.Length)
            return false;

        var entryCount = regionSize * regionSize;
        fs.Seek(indexOffset, SeekOrigin.Begin);
        var entries = new List<LiveEntry>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var offset = br.ReadInt32();
            var length = br.ReadInt32();
            var flags = br.ReadInt32();
            var crc = br.ReadUInt32();
            if (offset <= 0 || length <= 0)
                continue;
            if ((long)offset + length > fs.Length)
                continue;

            var returnPos = fs.Position;
            fs.Seek(offset, SeekOrigin.Begin);
            var payload = br.ReadBytes(length);
            fs.Seek(returnPos, SeekOrigin.Begin);
            if (payload.Length != length)
                continue;

            entries.Add(new LiveEntry(i, flags, crc, payload));
        }

        region = new RegionDocument(version, regionX, regionZ, regionSize, entries);
        return true;
    }

    private static void WriteCompactedRegion(string path, RegionDocument region)
    {
        var headerSize = 8 + 4 + 4 + 4 + 4 + 8;
        var indexEntrySize = 4 + 4 + 4 + 4;
        var entryCount = region.RegionSize * region.RegionSize;
        var indexTableSize = entryCount * indexEntrySize;
        var indexOffset = headerSize;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

        bw.Write(MagicBytes);
        bw.Write(region.Version);
        bw.Write(region.RegionX);
        bw.Write(region.RegionZ);
        bw.Write(region.RegionSize);
        bw.Write((long)indexOffset);

        for (var i = 0; i < entryCount; i++)
        {
            bw.Write(0); // offset
            bw.Write(0); // length
            bw.Write(0); // flags
            bw.Write(0u); // crc
        }

        var index = new Dictionary<int, (int offset, int length, int flags, uint crc)>();
        fs.Seek(indexOffset + indexTableSize, SeekOrigin.Begin);
        for (var i = 0; i < region.LiveEntries.Count; i++)
        {
            var entry = region.LiveEntries[i];
            var payloadOffset = checked((int)fs.Position);
            bw.Write(entry.Payload);
            index[entry.EntryIndex] = (payloadOffset, entry.Payload.Length, entry.Flags, entry.Crc32);
        }

        for (var i = 0; i < entryCount; i++)
        {
            fs.Seek(indexOffset + (i * indexEntrySize), SeekOrigin.Begin);
            if (index.TryGetValue(i, out var value))
            {
                bw.Write(value.offset);
                bw.Write(value.length);
                bw.Write(value.flags);
                bw.Write(value.crc);
            }
            else
            {
                bw.Write(0);
                bw.Write(0);
                bw.Write(0);
                bw.Write(0u);
            }
        }

        bw.Flush();
        fs.Flush(flushToDisk: true);
    }

    private static bool MatchesMagic(byte[] magic)
    {
        if (magic.Length != MagicBytes.Length)
            return false;
        for (var i = 0; i < MagicBytes.Length; i++)
        {
            if (magic[i] != MagicBytes[i])
                return false;
        }

        return true;
    }

    private readonly struct RegionDocument
    {
        public RegionDocument(int version, int regionX, int regionZ, int regionSize, List<LiveEntry> liveEntries)
        {
            Version = version;
            RegionX = regionX;
            RegionZ = regionZ;
            RegionSize = regionSize;
            LiveEntries = liveEntries;
        }

        public int Version { get; }
        public int RegionX { get; }
        public int RegionZ { get; }
        public int RegionSize { get; }
        public List<LiveEntry> LiveEntries { get; }
    }

    private readonly struct LiveEntry
    {
        public LiveEntry(int entryIndex, int flags, uint crc32, byte[] payload)
        {
            EntryIndex = entryIndex;
            Flags = flags;
            Crc32 = crc32;
            Payload = payload;
        }

        public int EntryIndex { get; }
        public int Flags { get; }
        public uint Crc32 { get; }
        public byte[] Payload { get; }
    }
}
