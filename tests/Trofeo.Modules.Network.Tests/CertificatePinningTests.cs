using System.Net.Security;
using Trofeo.ModuleKit;

namespace Trofeo.Modules.Network.Tests;

public class CertificatePinningTests
{
    [Theory]
    [InlineData(null, "AA", SslPolicyErrors.None, true, null)]                                           // aucune erreur : accepté
    [InlineData("BB", "AA", SslPolicyErrors.None, true, null)]                                           // même avec une autre empreinte mémorisée
    [InlineData(null, "AA", SslPolicyErrors.RemoteCertificateChainErrors, true, "AA")]                   // première fois : la présentée devient la mémorisée
    [InlineData("", "AA", SslPolicyErrors.RemoteCertificateChainErrors, true, "AA")]
    [InlineData("AA", "AA", SslPolicyErrors.RemoteCertificateChainErrors, true, null)]                   // identique
    [InlineData("AA", "aa", SslPolicyErrors.RemoteCertificateChainErrors, true, null)]                   // casse indifférente
    [InlineData("BB", "AA", SslPolicyErrors.RemoteCertificateChainErrors, false, null)]                  // différente : refusée
    [InlineData("BB", "AA", SslPolicyErrors.RemoteCertificateNameMismatch, false, null)]
    [InlineData(null, "", SslPolicyErrors.RemoteCertificateChainErrors, false, null)]                    // rien à mémoriser : refusé
    public void Check_Table(string? pinned, string presented, SslPolicyErrors errors, bool accept, string? newPin)
    {
        var result = CertificatePinning.Check(pinned, presented, errors);

        Assert.Equal((accept, newPin), result);
    }
}
