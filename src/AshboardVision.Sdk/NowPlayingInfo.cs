namespace AshboardVision.Sdk;

/// <summary>La musique en cours, lue par le service (permission « musique »).</summary>
/// <param name="Title">Le titre.</param>
/// <param name="Artist">L'artiste.</param>
/// <param name="AppName">L'application qui joue.</param>
/// <param name="Position">Où en est la lecture.</param>
/// <param name="Duration">La durée du morceau (zéro si inconnue).</param>
/// <param name="IsPlaying">Vrai en lecture, faux en pause.</param>
/// <param name="CoverPng">La pochette, en PNG, ou <c>null</c>.</param>
public sealed record NowPlayingInfo(
    string Title, string Artist, string AppName, TimeSpan Position, TimeSpan Duration, bool IsPlaying, byte[]? CoverPng);
