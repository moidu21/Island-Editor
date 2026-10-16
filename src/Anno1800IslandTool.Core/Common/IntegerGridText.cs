using System.Globalization;
namespace Anno1800IslandTool.Core.Common;

/// <summary>Read fixed-size whitespace-delimited grids without allocating per token.</summary>
public static class IntegerGridText
{
    public static bool TryRead(string text, int expectedCount, out int[] values)
    {
        values = Array.Empty<int>();
        if (expectedCount < 0) return false;
        // Match counts first: malformed short input must not allocate a huge grid.
        int count = 0;
        var span = text.AsSpan();
        for (int i = 0; i < span.Length;)
        {
            while (i < span.Length && char.IsWhiteSpace(span[i])) i++;
            if (i == span.Length) break;
            if (++count > expectedCount) return false;
            while (i < span.Length && !char.IsWhiteSpace(span[i])) i++;
        }
        if (count != expectedCount) return false;
        var parsed = new int[count];
        int index = 0;
        for (int i = 0; i < span.Length;)
        {
            while (i < span.Length && char.IsWhiteSpace(span[i])) i++;
            if (i == span.Length) break;
            int start = i;
            while (i < span.Length && !char.IsWhiteSpace(span[i])) i++;
            if (!int.TryParse(span.Slice(start, i-start), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out parsed[index++])) return false;
        }
        values = parsed;
        return true;
    }
}
