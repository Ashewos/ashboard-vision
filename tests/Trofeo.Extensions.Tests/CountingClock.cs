using Microsoft.Extensions.Time.Testing;

namespace Trofeo.Extensions.Tests;

/// <summary>Un faux temps qui compte les minuteurs créés et libérés : de quoi affirmer qu'un composant n'en garde aucun quand il n'a rien à faire.</summary>
internal sealed class CountingClock : FakeTimeProvider
{
    private int _created;
    private int _disposed;

    public CountingClock()
        : base(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero))
    {
    }

    /// <summary>Le nombre de minuteurs créés depuis le début.</summary>
    public int TimersCreated => Volatile.Read(ref _created);

    /// <summary>Le nombre de minuteurs créés et pas encore libérés.</summary>
    public int TimersAlive => Volatile.Read(ref _created) - Volatile.Read(ref _disposed);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Interlocked.Increment(ref _created);
        return new CountedTimer(base.CreateTimer(callback, state, dueTime, period), () => Interlocked.Increment(ref _disposed));
    }

    private sealed class CountedTimer(ITimer inner, Action onDispose) : ITimer
    {
        private int _done;

        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                onDispose();
            }

            inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Faire avancer un faux temps par petits pas. <c>FakeTimeProvider.Advance</c> d'un coup lit l'heure à l'arrivée dans tous les rappels
/// des minuteurs périodiques qu'il croise ; par pas d'une période, chaque rappel voit l'heure qu'il aurait vue en vrai.
/// </summary>
internal static class ClockSteps
{
    public static void AdvanceInSteps(this FakeTimeProvider clock, TimeSpan total, TimeSpan step)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += step)
        {
            clock.Advance(step < total - elapsed ? step : total - elapsed);
        }
    }
}
