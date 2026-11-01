using System.Xml.Linq;

namespace Trofeo.Sensors;

/// <summary>Définition de la tâche planifiée qui lance l'assistant de capteurs sous SYSTEM à l'ouverture de toute session.</summary>
public static class TaskDefinition
{
    /// <summary>Nom complet de la tâche, dossier compris.</summary>
    public const string TaskName = @"\Ashboard Vision\Capteurs";

    /// <summary>Nom de la tâche d'avant le changement de nom : l'installation l'arrête et la supprime.</summary>
    public const string LegacyTaskName = @"\trofeo-HB-dashboard\Capteurs";

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>
    /// XML d'import pour <c>schtasks /Create /XML</c>, déclaré en UTF-16 : à enregistrer dans ce codage. La commande est
    /// le chemin nu, sans guillemets, que le planificateur sait lire même avec des espaces.
    /// </summary>
    public static string Xml(string helperExePath)
    {
        var task = new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo",
                new XElement(Ns + "Description", "Assistant de capteurs d'Ashboard Vision (lecture élevée des capteurs matériels).")),
            new XElement(Ns + "Triggers",
                new XElement(Ns + "LogonTrigger", new XElement(Ns + "Enabled", "true"))),
            new XElement(Ns + "Principals",
                new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(Ns + "UserId", "S-1-5-18"),
                    new XElement(Ns + "RunLevel", "HighestAvailable"))),
            new XElement(Ns + "Settings",
                new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                new XElement(Ns + "AllowHardTerminate", "true"),
                new XElement(Ns + "StartWhenAvailable", "true"),
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "Hidden", "true"),
                new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
                new XElement(Ns + "Priority", "7"),
                new XElement(Ns + "RestartOnFailure",
                    new XElement(Ns + "Interval", "PT1M"),
                    new XElement(Ns + "Count", "3"))),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(Ns + "Exec", new XElement(Ns + "Command", helperExePath))));

        using var writer = new StringWriter(); // UTF-16 : la déclaration générée l'annonce
        new XDocument(new XDeclaration("1.0", "UTF-16", null), task).Save(writer);
        return writer.ToString();
    }
}
