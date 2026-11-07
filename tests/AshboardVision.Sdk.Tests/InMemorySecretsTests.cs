using AshboardVision.Sdk.Tool;

namespace AshboardVision.Sdk.Tests;

/// <summary>
/// Le coffre du banc d'essai passe, comme le coffre du logiciel, par l'écriture gardée : c'est le superviseur qui lui donne la règle
/// (les plafonds du service), de sorte que le banc refuse exactement ce que le service refuse.
/// </summary>
public sealed class InMemorySecretsTests
{
    [Fact]
    public void TrySet_RefusedByTheGuard_WritesNothingAndSeesTheCurrentContent()
    {
        var secrets = new InMemorySecrets();
        secrets.Set("jeton", "avant");
        IReadOnlyDictionary<string, string>? seen = null;

        var accepted = secrets.TrySet("jeton", "apres", current =>
        {
            seen = new Dictionary<string, string>(current);
            return false;
        });

        Assert.False(accepted);
        Assert.Equal("avant", secrets.Get("jeton"));
        Assert.Equal(new Dictionary<string, string> { ["jeton"] = "avant" }, seen);
    }

    [Fact]
    public void TrySet_AcceptedByTheGuard_WritesAndNullRemoves()
    {
        var secrets = new InMemorySecrets();

        Assert.True(secrets.TrySet("jeton", "abc", _ => true));
        Assert.Equal("abc", secrets.Get("jeton"));
        Assert.True(secrets.TrySet("jeton", null, _ => true));
        Assert.Null(secrets.Get("jeton"));
    }

    [Fact]
    public void ItIsAGuardedStore_SoTheSupervisorAppliesTheServiceLimits()
    {
        Assert.IsAssignableFrom<Trofeo.Core.Secrets.IGuardedSecretStore>(new InMemorySecrets());
    }
}
