using CommunityToolkit.Mvvm.ComponentModel;
using Unbounded.Panorama.Sensors;

namespace Unbounded.Panorama.App.ViewModels;

/// <summary>One sensor in the picker, with its live value.</summary>
public sealed partial class SensorRow(SensorInfo info) : ObservableObject
{
    public SensorInfo Info { get; } = info;
    public string Id => Info.Id;
    public string Name => Info.Name;
    public string Group => Info.Group;
    public string Type => Info.Type;

    [ObservableProperty] public partial string ValueText { get; set; } = "";
}
