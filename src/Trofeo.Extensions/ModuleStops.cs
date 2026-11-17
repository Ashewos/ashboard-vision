namespace Trofeo.Extensions;

/// <summary>Les arrêts brusques d'un module de la communauté depuis le démarrage du service.</summary>
/// <param name="Count">Combien de fois son hôte s'est arrêté sans que personne l'ait voulu (relances comprises).</param>
/// <param name="LastAt">Quand s'est arrêté le dernier (UTC).</param>
public sealed record ModuleStops(int Count, DateTimeOffset LastAt);
