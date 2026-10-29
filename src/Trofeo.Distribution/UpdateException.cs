namespace Trofeo.Distribution;

/// <summary>Une mise à jour qui n'a pas pu se faire. Le message est écrit pour être lu par Alex, en français.</summary>
public sealed class UpdateException(string message) : Exception(message);
