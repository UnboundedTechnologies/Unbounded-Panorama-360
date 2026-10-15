namespace Unbounded.Panorama.Layout;

/// <summary>Starter designs. Sensors use portable roles, resolved on each PC.</summary>
public static class BuiltInPresets
{
    public static IReadOnlyList<(string Name, Func<ScreenLayout> Create)> All { get; } =
    [
        ("Clean clock", CleanClock),
        ("Stats tower", StatsTower),
        ("Minimal temps", MinimalTemps),
        ("Hardware badges", HardwareBadges),
    ];

    private static Widget Sensor(string role, string prefix, int y, int size, string color, FontWeightName weight = FontWeightName.Medium, int decimals = 0) => new()
    {
        Content = WidgetContent.Sensor, SensorId = "role:" + role, Prefix = prefix, Decimals = decimals,
        X = 90, Y = y, Width = 900, Size = size, Weight = weight, Align = TextAlign.Center, Color = color,
    };

    private static ScreenLayout CleanClock() => new()
    {
        Widgets =
        [
            new Widget { Content = WidgetContent.Clock, Format = "HH:mm", X = 40, Y = 300, Width = 1000, Size = 300, Weight = FontWeightName.Light, Align = TextAlign.Center },
            new Widget { Content = WidgetContent.Date, Format = "dddd d MMMM", X = 40, Y = 700, Width = 1000, Size = 64, Align = TextAlign.Center, Color = "#BFC7D5" },
        ],
    };

    private static ScreenLayout StatsTower() => new()
    {
        Widgets =
        [
            new Widget { Content = WidgetContent.Clock, Format = "HH:mm", X = 90, Y = 80, Width = 900, Size = 180, Weight = FontWeightName.Light, Align = TextAlign.Center },
            Sensor("cpu-temp", "CPU  ", 380, 96, "#FF7A59"),
            Sensor("cpu-load", "LOAD  ", 540, 72, "#FFC247"),
            Sensor("gpu-temp", "GPU  ", 700, 96, "#76B900"),
            Sensor("gpu-load", "LOAD  ", 860, 72, "#3DDC97"),
            Sensor("ram-load", "RAM  ", 1020, 72, "#22D3EE"),
        ],
    };

    private static ScreenLayout MinimalTemps() => new()
    {
        Widgets =
        [
            Sensor("cpu-temp", "", 360, 260, "#FFFFFF", FontWeightName.Light),
            new Widget { Content = WidgetContent.Text, Text = "CPU", X = 90, Y = 700, Width = 900, Size = 56, Align = TextAlign.Center, Color = "#7C8799", Weight = FontWeightName.Medium },
            Sensor("gpu-temp", "", 1240, 260, "#FFFFFF", FontWeightName.Light),
            new Widget { Content = WidgetContent.Text, Text = "GPU", X = 90, Y = 1580, Width = 900, Size = 56, Align = TextAlign.Center, Color = "#7C8799", Weight = FontWeightName.Medium },
        ],
    };

    private static ScreenLayout HardwareBadges() => new()
    {
        Widgets =
        [
            new Widget { Content = WidgetContent.CpuName, X = 90, Y = 120, Width = 900, Size = 48, Align = TextAlign.Center, Badge = true, BadgeFrom = "#A92F2C", BadgeTo = "#CB6236", Weight = FontWeightName.Medium },
            Sensor("cpu-temp", "", 240, 200, "#FFFFFF", FontWeightName.Light),
            new Widget { Content = WidgetContent.GpuName, X = 90, Y = 620, Width = 900, Size = 48, Align = TextAlign.Center, Badge = true, BadgeFrom = "#629A00", BadgeTo = "#79AB51", Weight = FontWeightName.Medium },
            Sensor("gpu-temp", "", 740, 200, "#FFFFFF", FontWeightName.Light),
        ],
    };
}
