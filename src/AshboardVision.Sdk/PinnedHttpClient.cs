using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Trofeo.ModuleKit;

namespace AshboardVision.Sdk;

/// <summary>Fabrique le client HTTP d'un module : relais pour les adresses de ce PC, certificat épinglé à la demande.</summary>
internal static class PinnedHttpClient
{
    private const string PinPrefix = "cert:";

    public static HttpClient Create(ModuleServices services, string? certificateKey)
    {
        var handler = new SocketsHttpHandler
        {
            // Le proxy du système n'est pas lisible depuis l'enfermement, et le relais vers ce PC n'a pas de proxy.
            UseProxy = false,
            ConnectCallback = (context, ct) => ConnectAsync(services, context.DnsEndPoint, ct),
        };

        if (certificateKey is not null)
        {
            var pinKey = PinPrefix + certificateKey;
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    certificate is not null && ValidatePin(services.Secrets, pinKey, Fingerprint(certificate), errors),
            };
        }

        return new HttpClient(handler, disposeHandler: true);
    }

    /// <summary>Vrai pour les adresses de ce PC : <c>localhost</c>, <c>127.0.0.1</c> et <c>::1</c> (avec ou sans crochets).</summary>
    internal static bool IsThisComputer(string host)
    {
        var name = host.Trim('[', ']');
        return string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase) || name is "127.0.0.1" or "::1";
    }

    private static async ValueTask<Stream> ConnectAsync(ModuleServices services, DnsEndPoint endPoint, CancellationToken ct)
    {
        if (IsThisComputer(endPoint.Host))
        {
            var result = await services.ConnectLocalAsync(endPoint.Port, ct).ConfigureAwait(false);
            return result is { Status: DataStatus.Ok, Value: { } stream }
                ? stream
                : throw new HttpRequestException(result.Status == DataStatus.Denied
                    ? $"Connexion à ce PC refusée (port {endPoint.Port}) : la permission « Cet ordinateur » est nécessaire, et le port doit venir des réglages de la case."
                    : $"Impossible de joindre le port {endPoint.Port} de ce PC.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endPoint, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Épinglage : première empreinte vue mémorisée dans le coffre, toute autre refusée.</summary>
    internal static bool ValidatePin(ISecretStore secrets, string pinKey, string presentedSha256, SslPolicyErrors errors)
    {
        var (accept, newPin) = CertificatePinning.Check(errors == SslPolicyErrors.None ? null : secrets.Get(pinKey), presentedSha256, errors);
        if (newPin is not null)
        {
            secrets.Set(pinKey, newPin);
        }

        return accept;
    }

    private static string Fingerprint(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
}
