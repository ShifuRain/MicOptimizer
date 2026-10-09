namespace MicOptimizer.Audio;

/// <summary>
/// A small mono FIFO between the capture thread (writes blocks) and the render thread (reads blocks) that keeps the
/// delay between them tiny and constant.
///
/// The two sides run on different hardware clocks, so over time one slowly gains on the other. A plain queue would
/// either run dry (clicks) or keep growing (more and more delay). This one watches how much audio is waiting each
/// time a block arrives, and when that drifts from <see cref="CushionFrames"/> it quietly drops or repeats a single
/// sample every <see cref="CorrectionInterval"/> output samples until it is back, which is inaudible for speech.
/// </summary>
public sealed class ElasticBuffer
{
    /// <summary>One correction sample is added or removed per this many samples while catching up (0.5%).</summary>
    public const int CorrectionInterval = 200;

    private readonly object _gate = new();
    private readonly float[] _ring;
    private int _readPos, _count;
    private bool _primed, _everPrimed;
    private int _debt;              // >0: too much audio waiting (drop samples); <0: too little (repeat samples)
    private int _sinceCorrection;
    private float _last;

    private int _cushion;

    public ElasticBuffer(int cushionFrames, int capacityFrames = 16384)
    {
        _cushion = Math.Max(1, cushionFrames);
        _ring = new float[capacityFrames];
    }

    /// <summary>How much audio should be waiting just before the next block arrives; this is the buffer's added delay.</summary>
    public int CushionFrames
    {
        get { lock (_gate) return _cushion; }
        set { lock (_gate) _cushion = Math.Max(1, value); }
    }

    public int Underruns { get; private set; }
    public int Overflows { get; private set; }
    /// <summary>Frames currently queued (for diagnostics).</summary>
    public int Count { get { lock (_gate) return _count; } }

    // Diagnostics, reset each time they are read.
    private long _writtenFrames, _readFrames;
    private int _minFillBeforeWrite = int.MaxValue, _maxFillBeforeWrite, _writes;

    /// <summary>
    /// Frames written and read since the last call, and the smallest/largest amount waiting just before a block arrived.
    /// Written/read should match over time; a persistent gap means the two clocks disagree.
    /// </summary>
    public (long Written, long Read, int MinFill, int MaxFill, int Writes) TakeStats()
    {
        lock (_gate)
        {
            var s = (_writtenFrames, _readFrames, _minFillBeforeWrite == int.MaxValue ? 0 : _minFillBeforeWrite, _maxFillBeforeWrite, _writes);
            _writtenFrames = 0; _readFrames = 0; _minFillBeforeWrite = int.MaxValue; _maxFillBeforeWrite = 0; _writes = 0;
            return s;
        }
    }

    public void Write(ReadOnlySpan<float> block)
    {
        lock (_gate)
        {
            _writtenFrames += block.Length; _writes++;
            _minFillBeforeWrite = Math.Min(_minFillBeforeWrite, _count);
            _maxFillBeforeWrite = Math.Max(_maxFillBeforeWrite, _count);
            // How much was still waiting when this block arrived is the real cushion; steer it toward the target.
            if (_primed)
            {
                int hysteresis = Math.Max(24, CushionFrames / 2);
                int excess = _count - CushionFrames;
                if (_debt == 0 && Math.Abs(excess) > hysteresis) _debt = excess;
            }

            if (block.Length > _ring.Length) block = block[^_ring.Length..];
            if (_count + block.Length > _ring.Length)
            {
                // Should not happen in practice; keep the newest audio and carry on.
                int drop = _count + block.Length - _ring.Length;
                _readPos = (_readPos + drop) % _ring.Length;
                _count -= drop;
                Overflows++;
            }

            int write = (_readPos + _count) % _ring.Length;
            int first = Math.Min(block.Length, _ring.Length - write);
            block[..first].CopyTo(_ring.AsSpan(write));
            block[first..].CopyTo(_ring);
            _count += block.Length;
        }
    }

    /// <summary>Fills <paramref name="output"/> completely; any shortfall is silence (and counted as an underrun).</summary>
    public void Read(Span<float> output)
    {
        lock (_gate)
        {
            _readFrames += output.Length;
            // At the very start, wait for the cushion to build up, otherwise the first block would underrun.
            // After a later underrun resume as soon as one block is available (a long gap is worse than a thin
            // cushion); the drift correction then stretches a few samples to rebuild the cushion unnoticed.
            if (!_primed)
            {
                int needed = _everPrimed ? output.Length : _cushion + output.Length;
                if (_count >= needed) { _primed = true; _everPrimed = true; }
                else { output.Clear(); return; }
            }

            for (int i = 0; i < output.Length; i++)
            {
                if (_count == 0)
                {
                    // Ran dry: fade out what we last played, then re-prime so we don't stutter.
                    Underruns++;
                    for (; i < output.Length; i++) { _last *= 0.98f; output[i] = _last; }
                    _primed = false;
                    _debt = 0;
                    return;
                }

                // Gradual correction: skip or repeat one sample every CorrectionInterval samples.
                if (_debt != 0 && ++_sinceCorrection >= CorrectionInterval)
                {
                    _sinceCorrection = 0;
                    if (_debt > 0 && _count > 1)
                    {
                        _readPos = (_readPos + 1) % _ring.Length;
                        _count--; _debt--;
                    }
                    else if (_debt < 0)
                    {
                        output[i] = _last;     // repeat the previous sample instead of consuming one
                        _debt++;
                        continue;
                    }
                }

                _last = _ring[_readPos];
                output[i] = _last;
                _readPos = (_readPos + 1) % _ring.Length;
                _count--;
            }
        }
    }
}
