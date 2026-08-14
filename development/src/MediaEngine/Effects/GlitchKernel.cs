namespace VideoEditor.MediaEngine.Effects;

/// <summary>
/// Digital glitch: horizontal band displacement plus RGB channel splitting.
/// Time-varying — it reads the auto-injected "__time" argument (seconds inside
/// the clip), so the glitch pattern jumps as the clip plays. Deterministic:
/// the same time and arguments always produce the same frame, which keeps
/// preview and export identical.
/// </summary>
public sealed class GlitchKernel : IVideoKernel
{
    private const int BandHeight = 8;

    public string Key => "glitch";

    public void Apply(byte[] bgra, int width, int height, IReadOnlyDictionary<string, double> args)
    {
        var amount = Math.Clamp(args.Get("amount", 0.5), 0, 1);
        if (amount <= 0) return;

        var speed = Math.Clamp(args.Get("speed", 12), 1, 60);
        var time = args.Get(VideoEffectPipeline.TimeArg, 0);
        var timeBucket = (int)(time * speed);

        var maxShift = Math.Max(2, (int)(width * 0.12 * amount));
        var channelShift = (int)(amount * 6);
        var stride = width * 4;
        var rowCopy = new byte[stride];

        for (var bandTop = 0; bandTop < height; bandTop += BandHeight)
        {
            var noise = Hash(bandTop * 7919 ^ timeBucket * 104729);

            // Only some bands glitch each tick; probability scales with amount.
            if (noise % 100 >= amount * 45) continue;

            var shift = (int)((noise >> 8) % (uint)(maxShift * 2)) - maxShift;
            var bandBottom = Math.Min(bandTop + BandHeight, height);

            for (var y = bandTop; y < bandBottom; y++)
            {
                var row = y * stride;
                Buffer.BlockCopy(bgra, row, rowCopy, 0, stride);

                for (var x = 0; x < width; x++)
                {
                    var to = row + x * 4;
                    var from = Wrap(x + shift, width) * 4;
                    var fromR = Wrap(x + shift + channelShift, width) * 4;
                    var fromB = Wrap(x + shift - channelShift, width) * 4;

                    bgra[to] = rowCopy[fromB];          // blue pulled one way
                    bgra[to + 1] = rowCopy[from + 1];   // green stays with the band
                    bgra[to + 2] = rowCopy[fromR + 2];  // red pulled the other way
                }
            }
        }
    }

    private static int Wrap(int x, int width)
    {
        var wrapped = x % width;
        return wrapped < 0 ? wrapped + width : wrapped;
    }

    /// <summary>Small deterministic integer hash (xorshift-style).</summary>
    private static uint Hash(int seed)
    {
        var value = (uint)seed * 2654435761u;
        value ^= value >> 15;
        value *= 2246822519u;
        value ^= value >> 13;
        return value;
    }
}
