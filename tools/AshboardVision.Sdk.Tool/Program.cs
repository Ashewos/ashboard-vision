using System.CommandLine;
using System.CommandLine.Help;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

// L'enfermement (AppContainer, job object) n'existe que sous Windows : l'outil l'annonce, comme les assemblages qu'il appelle (Trofeo.Extensions
// vise net10.0-windows10.0.19041.0 ; l'outil, lui, ne peut pas viser une plateforme : voir son .csproj).
[assembly: SupportedOSPlatform("windows10.0.19041.0")]

namespace AshboardVision.Sdk.Tool;

/// <summary>
/// <c>ashboard-module</c> : le banc d'essai des modules de la communauté d'Ashboard Vision. <c>check</c> essaie un module comme le logiciel
/// le lance ; <c>pack</c> fabrique son paquet <c>.avmod</c>. Code de sortie : 0 si tout va bien, 1 sinon.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);

        // Les messages de la bibliothèque de commandes (aide, erreurs de saisie) sortent en français, comme les nôtres.
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");

        // Ctrl+C n'arrête pas le processus d'un coup : l'essai se termine proprement et supprime son conteneur d'essai.
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        var folder = new Argument<DirectoryInfo?>("dossier")
        {
            Description = "Le dossier du module (manifest.json et la DLL du module). Par défaut, le dossier où tu es.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var checkOut = new Option<DirectoryInfo?>("--out")
        {
            Description = $"Le dossier où écrire les images PNG (par défaut « {CheckCommand.DefaultOutputFolder} », dans le dossier où tu es).",
        };

        // La durée de la mesure n'est pas faite pour les créateurs (le contrat est 30 secondes) : elle sert aux tests de l'outil.
        var measure = new Option<int?>("--mesure")
        {
            Description = "La durée de la mesure, en secondes (30 par défaut).",
            Hidden = true,
        };

        var check = new Command("check", "Essaie le module comme le logiciel le lance : manifeste, démarrage enfermé, images par taille et par thème, permissions refusées, consommation, arrêt.")
        {
            folder,
            checkOut,
            measure,
        };
        check.SetAction((result, token) => CheckCommand.RunAsync(
            result.GetValue(folder),
            result.GetValue(checkOut),
            result.GetValue(measure) is { } seconds && seconds > 0 ? TimeSpan.FromSeconds(seconds) : CheckCommand.DefaultMeasure,
            Console.Out,
            token));

        var packFolder = new Argument<DirectoryInfo?>("dossier")
        {
            Description = "Le dossier du module (manifest.json et la DLL du module). Par défaut, le dossier où tu es.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var packOut = new Option<DirectoryInfo?>("--out")
        {
            Description = $"Le dossier où écrire le paquet et son empreinte (par défaut « {PackCommand.DefaultOutputFolder} », dans le dossier où tu es). Pas dans le dossier du module.",
        };

        var pack = new Command("pack", "Vérifie le manifeste et fabrique <id>-<version>.avmod et son empreinte .sha256, à joindre à ta version GitHub.")
        {
            packFolder,
            packOut,
        };
        pack.SetAction(result => PackCommand.Run(result.GetValue(packFolder), result.GetValue(packOut), Console.Out));

        var root = new RootCommand("ashboard-module : le banc d'essai des modules de la communauté d'Ashboard Vision.")
        {
            check,
            pack,
        };

        // Le texte de l'option d'aide n'est pas traduit par la bibliothèque.
        foreach (var option in root.Options.OfType<HelpOption>())
        {
            option.Description = "Affiche l'aide.";
        }

        // Le traitement de Ctrl+C est le nôtre (ci-dessus) : la bibliothèque n'en pose pas un second.
        return await root.Parse(args).InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = null }, cancel.Token).ConfigureAwait(false);
    }
}
