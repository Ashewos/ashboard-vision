using SkiaSharp;
using Trofeo.Extensions.Protocol;

namespace Trofeo.Extensions.Service;

/// <summary>Ce qui fait la taille d'une image de case : ce que l'hôte doit connaître pour ouvrir l'instance.</summary>
/// <param name="Width">Largeur de la case, en pixels.</param>
/// <param name="Height">Hauteur de la case, en pixels.</param>
/// <param name="Columns">Colonnes du damier occupées.</param>
/// <param name="Rows">Rangées du damier occupées.</param>
/// <param name="TextScale">Agrandissement du texte de la case.</param>
internal readonly record struct FrameGeometry(int Width, int Height, int Columns, int Rows, float TextScale);

/// <summary>
/// Une case ouverte dans l'hôte, pour une taille donnée : la section d'images partagée avec l'hôte et le bitmap où le service
/// copie chaque image reçue. Une case qui change de taille reçoit un nouvel <see cref="InstanceSlot"/> (et un nouvel identifiant,
/// pour qu'une image tardive de l'ancienne taille ne soit jamais prise pour une neuve).
/// </summary>
internal sealed class InstanceSlot : IDisposable
{
    private readonly object _gate = new();
    private SKBitmap? _scratch;
    private bool _disposed;

    public InstanceSlot(RemoteModule owner, FrameGeometry geometry)
    {
        Owner = owner;
        Geometry = geometry;
        Buffers = new FrameBuffers(geometry.Width, geometry.Height);
    }

    /// <summary>L'identifiant de l'instance dans le protocole.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    public RemoteModule Owner { get; }

    public FrameGeometry Geometry { get; }

    public FrameBuffers Buffers { get; }

    /// <summary>
    /// Copie le tampon <paramref name="buffer"/> (0 ou 1, déjà vérifié par l'appelant) dans un bitmap à soi, que l'appelant
    /// pose ensuite dans la case. <c>null</c> si la case est déjà refermée.
    /// </summary>
    public SKBitmap? CopyFrame(int buffer)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            var target = Interlocked.Exchange(ref _scratch, null) ?? NewBitmap();
            try
            {
                Buffers.CopyTo(buffer, target);
            }
            catch
            {
                target.Dispose();
                throw;
            }

            return target;
        }
    }

    /// <summary>Reprend l'image que la case vient de remplacer : elle servira de bitmap à la copie suivante (même taille) ou sera libérée.</summary>
    public void Recycle(SKBitmap? replaced)
    {
        if (replaced is null)
        {
            return;
        }

        lock (_gate)
        {
            if (!_disposed && _scratch is null && replaced.Width == Geometry.Width && replaced.Height == Geometry.Height)
            {
                _scratch = replaced;
                return;
            }
        }

        replaced.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _scratch?.Dispose();
            _scratch = null;
            Buffers.Dispose();
        }
    }

    private SKBitmap NewBitmap() => new(new SKImageInfo(Geometry.Width, Geometry.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
}
