namespace Unbounded.Panorama.Layout;

public readonly record struct Box(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;

    public static Box Union(IEnumerable<Box> boxes)
    {
        var list = boxes.ToList();
        if (list.Count == 0)
            return default;
        var x = list.Min(b => b.X);
        var y = list.Min(b => b.Y);
        return new Box(x, y, list.Max(b => b.Right) - x, list.Max(b => b.Bottom) - y);
    }
}

/// <summary>
/// A guide to draw: a line at <see cref="Position"/> (x for vertical lines, y for horizontal ones)
/// running from <see cref="From"/> to <see cref="To"/>; spacing guides carry a distance label.
/// </summary>
public sealed record Guide(bool Vertical, double Position, double From, double To, string? Label = null, bool IsSpacing = false);

public sealed record SnapResult(double X, double Y, IReadOnlyList<Guide> Guides);

/// <summary>
/// Word/Figma-style smart guides: snap to the screen centre, edges and margins, to other
/// widgets' edges and centres, to equal spacing between neighbours, and to equal widths.
/// </summary>
public static class SnapEngine
{
    public const double Margin = 40;
    private const double Exact = 0.5;

    /// <summary>Snaps a moving box (the selection) against the canvas and the other boxes.</summary>
    public static SnapResult SnapMove(Box moving, IReadOnlyList<Box> others, double canvasW, double canvasH, double threshold)
    {
        var dx = BestAlignDelta(moving.X, moving.W, XTargets(others, canvasW), threshold);
        var dy = BestAlignDelta(moving.Y, moving.H, YTargets(others, canvasH), threshold);

        // Equal spacing competes with alignment; the closer one wins on each axis.
        var sx = BestSpacingDelta(moving, others, horizontal: true, threshold);
        var sy = BestSpacingDelta(moving, others, horizontal: false, threshold);
        if (sx is { } s1 && (dx is null || Math.Abs(s1) < Math.Abs(dx.Value)))
            dx = s1;
        if (sy is { } s2 && (dy is null || Math.Abs(s2) < Math.Abs(dy.Value)))
            dy = s2;

        var snapped = moving with { X = moving.X + (dx ?? 0), Y = moving.Y + (dy ?? 0) };
        return new SnapResult(snapped.X, snapped.Y, GuidesFor(snapped, others, canvasW, canvasH));
    }

    /// <summary>Snaps the right edge while resizing: alignment targets and equal widths.</summary>
    public static (double Width, IReadOnlyList<Guide> Guides) SnapWidth(Box moving, IReadOnlyList<Box> others, double canvasW, double canvasH, double threshold)
    {
        double? best = null;
        foreach (var t in XTargets(others, canvasW))
            Consider(ref best, t - moving.Right, threshold);
        foreach (var o in others)
            Consider(ref best, o.W - moving.W, threshold);

        var width = Math.Max(1, moving.W + (best ?? 0));
        var snapped = moving with { W = width };
        var guides = GuidesFor(snapped, others, canvasW, canvasH).Where(g => g.Vertical && Math.Abs(g.Position - snapped.Right) < Exact).ToList();
        foreach (var o in others.Where(o => Math.Abs(o.W - width) < Exact))
        {
            guides.Add(new Guide(false, o.Bottom + 14, o.X, o.Right, $"{width:0}", IsSpacing: true));
            guides.Add(new Guide(false, snapped.Bottom + 14, snapped.X, snapped.Right, $"{width:0}", IsSpacing: true));
        }
        return (width, guides);
    }

    private static IEnumerable<double> XTargets(IReadOnlyList<Box> others, double canvasW)
    {
        yield return 0;
        yield return Margin;
        yield return canvasW / 2;
        yield return canvasW - Margin;
        yield return canvasW;
        foreach (var o in others)
        {
            yield return o.X;
            yield return o.CenterX;
            yield return o.Right;
        }
    }

    private static IEnumerable<double> YTargets(IReadOnlyList<Box> others, double canvasH)
    {
        yield return 0;
        yield return Margin;
        yield return canvasH / 2;
        yield return canvasH - Margin;
        yield return canvasH;
        foreach (var o in others)
        {
            yield return o.Y;
            yield return o.CenterY;
            yield return o.Bottom;
        }
    }

    /// <summary>Smallest move that puts the start, centre or end of [start, start+size] on a target.</summary>
    private static double? BestAlignDelta(double start, double size, IEnumerable<double> targets, double threshold)
    {
        double? best = null;
        foreach (var t in targets)
        {
            Consider(ref best, t - start, threshold);
            Consider(ref best, t - (start + size / 2), threshold);
            Consider(ref best, t - (start + size), threshold);
        }
        return best;
    }

    private static void Consider(ref double? best, double delta, double threshold)
    {
        if (Math.Abs(delta) <= threshold && (best is null || Math.Abs(delta) < Math.Abs(best.Value)))
            best = delta;
    }

    // ------------------------------------------------------------------ spacing

    /// <summary>
    /// Candidate positions that repeat a gap already present between two neighbours on the same
    /// row/column, or that centre the box between two neighbours.
    /// </summary>
    private static double? BestSpacingDelta(Box moving, IReadOnlyList<Box> others, bool horizontal, double threshold)
    {
        var m = horizontal ? moving : Transpose(moving);
        var list = others.Select(o => horizontal ? o : Transpose(o)).ToList();
        var lane = list.Where(o => OverlapsAcross(o, m)).ToList();
        var gaps = ExistingGaps(list);

        double? best = null;
        foreach (var a in lane.Where(a => a.Right <= m.X + threshold + 1))
        {
            foreach (var g in gaps)
                Consider(ref best, a.Right + g - m.X, threshold);
            foreach (var b in lane.Where(b => b.X >= a.Right && b.X - a.Right >= m.W))
                Consider(ref best, (a.Right + b.X - m.W) / 2 - m.X, threshold);
        }
        foreach (var b in lane.Where(b => b.X >= m.Right - threshold - 1))
        {
            foreach (var g in gaps)
                Consider(ref best, b.X - g - m.W - m.X, threshold);
        }
        return best;
    }

    /// <summary>Gaps between boxes that sit next to each other on the main axis and overlap across it.</summary>
    private static List<double> ExistingGaps(List<Box> boxes)
    {
        var gaps = new List<double>();
        foreach (var a in boxes)
        {
            var next = boxes.Where(b => b.X >= a.Right && OverlapsAcross(a, b)).OrderBy(b => b.X).FirstOrDefault();
            if (next != default && next.X - a.Right > 0)
                gaps.Add(next.X - a.Right);
        }
        return gaps;
    }

    private static bool OverlapsAcross(Box a, Box b) => a.Y < b.Bottom && b.Y < a.Bottom;

    private static Box Transpose(Box b) => new(b.Y, b.X, b.H, b.W);

    // ------------------------------------------------------------------ guides

    private static List<Guide> GuidesFor(Box m, IReadOnlyList<Box> others, double canvasW, double canvasH)
    {
        var guides = new List<Guide>();

        // Screen centre / edges / margins.
        foreach (var x in new[] { canvasW / 2, Margin, canvasW - Margin })
            if (Touches(x, m.X, m.CenterX, m.Right))
                guides.Add(new Guide(true, x, 0, canvasH, x == canvasW / 2 ? "centre" : null));
        foreach (var y in new[] { canvasH / 2, Margin, canvasH - Margin })
            if (Touches(y, m.Y, m.CenterY, m.Bottom))
                guides.Add(new Guide(false, y, 0, canvasW, y == canvasH / 2 ? "centre" : null));

        // Other widgets' edges and centres.
        foreach (var o in others)
        {
            foreach (var x in new[] { o.X, o.CenterX, o.Right })
                if (Touches(x, m.X, m.CenterX, m.Right))
                    guides.Add(new Guide(true, x, Math.Min(o.Y, m.Y), Math.Max(o.Bottom, m.Bottom)));
            foreach (var y in new[] { o.Y, o.CenterY, o.Bottom })
                if (Touches(y, m.Y, m.CenterY, m.Bottom))
                    guides.Add(new Guide(false, y, Math.Min(o.X, m.X), Math.Max(o.Right, m.Right)));
        }

        guides.AddRange(SpacingGuides(m, others, horizontal: false));
        guides.AddRange(SpacingGuides(m, others, horizontal: true));
        return guides;
    }

    /// <summary>When the gap to a neighbour equals another gap in the layout, mark both.</summary>
    private static IEnumerable<Guide> SpacingGuides(Box moving, IReadOnlyList<Box> others, bool horizontal)
    {
        var m = horizontal ? moving : Transpose(moving);
        var list = others.Select(o => horizontal ? o : Transpose(o)).ToList();
        var lane = list.Where(o => OverlapsAcross(o, m)).ToList();
        var before = lane.Where(a => a.Right <= m.X + Exact).OrderByDescending(a => a.Right).FirstOrDefault();
        var after = lane.Where(b => b.X >= m.Right - Exact).OrderBy(b => b.X).FirstOrDefault();

        var pairs = new List<(Box A, Box B)>();
        foreach (var a in list)
        {
            var next = list.Where(b => b.X >= a.Right && OverlapsAcross(a, b)).OrderBy(b => b.X).FirstOrDefault();
            if (next != default)
                pairs.Add((a, next));
        }

        var result = new List<Guide>();
        void Mark(Box a, Box b)
        {
            var gap = b.X - a.Right;
            var across = (Math.Max(a.Y, b.Y) + Math.Min(a.Bottom, b.Bottom)) / 2;
            // In transposed space a horizontal gap becomes vertical; map back to screen orientation.
            result.Add(horizontal
                ? new Guide(false, across, a.Right, b.X, $"{gap:0}", IsSpacing: true)
                : new Guide(true, across, a.Right, b.X, $"{gap:0}", IsSpacing: true));
        }

        foreach (var neighbour in new[] { (before, true), (after, false) })
        {
            if (neighbour.Item1 == default)
                continue;
            var (a, b) = neighbour.Item2 ? (neighbour.Item1, m) : (m, neighbour.Item1);
            var gap = b.X - a.Right;
            if (gap <= 0)
                continue;
            var twins = pairs.Where(p => Math.Abs(p.B.X - p.A.Right - gap) < Exact).ToList();
            var other = neighbour.Item2 ? after : before;
            var symmetric = other != default && Math.Abs((neighbour.Item2 ? other.X - m.Right : m.X - other.Right) - gap) < Exact;
            if (twins.Count == 0 && !symmetric)
                continue;
            Mark(a, b);
            foreach (var (pa, pb) in twins)
                Mark(pa, pb);
        }
        return result.DistinctBy(g => (g.Vertical, Math.Round(g.Position), Math.Round(g.From), Math.Round(g.To)));
    }

    private static bool Touches(double line, params double[] edges) => edges.Any(e => Math.Abs(e - line) < Exact);
}
