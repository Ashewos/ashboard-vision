namespace Trofeo.ModuleKit;

/// <summary>La fabrique d'un type de module. Réservé aux modules d'origine ; sans garantie pour un module complémentaire.</summary>
public interface IModuleFactory
{
    /// <summary>Ce qui décrit le type de module. Réservé aux modules d'origine ; sans garantie pour un module complémentaire.</summary>
    ModuleDescriptor Descriptor { get; }

    /// <summary>Crée un module pour une case. Réservé aux modules d'origine ; sans garantie pour un module complémentaire.</summary>
    IModule Create(ModuleContext context);
}
