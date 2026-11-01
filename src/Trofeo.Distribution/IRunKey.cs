namespace Trofeo.Distribution;

/// <summary>
/// La clé <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>, derrière une interface pour que la préparation et le nettoyage
/// se testent sans écrire dans le registre. <see cref="Write"/> et <see cref="Delete"/> peuvent lever <see cref="IOException"/>,
/// <see cref="UnauthorizedAccessException"/> ou <see cref="System.Security.SecurityException"/> si Windows refuse.
/// </summary>
public interface IRunKey
{
    /// <summary>La valeur <paramref name="name"/>, ou <c>null</c> si elle n'existe pas.</summary>
    string? Read(string name);

    void Write(string name, string value);

    /// <summary>Supprime la valeur ; une valeur absente n'est pas une erreur.</summary>
    void Delete(string name);
}
