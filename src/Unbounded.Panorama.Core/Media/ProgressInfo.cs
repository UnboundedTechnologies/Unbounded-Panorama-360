namespace Unbounded.Panorama.Media;

/// <summary>A progress update: what is happening and, when known, how far along (0..1).</summary>
public sealed record ProgressInfo(string Text, double? Fraction = null, string? Detail = null);
