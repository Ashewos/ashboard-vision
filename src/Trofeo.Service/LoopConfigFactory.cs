using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;

namespace Trofeo.Display;

/// <summary>Fabrique ce que la boucle applique (thème et fond compris) à partir d'une disposition.</summary>
public static class LoopConfigFactory
{
    /// <summary>
    /// AppearanceResolver + BackgroundFactory, comme Program.Run aujourd'hui ; les avertissements vont à <paramref name="warn"/>.
    /// La boucle devient propriétaire du fond du résultat.
    /// </summary>
    /// <param name="video">
    /// Ouvre un fond vidéo (Media Foundation, côté Windows) à la position donnée, depuis le début du fichier ; <c>null</c> : les
    /// vidéos ne sont pas prises en charge. La carte qui décode n'est pas dans ses arguments : l'appelant la choisit (c'est celle de
    /// <paramref name="videoDecoderLuid"/>).
    /// </param>
    /// <param name="current">
    /// Ce que la boucle applique en ce moment, s'il y en a un. Son fond est gardé (même instance, la vidéo n'est pas rouverte)
    /// quand le fond demandé n'a pas changé et que : soit le fond par défaut du thème résolu est resté le même (l'opacité, la
    /// police, la garde de lisibilité ou un autre thème de même fond par défaut ne le touchent pas), soit le fond est un choix
    /// explicite (couleur, image, GIF, vidéo) qui s'est chargé sans repli (<see cref="LoopConfig.BackgroundIsThemeFallback"/>), donc sans dépendre du thème : changer de thème ne
    /// redémarre plus une vidéo choisie. Un fond de repli (le fond du thème, ou un choix qui a échoué, même quand l'hôte vidéo a averti seul) est reconstruit quand le
    /// fond par défaut du thème change. Les avertissements du fond gardé sont redonnés à <paramref name="warn"/>.
    /// </param>
    /// <param name="videoDecoderLuid">
    /// La carte qui doit décoder la vidéo (son LUID) ; <c>null</c> : le processeur. Une vidéo gardée qui se décode sur une autre
    /// carte que celle-ci (réglages de l'accélération changés, panne de la carte, nouvel essai) est rouverte, à la position où elle
    /// en est ; une vidéo qui se décode déjà sur la bonne carte est gardée, quel que soit le reste.
    /// </param>
    public static LoopConfig Build(
        DashboardLayout layout, ISystemFonts fonts, Func<string, TimeSpan, IBackground?>? video, Action<string> warn,
        LoopConfig? current = null, long? videoDecoderLuid = null)
    {
        var appearance = layout.Appearance ?? AppearanceSettings.Default;
        var theme = AppearanceResolver.Resolve(appearance, fonts, warn);

        // Un fond du thème (par défaut, ou repli d'un choix raté) dépend du thème ; un choix explicite chargé tel quel, non.
        var reopenAt = TimeSpan.Zero;
        if (current is not null
            && Equals(current.Layout.Appearance?.Background, appearance.Background)
            && (!current.BackgroundIsThemeFallback || Equals(current.Theme.DefaultBackground, theme.DefaultBackground))
            && !DecoderIsStale(current, videoDecoderLuid, out reopenAt))
        {
            foreach (var message in current.BackgroundWarnings) warn(message);
            return new LoopConfig(layout, theme, current.Background)
            {
                BackgroundWarnings = current.BackgroundWarnings,
                BackgroundIsThemeFallback = current.BackgroundIsThemeFallback,
            };
        }

        var backgroundWarnings = new List<string>();
        var openerFailed = false; // l'hôte vidéo qui rend null a averti lui-même : la fabrique n'ajoute rien
        Func<string, IBackground?>? trackedVideo = video is null ? null : path =>
        {
            var opened = video(path, reopenAt);
            if (opened is null) openerFailed = true;
            return opened;
        };
        var background = BackgroundFactory.Create(appearance.Background, theme, trackedVideo, message =>
        {
            backgroundWarnings.Add(message);
            warn(message);
        });
        var isThemeFallback = appearance.Background is not { Kind: not BackgroundKind.Theme }
            || backgroundWarnings.Count > 0 || openerFailed;
        return new LoopConfig(layout, theme, background)
        {
            BackgroundWarnings = backgroundWarnings,
            BackgroundIsThemeFallback = isThemeFallback,
        };
    }

    /// <summary>
    /// Vrai si le fond de <paramref name="current"/> est une vidéo qui se décode ailleurs que sur la carte voulue
    /// (<paramref name="wantedLuid"/>, <c>null</c> : le processeur) : elle est à rouvrir, à <paramref name="position"/>.
    /// Le fond de repli du thème, une image, une couleur : jamais (ils ne se décodent pas sur une carte).
    /// </summary>
    public static bool DecoderIsStale(LoopConfig current, long? wantedLuid, out TimeSpan position)
    {
        if (current.Background is ISeekableBackground seekable && !current.BackgroundIsThemeFallback && seekable.DecoderLuid != wantedLuid)
        {
            position = seekable.Position;
            return true;
        }

        position = TimeSpan.Zero;
        return false;
    }
}
