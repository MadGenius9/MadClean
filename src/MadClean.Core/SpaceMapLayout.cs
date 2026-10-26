namespace MadClean.Core;

public sealed record SpaceMapRectangle(int Index, double X, double Y, double Width, double Height);

// Original weighted binary partition. Geometry has no filesystem or deletion semantics.
public static class SpaceMapLayout
{
    public static IReadOnlyList<SpaceMapRectangle> Create(IReadOnlyList<long> weights, double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 0 || height < 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (weights.Any(w => w < 0)) throw new ArgumentOutOfRangeException(nameof(weights));
        if (width == 0 || height == 0) return Array.Empty<SpaceMapRectangle>();
        var items = weights.Select((weight, index) => (Weight: (double)weight, Index: index)).Where(i => i.Weight > 0).ToArray();
        List<SpaceMapRectangle> rectangles = [];
        void Partition(int start, int count, double x, double y, double w, double h)
        {
            if (count == 1) { rectangles.Add(new(items[start].Index, x, y, w, h)); return; }
            double total = 0;
            for (int i = start; i < start + count; i++) total += items[i].Weight;
            double left = 0; int cut = 0;
            while (cut < count - 1 && (cut == 0 || left < total / 2)) left += items[start + cut++].Weight;
            double ratio = Math.Clamp(left / total, 0, 1);
            if (w >= h)
            {
                double first = w * ratio;
                Partition(start, cut, x, y, first, h);
                Partition(start + cut, count - cut, x + first, y, Math.Max(0, w - first), h);
            }
            else
            {
                double first = h * ratio;
                Partition(start, cut, x, y, w, first);
                Partition(start + cut, count - cut, x, y + first, w, Math.Max(0, h - first));
            }
        }
        if (items.Length > 0) Partition(0, items.Length, 0, 0, width, height);
        return rectangles;
    }
}
