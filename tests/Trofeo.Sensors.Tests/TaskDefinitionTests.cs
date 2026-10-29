using System.Xml.Linq;

namespace Trofeo.Sensors.Tests;

public class TaskDefinitionTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static string Text(XElement parent, params string[] path)
    {
        var node = parent;
        foreach (var name in path)
            node = node.Element(Ns + name) ?? throw new Xunit.Sdk.XunitException($"Élément <{name}> absent.");
        return node.Value;
    }

    [Fact]
    public void TaskName_IsInTheProductFolder() =>
        Assert.Equal(@"\Ashboard Vision\Capteurs", TaskDefinition.TaskName);

    [Fact]
    public void LegacyTaskName_IsTheTaskOfTheOldName() =>
        Assert.Equal(@"\trofeo-HB-dashboard\Capteurs", TaskDefinition.LegacyTaskName);

    [Fact]
    public void Xml_DescribesTheTaskWithTheNewName()
    {
        var task = XDocument.Parse(TaskDefinition.Xml(@"C:\x\Trofeo.SensorHelper.exe")).Root!;

        Assert.StartsWith("Assistant de capteurs d'Ashboard Vision", Text(task, "RegistrationInfo", "Description"));
    }

    [Fact]
    public void Xml_RunsAsSystem_AtLogon_WithoutTimeLimit()
    {
        const string exe = @"C:\Program Files\Ashboard Vision\sensors\Trofeo.SensorHelper.exe";
        var task = XDocument.Parse(TaskDefinition.Xml(exe)).Root!;

        Assert.Equal(Ns + "Task", task.Name);

        var principal = task.Element(Ns + "Principals")!.Element(Ns + "Principal")!;
        Assert.Equal("S-1-5-18", Text(principal, "UserId"));
        Assert.Equal("HighestAvailable", Text(principal, "RunLevel"));

        var logon = task.Element(Ns + "Triggers")!.Element(Ns + "LogonTrigger")!;
        Assert.Null(logon.Element(Ns + "UserId")); // aucun utilisateur : toute session

        var settings = task.Element(Ns + "Settings")!;
        Assert.Equal("IgnoreNew", Text(settings, "MultipleInstancesPolicy"));
        Assert.Equal("false", Text(settings, "DisallowStartIfOnBatteries"));
        Assert.Equal("false", Text(settings, "StopIfGoingOnBatteries"));
        Assert.Equal("PT0S", Text(settings, "ExecutionTimeLimit"));
        Assert.Equal("PT1M", Text(settings, "RestartOnFailure", "Interval"));
        Assert.Equal("3", Text(settings, "RestartOnFailure", "Count"));
        Assert.Equal("7", Text(settings, "Priority"));
        Assert.Equal("true", Text(settings, "Hidden"));

        var actions = task.Element(Ns + "Actions")!;
        Assert.Equal(principal.Attribute("id")!.Value, actions.Attribute("Context")!.Value);
        Assert.Equal(exe, Text(actions, "Exec", "Command")); // chemin nu : pas de guillemets littéraux
    }

    [Fact]
    public void Xml_EscapesSpecialCharactersInTheCommand()
    {
        const string exe = @"C:\a&b\<x>\Trofeo.SensorHelper.exe";
        var task = XDocument.Parse(TaskDefinition.Xml(exe)).Root!;
        Assert.Equal(exe, Text(task.Element(Ns + "Actions")!, "Exec", "Command"));
    }
}
