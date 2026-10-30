namespace Trofeo.Control;

/// <summary>Constantes du canal de contrôle entre le service d'affichage et la fenêtre de réglage.</summary>
public static class ControlPipe
{
    public const string Name = "trofeo-HB-dashboard.control";

    /// <summary>Version du protocole ; le client refuse un service qui en annonce une autre.</summary>
    public const int ProtocolVersion = 2;
}
