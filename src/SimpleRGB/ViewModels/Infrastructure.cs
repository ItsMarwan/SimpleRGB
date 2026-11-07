using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SimpleRGB.Core.Lighting;

namespace SimpleRGB.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute) : this(_ => execute()) { }
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

public static class LightingVisuals
{
    private static readonly Brush OffBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x2C, 0x32)));

    public static Brush BrushFor(ZoneLighting lighting) => lighting.Mode switch
    {
        LightingMode.Off => OffBrush,
        LightingMode.Rainbow or LightingMode.ColorCycle => (Brush)Application.Current.Resources["RainbowBrush"],
        LightingMode.Music when lighting.MusicColors != MusicColors.SameColor => (Brush)Application.Current.Resources["RainbowBrush"],
        LightingMode.Gradient or LightingMode.ColorWave or LightingMode.Sparkle =>
            Freeze(new LinearGradientBrush(ToColor(lighting.BaseColor), ToColor(lighting.SecondColor), 0)),
        _ => Freeze(new SolidColorBrush(ToColor(lighting.BaseColor))),
    };

    public static string ModeText(LightingMode mode) => mode switch
    {
        LightingMode.Static => "Static",
        LightingMode.Breathing => "Breathing",
        LightingMode.ColorCycle => "Color cycle",
        LightingMode.Rainbow => "Rainbow wave",
        LightingMode.Gradient => "Gradient",
        LightingMode.ColorWave => "Color wave",
        LightingMode.Comet => "Comet",
        LightingMode.Sparkle => "Sparkle",
        LightingMode.Candle => "Candle",
        LightingMode.Heartbeat => "Heartbeat",
        LightingMode.Music => "Music",
        LightingMode.Off => "Off",
        _ => mode.ToString(),
    };

    public static string Describe(ZoneLighting lighting) => lighting.Mode switch
    {
        LightingMode.Static or LightingMode.Breathing or LightingMode.Comet or LightingMode.Candle or LightingMode.Heartbeat
            => $"{ModeText(lighting.Mode)} · {lighting.BaseColor.ToHex()}",
        LightingMode.Music => "Music · " + lighting.MusicReact switch
        {
            MusicReact.Beat => "beat",
            MusicReact.Bass => "bass",
            MusicReact.Vocals => "vocals",
            MusicReact.Treble => "treble",
            _ => "everything",
        },
        _ => ModeText(lighting.Mode),
    };

    public static Color ToColor(RgbColor c) => Color.FromRgb(c.R, c.G, c.B);
    public static RgbColor ToRgb(Color c) => new(c.R, c.G, c.B);

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
}
