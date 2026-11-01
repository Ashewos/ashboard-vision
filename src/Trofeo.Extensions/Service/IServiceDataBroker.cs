using AshboardVision.Sdk;

namespace Trofeo.Extensions.Service;

/// <summary>
/// Ce que le service sait lire pour un module qui en a la permission : les capteurs et la musique en cours. Le service reste
/// le seul à toucher au matériel ; le superviseur du module (<see cref="ModuleHostSupervisor"/>) vérifie la permission avant
/// d'appeler, donc l'implémentation n'a pas à le faire. Les méthodes sont appelées depuis un fil du pool, jamais depuis la
/// boucle d'affichage, et peuvent prendre un peu de temps.
/// </summary>
public interface IServiceDataBroker
{
    /// <summary>Un instantané des capteurs ; <see cref="DataStatus.Unavailable"/> si les capteurs ne répondent pas.</summary>
    DataResult<SensorReading> Sensors();

    /// <summary>La musique en cours ; <see cref="DataStatus.Unavailable"/> si rien ne joue.</summary>
    DataResult<NowPlayingInfo> NowPlaying();
}
