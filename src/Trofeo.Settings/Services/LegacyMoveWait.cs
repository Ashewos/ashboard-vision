using System.Runtime.InteropServices;

namespace Trofeo.Settings.Services;

/// <summary>Ce que la fenêtre doit faire avant de toucher à son dossier de réglages.</summary>
internal enum LegacyMoveOutcome
{
    /// <summary>Rien à attendre : le dossier peut être utilisé.</summary>
    Proceed,

    /// <summary>Le service n'a pas pu être lancé, il est donc inutile d'attendre le déménagement.</summary>
    ServiceMissing,

    /// <summary>Le nouveau dossier n'est pas apparu dans le délai : la fenêtre se retire sans rien créer.</summary>
    NotMoved,
}

/// <summary>
/// Le déménagement des réglages d'avant le changement de nom (<c>trofeo-HB-dashboard</c> vers <c>ashboard-vision</c>) appartient au service :
/// c'est lui qui renomme le dossier, à son premier lancement. La fenêtre ne doit donc ni migrer ni rien créer dans le nouveau dossier (le
/// journal y crée <c>logs</c>) avant que le service l'ait fait : le service verrait deux dossiers, garderait l'ancien tel quel et partirait de
/// la disposition par défaut, sans le coffre ni l'autorisation de la Freebox. Elle demande donc le démarrage du service et attend.
/// </summary>
internal static class LegacyMoveWait
{
    /// <summary>Le temps laissé au service pour déménager les réglages.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>L'intervalle entre deux regards sur le nouveau dossier.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Vrai quand l'ancien dossier est encore seul en place : le service ne l'a pas déménagé. Si les deux existent, il a déjà tranché
    /// (<c>AlreadyMoved</c>) et la fenêtre n'a rien à attendre.
    /// </summary>
    public static bool MustWait(bool legacyExists, bool newExists) => legacyExists && !newExists;

    /// <summary>
    /// Décide, et au besoin attend. <paramref name="startService"/> demande le démarrage du service (faux : introuvable) ;
    /// <paramref name="sleep"/> sépare deux sondages. Ne crée ni ne déplace aucun dossier.
    /// </summary>
    public static LegacyMoveOutcome Run(string legacyDir, string newDir, Func<bool> startService, Action<TimeSpan> sleep)
    {
        if (!MustWait(Directory.Exists(legacyDir), Directory.Exists(newDir)))
            return LegacyMoveOutcome.Proceed;
        if (!startService())
            return LegacyMoveOutcome.ServiceMissing;

        for (var waited = TimeSpan.Zero; waited < Timeout; waited += Poll)
        {
            sleep(Poll);
            if (Directory.Exists(newDir))
                return LegacyMoveOutcome.Proceed;
        }

        return LegacyMoveOutcome.NotMoved;
    }

    /// <summary>Le message à montrer avant de quitter ; <c>null</c> quand la fenêtre peut s'ouvrir.</summary>
    public static string? MessageFor(LegacyMoveOutcome outcome) => outcome switch
    {
        LegacyMoveOutcome.NotMoved => Texts.MoveInProgress,
        LegacyMoveOutcome.ServiceMissing => Texts.MoveServiceMissing,
        _ => null,
    };

    /// <summary>Montre un message dans une boîte de Windows : la fenêtre de réglage n'existe pas encore à ce moment-là.</summary>
    public static void Show(string message)
    {
        try
        {
            MessageBoxW(IntPtr.Zero, message, Texts.WindowTitle, 0x40 /* MB_ICONINFORMATION */);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Pas de boîte de message possible : la fenêtre se retire quand même, sans rien créer.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
