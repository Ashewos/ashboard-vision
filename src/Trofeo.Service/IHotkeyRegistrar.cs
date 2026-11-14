namespace Trofeo.Display;

/// <summary>
/// Ce qui enregistre les raccourcis auprès de Windows : <c>HotkeyWindow</c> en production, un faux dans les tests. Un enregistrement
/// appartient au fil qui a créé la fenêtre : on l'appelle depuis le fil de l'interface seulement.
/// </summary>
public interface IHotkeyRegistrar
{
    /// <summary>Enregistre un raccourci global ; rend faux si Windows le refuse (déjà pris par une autre application).</summary>
    bool Register(int id, uint modifiers, uint virtualKey);

    /// <summary>Retire un raccourci enregistré par <see cref="Register"/>.</summary>
    void Unregister(int id);
}
