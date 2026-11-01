using CommunityToolkit.Mvvm.ComponentModel;
using Trofeo.Control;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.ViewModels;

/// <summary>
/// La pastille de l'écran (« Écran branché », « Mode émulé »…) : un texte et un point de couleur, d'après la liaison avec le
/// service. L'en-tête de la fenêtre et la première étape de l'assistant montrent la même, par <c>ScreenPillView</c>.
/// </summary>
public sealed partial class ScreenPillViewModel(IServiceLink link) : ObservableObject
{
    [ObservableProperty]
    private string _text = "";

    // Le point : un seul des quatre est vrai.
    [ObservableProperty] private bool _online;
    [ObservableProperty] private bool _idle;
    [ObservableProperty] private bool _amber;
    [ObservableProperty] private bool _refuse;

    /// <summary>Relit la liaison (à chaque changement de l'état du service).</summary>
    public void Update()
    {
        var (text, dot) = Read();
        Text = text;
        Online = dot == Dot.Online;
        Idle = dot == Dot.Idle;
        Amber = dot == Dot.Amber;
        Refuse = dot == Dot.Refuse;
    }

    private enum Dot { Online, Idle, Amber, Refuse }

    private (string Text, Dot Dot) Read()
    {
        if (link.State == LinkState.ServiceStopped)
            return (Texts.LinkStopped, Dot.Refuse);
        if (link.State == LinkState.VersionMismatch)
            return (Texts.LinkVersionMismatch, Dot.Refuse);
        if (link.State != LinkState.Connected || link.Status is not { } status)
            return (Texts.LinkConnecting, Dot.Idle);
        if (status.Emulated)
            return (Texts.EmulatedMode, Dot.Amber);
        return status.Screen.State switch
        {
            LinkScreenState.Ready => (Texts.ScreenOnline, Dot.Online),
            LinkScreenState.Recovering => (Texts.ScreenRecovering, Dot.Amber),
            _ => (Texts.ScreenIdle, Dot.Idle),
        };
    }
}
