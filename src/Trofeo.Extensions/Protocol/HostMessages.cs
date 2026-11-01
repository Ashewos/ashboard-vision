using System.Text.Json;
using System.Text.Json.Serialization;
using AshboardVision.Sdk;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions.Protocol;

/// <summary>
/// Un message entre le service et l'hôte de module. Le contrat est documenté (d'autres langages pourront parler à un
/// service) : le discriminant s'appelle <c>type</c>, les noms de message et de propriété sont en camelCase, les énumérations
/// voyagent en texte et les permissions avec leur nom de manifeste (<c>reseau-local</c>, <c>internet</c>…).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(Open), "open")]
[JsonDerivedType(typeof(Close), "close")]
[JsonDerivedType(typeof(AppearanceChanged), "appearanceChanged")]
[JsonDerivedType(typeof(Limits), "limits")]
[JsonDerivedType(typeof(FrameDone), "frameDone")]
[JsonDerivedType(typeof(Lookup), "lookup")]
[JsonDerivedType(typeof(RunAction), "runAction")]
[JsonDerivedType(typeof(SecretValue), "secretValue")]
[JsonDerivedType(typeof(DataValue), "dataValue")]
[JsonDerivedType(typeof(Shutdown), "shutdown")]
[JsonDerivedType(typeof(Ready), "ready")]
[JsonDerivedType(typeof(Frame), "frame")]
[JsonDerivedType(typeof(Status), "status")]
[JsonDerivedType(typeof(Log), "log")]
[JsonDerivedType(typeof(SecretGet), "secretGet")]
[JsonDerivedType(typeof(SecretSet), "secretSet")]
[JsonDerivedType(typeof(DataGet), "dataGet")]
[JsonDerivedType(typeof(LocalConnect), "localConnect")]
[JsonDerivedType(typeof(LookupReply), "lookupReply")]
[JsonDerivedType(typeof(ActionReply), "actionReply")]
[JsonDerivedType(typeof(Failure), "failure")]
[JsonDerivedType(typeof(LocalConnected), "localConnected")]
[JsonDerivedType(typeof(LocalRefused), "localRefused")]
public abstract record HostMessage
{
    /// <summary>
    /// La version du protocole parlée par ce logiciel (<see cref="Hello.Protocol"/>). Même numéro majeur : compatible ;
    /// le numéro mineur n'augmente que pour ajouter des messages ou des propriétés optionnelles.
    /// </summary>
    public const string ProtocolVersion = "1.0";
}

// --- Du service vers l'hôte ---------------------------------------------------------------------------------------------

/// <summary>Premier message du service : de quoi charger le module. <c>Appearance</c> est l'apparence résolue, en JSON.</summary>
public sealed record Hello(string Protocol, string ModuleDirectory, string Entry, string TypeId,
    IReadOnlyList<ModulePermission> Granted, string DataDirectory, JsonElement Appearance, int MaxFps) : HostMessage;

/// <summary>
/// Ouvre une instance (une case) du module. Une seule section de mémoire contient les deux tampons d'images : le tampon 0 au
/// décalage 0 et le tampon 1 au décalage <c>Width × Height × 4</c> (BGRA, alpha prémultiplié, pas = <c>Width × 4</c>).
/// <c>Buffer0</c> est la valeur, dans l'hôte, de la poignée de cette section (dupliquée par le service) ; <c>Buffer1</c> est
/// réservé et vaut toujours 0 dans le protocole 1.0.
/// </summary>
public sealed record Open(Guid InstanceId, JsonElement Settings, int Width, int Height, float TextScale, int Columns, int Rows,
    long Buffer0, long Buffer1) : HostMessage;

/// <summary>Ferme une instance.</summary>
public sealed record Close(Guid InstanceId) : HostMessage;

/// <summary>L'apparence a changé (thème, polices).</summary>
public sealed record AppearanceChanged(JsonElement Appearance) : HostMessage;

/// <summary>Nouveau plafond d'images par seconde pour toutes les instances.</summary>
public sealed record Limits(int MaxFps) : HostMessage;

/// <summary>Le service a fini de copier le tampon <c>Buffer</c> (0 ou 1) : l'hôte peut de nouveau y écrire.</summary>
public sealed record FrameDone(Guid InstanceId, int Buffer) : HostMessage;

/// <summary>Demande la liste d'un champ de recherche des réglages.</summary>
public sealed record Lookup(int Id, string FieldKey, string Query) : HostMessage;

/// <summary>Lance une action des réglages.</summary>
public sealed record RunAction(int Id, Guid InstanceId, string ActionKey, JsonElement Settings) : HostMessage;

/// <summary>Réponse à <see cref="SecretGet"/> (absente : pas de secret) ou à <see cref="SecretSet"/> (<c>Value</c> vide).</summary>
public sealed record SecretValue(int Id, string? Value) : HostMessage;

/// <summary>Réponse à <see cref="DataGet"/> : <c>Status</c> vaut <c>ok</c>, <c>denied</c> ou <c>unavailable</c>.</summary>
public sealed record DataValue(int Id, string Status, JsonElement? Value) : HostMessage;

/// <summary>Le service demande à l'hôte de s'arrêter.</summary>
public sealed record Shutdown : HostMessage;

// --- De l'hôte vers le service ----------------------------------------------------------------------------------------------

/// <summary>Le module est chargé ; <c>RequiredSdk</c> est la version du SDK dont il a besoin.</summary>
public sealed record Ready(string RequiredSdk) : HostMessage;

/// <summary>Une image est prête dans le tampon <c>Buffer</c> ; <c>Animating</c> dit que d'autres vont suivre sans qu'on les demande.</summary>
public sealed record Frame(Guid InstanceId, int Buffer, bool Animating) : HostMessage;

/// <summary>L'état d'une instance.</summary>
public sealed record Status(Guid InstanceId, ModuleHealth Health, string? Message) : HostMessage;

/// <summary>Une ligne du journal du module (<c>Level</c> : debug, info, warn, error).</summary>
public sealed record Log(string Level, string Message) : HostMessage;

/// <summary>Lit un secret du coffre du module.</summary>
public sealed record SecretGet(int Id, string Key) : HostMessage;

/// <summary>Écrit (ou, si <c>Value</c> est absente, efface) un secret du coffre du module.</summary>
public sealed record SecretSet(int Id, string Key, string? Value) : HostMessage;

/// <summary>Demande des données au service : <c>Kind</c> vaut <c>sensors</c> ou <c>nowPlaying</c>.</summary>
public sealed record DataGet(int Id, string Kind) : HostMessage;

/// <summary>Demande une connexion vers un service de ce PC, sur un port saisi dans les réglages de l'instance.</summary>
public sealed record LocalConnect(int Id, Guid InstanceId, int Port) : HostMessage;

/// <summary>Réponse à <see cref="Lookup"/>.</summary>
public sealed record LookupReply(int Id, IReadOnlyList<LookupResult> Results) : HostMessage;

/// <summary>Réponse à <see cref="RunAction"/>.</summary>
public sealed record ActionReply(int Id, ActionResult Result) : HostMessage;

/// <summary>L'hôte ne peut pas continuer (module introuvable, deux classes de module…) et s'arrête.</summary>
public sealed record Failure(string Message) : HostMessage;

// --- Réponses du service au relais ----------------------------------------------------------------------------------------------

/// <summary>La connexion est établie : deux bouts de tubes anonymes, dupliqués dans l'hôte (lecture, écriture).</summary>
public sealed record LocalConnected(int Id, long ReadHandle, long WriteHandle) : HostMessage;

/// <summary>La connexion est refusée (port non autorisé, permission absente, trop de connexions, service injoignable).</summary>
public sealed record LocalRefused(int Id, string Reason) : HostMessage;
