using System.Text.Json;
using Trofeo.ModuleKit;

namespace Trofeo.Shared;

/// <summary>
/// La règle des ports que le logiciel relaie à un module qui a la permission « Cet ordinateur ». Un seul fichier source, inclus
/// (<c>Compile Include</c>) par le relais du service (<c>Trofeo.Extensions</c>) et par les aides de test (<c>AshboardVision.Sdk.Testing</c>) :
/// le faux des créateurs applique ainsi exactement la règle du vrai relais, sans type public de plus dans le SDK. Aucune dépendance Windows.
/// </summary>
internal static class PortRule
{
    /// <summary>
    /// Vrai si <paramref name="port"/> est la valeur d'un champ « port » de ces réglages (ou, quand le réglage n'est pas écrit,
    /// la valeur proposée par le champ). Un réglage qui n'est pas un nombre entier ne compte pas.
    /// </summary>
    public static bool IsAllowed(IReadOnlyList<PortField> portFields, JsonElement settings, int port)
    {
        ArgumentNullException.ThrowIfNull(portFields);
        if (port is < 1 or > 65535)
        {
            return false;
        }

        foreach (var field in portFields)
        {
            var current = field.Default;
            if (SettingsDocument.Get(settings, field.Key) is { } written
                && (written.ValueKind != JsonValueKind.Number || !written.TryGetInt32(out current)))
            {
                continue;
            }

            if (current == port)
            {
                return true;
            }
        }

        return false;
    }
}
