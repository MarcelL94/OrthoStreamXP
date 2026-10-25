using System.Collections.Concurrent;

namespace OrthoStreamXP.Textures;

/// <summary>
/// Walks the whole colour blocks in a piece of a square DDS (from <c>fileOffset</c>, <c>length</c> bytes), level by
/// level, with the place each block is in: the texture split into <c>steps</c> x <c>steps</c> places, the same places
/// at every level. Blocks cut by either end of the piece are skipped, so a block's two halves always match whichever
/// reads they come in.
/// </summary>
internal ref struct DdsBlocks
{
    private static readonly ConcurrentDictionary<(int Size, int BlockBytes), long[]> Levels = new();
    private static readonly ConcurrentDictionary<(int Across, int Steps), int[]> PlacesAcross = new();

    private readonly long _fileOffset, _end;
    private readonly int _blockBytes, _size, _steps;
    private readonly long[] _levels;
    private long _at;
    private int _level, _across, _bx, _by;
    private int[] _placeOf;
    private bool _started;

    public DdsBlocks(long fileOffset, int length, int blockBytes, int size, int steps)
    {
        _fileOffset = fileOffset;
        _blockBytes = blockBytes;
        _size = size;
        _steps = steps;
        _levels = Levels.GetOrAdd((size, blockBytes), key => LevelOffsets(key.Size, key.BlockBytes));
        long from = Math.Max(fileOffset, Dds.HeaderSize) - Dds.HeaderSize;
        _at = Dds.HeaderSize + (from + blockBytes - 1) / blockBytes * blockBytes;
        _end = Math.Min(fileOffset + length, _levels[^1]);
        _placeOf = [];
    }

    /// <summary>Where the block's colour part starts in the piece (the second half of a 16-byte block).</summary>
    public readonly int At => (int)(_at - _fileOffset) + (_blockBytes == 16 ? 8 : 0);

    /// <summary>The block's place across and down (0 to steps - 1).</summary>
    public readonly int PlaceX => _placeOf[_bx];

    public readonly int PlaceY => _placeOf[_by];

    /// <summary>Whether the block starts a new row of places (the places of the previous row are not met again soon).</summary>
    public bool NewRow { get; private set; }

    public bool MoveNext()
    {
        int previousY = _started ? PlaceY : -1;
        bool newLevel = false;
        if (_started)
        {
            _at += _blockBytes;
            if (++_bx == _across)
            {
                _bx = 0;
                _by++;
            }
        }
        if (_at + _blockBytes > _end) return false;
        if (!_started || _at >= _levels[_level + 1])
        {
            while (_at >= _levels[_level + 1]) _level++;
            _across = Math.Max(1, ((_size >> _level) + 3) / 4);
            long index = (_at - _levels[_level]) / _blockBytes;
            (_bx, _by) = ((int)(index % _across), (int)(index / _across));
            int across = _across, steps = _steps;
            _placeOf = PlacesAcross.GetOrAdd((across, steps), key => [.. Enumerable.Range(0, key.Across).Select(i => (int)((long)i * key.Steps / key.Across))]);
            newLevel = true;
        }
        _started = true;
        NewRow = newLevel || PlaceY != previousY;
        return true;
    }

    /// <summary>Where each level starts in a square DDS (and, last, where the levels end).</summary>
    private static long[] LevelOffsets(int size, int blockBytes)
    {
        var offsets = new List<long> { Dds.HeaderSize };
        for (int s = size; ; s = Math.Max(1, s >> 1))
        {
            long blocks = Math.Max(1, (s + 3) / 4);
            offsets.Add(offsets[^1] + blocks * blocks * blockBytes);
            if (s == 1) break;
        }
        return [.. offsets];
    }
}
