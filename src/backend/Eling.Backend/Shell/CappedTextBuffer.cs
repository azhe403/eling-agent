using System.Text;

namespace Eling.Backend.Shell;

/// <summary>
/// Collects process output up to a byte budget, enforcing the budget while the
/// stream is read rather than after: a runaway command must not be able to fill
/// memory before the limit is applied.
/// </summary>
/// <remarks>
/// Both stream pumps of one process append concurrently, so every member takes
/// a private lock. The budget is counted in UTF-8 bytes — the unit the tool's
/// <c>maxOutputBytes</c> parameter is written in — so a run of multi-byte
/// characters cannot outgrow the cap it was given.
/// </remarks>
public sealed class CappedTextBuffer
{
    private readonly object _gate = new();
    private readonly StringBuilder _builder = new();
    private readonly int _cap;

    /// <summary>UTF-8 bytes of the buffered text, not characters.</summary>
    private int _bytes;

    /// <param name="cap">Maximum number of UTF-8 bytes to keep.</param>
    public CappedTextBuffer(int cap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1);
        _cap = cap;
    }

    /// <summary>Whether any appended text was dropped.</summary>
    public bool Truncated { get; private set; }

    /// <summary>
    /// Appends as much of <paramref name="text"/> as still fits the budget.
    /// Text beyond the budget is dropped and <see cref="Truncated"/> stays set
    /// for the life of the buffer. A chunk that fills the budget exactly is
    /// kept whole and does not mark the buffer: the next chunk does.
    /// </summary>
    public void Append(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (Truncated)
            {
                return;
            }

            var fitting = FittingChars(text, _cap - _bytes, out var bytes);
            if (fitting > 0)
            {
                _builder.Append(text, 0, fitting);
                _bytes += bytes;
            }

            if (fitting < text.Length)
            {
                Truncated = true;
            }
        }
    }

    /// <summary>The text collected so far.</summary>
    public override string ToString()
    {
        lock (_gate)
        {
            return _builder.ToString();
        }
    }

    /// <summary>
    /// Number of leading characters of <paramref name="text"/> whose UTF-8
    /// encoding fits in <paramref name="budget"/> bytes, and the bytes they
    /// take. A surrogate pair counts as one four-byte attempt, so a pair is
    /// never split in half.
    /// </summary>
    private static int FittingChars(string text, int budget, out int bytes)
    {
        var used = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            int width;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                width = 4;
            }
            else if (c < 0x80)
            {
                width = 1;
            }
            else if (c < 0x800)
            {
                width = 2;
            }
            else
            {
                width = 3;
            }

            if (used + width > budget)
            {
                bytes = used;
                return i;
            }

            used += width;
            if (width == 4)
            {
                // Skip the low surrogate: it is part of the character just counted.
                i++;
            }
        }

        bytes = used;
        return text.Length;
    }
}
