namespace AshboardVision.Sdk;

/// <summary>Un instantané des capteurs, lu par le service (permission « capteurs »).</summary>
/// <param name="CpuName">Le nom du processeur, s'il est connu.</param>
/// <param name="GpuName">Le nom de la carte graphique, s'il est connu.</param>
/// <param name="Values">Les valeurs mesurées, par nom (températures, charges, mémoire…) ; un capteur absent n'a pas d'entrée.</param>
public sealed record SensorReading(string? CpuName, string? GpuName, IReadOnlyDictionary<string, double> Values);
