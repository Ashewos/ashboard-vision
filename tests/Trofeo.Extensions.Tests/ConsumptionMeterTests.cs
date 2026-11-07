using Microsoft.Extensions.Time.Testing;
using Trofeo.Control;
using Trofeo.Extensions.Consumption;
using Trofeo.Extensions.Sandbox;

namespace Trofeo.Extensions.Tests;

// Ce que les modules consomment : la moyenne sur une minute, le repère « consommation élevée », et le rythme des relevés.
public sealed class ConsumptionMeterTests
{
    private const long MiB = 1024 * 1024;
    private const string Id = "alex.demo";
    private static readonly ProcessInfo Service = new(80 * MiB, 120 * MiB, 0.7);

    private static JobUsage Cpu(double seconds) => new(TimeSpan.FromSeconds(seconds), 0, false);

    private static FakeTimeProvider NewClock() => new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));

    private static ModuleConsumption Row(ConsumptionMeter meter, string id = Id) => meter.Read(Service).Single(r => r.Id == id);

    // --- Le calcul --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Meter_AboveTwoPercentForAMinute_IsHigh()
    {
        var clock = NewClock();
        var meter = new ConsumptionMeter(clock, processorCount: 4);
        meter.Begin(Id);

        clock.Advance(TimeSpan.FromSeconds(60));
        meter.Sample(Id, Cpu(60 * 4 * 0.03), 50 * MiB); // 3 % de la machine pendant une minute

        var row = Row(meter);
        Assert.True(row.High);
        Assert.Equal(3.0, row.CpuPercent, 1);
        Assert.True(row.IsCommunity);
    }

    [Fact]
    public void Meter_BelowTwoPercentForAMinute_IsNotHigh()
    {
        var clock = NewClock();
        var meter = new ConsumptionMeter(clock, processorCount: 4);
        meter.Begin(Id);

        clock.Advance(TimeSpan.FromSeconds(60));
        meter.Sample(Id, Cpu(60 * 4 * 0.019), 50 * MiB);

        Assert.False(Row(meter).High);
    }

    [Fact]
    public void Meter_ShortSpike_IsNotHigh()
    {
        // Deux secondes à fond sur un cœur, au milieu d'une minute calme : 1 % de la machine en moyenne. Un pic n'est pas un repère.
        var clock = NewClock();
        var meter = new ConsumptionMeter(clock, processorCount: 4);
        meter.Begin(Id);
        double cpu = 0;
        for (var i = 1; i <= 12; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            cpu += i == 6 ? 2.0 : 0.05;
            meter.Sample(Id, Cpu(cpu), 40 * MiB);
        }

        var row = Row(meter);
        Assert.False(row.High);
        Assert.InRange(row.CpuPercent, 1.0, 1.2);
    }

    [Fact]
    public void Meter_ShortMemorySpike_IsNotHigh_ButSustainedMemoryIs()
    {
        var clock = NewClock();
        var spiky = new ConsumptionMeter(clock, processorCount: 4);
        var heavy = new ConsumptionMeter(clock, processorCount: 4);
        spiky.Begin(Id);
        heavy.Begin(Id);
        for (var i = 1; i <= 12; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            spiky.Sample(Id, Cpu(0), (i == 6 ? 400 : 40) * MiB);
            heavy.Sample(Id, Cpu(0), 200 * MiB);
        }

        Assert.False(Row(spiky).High);
        Assert.True(Row(heavy).High);
        Assert.Equal(200 * MiB, Row(heavy).MemoryBytes);
    }

    [Fact]
    public void Meter_MemoryShownIsTheAverageOfTheMinute()
    {
        var clock = NewClock();
        var meter = new ConsumptionMeter(clock, processorCount: 4);
        meter.Begin(Id);
        foreach (var mib in new long[] { 40, 60, 80 })
        {
            clock.Advance(TimeSpan.FromSeconds(20));
            meter.Sample(Id, Cpu(0), mib * MiB);
        }

        Assert.Equal(60 * MiB, Row(meter).MemoryBytes);
    }

    [Fact]
    public void Meter_ObservedForLessThanAMinute_IsNeverHigh()
    {
        // Un module qui vient de démarrer et qui s'emballe dix secondes : on attend de voir une minute avant de le signaler.
        var clock = NewClock();
        var meter = new ConsumptionMeter(clock, processorCount: 4);
        meter.Begin(Id);

        clock.Advance(TimeSpan.FromSeconds(10));
        meter.Sample(Id, Cpu(10 * 4 * 0.5), 300 * MiB);

        var row = Row(meter);
        Assert.False(row.High);
        Assert.Equal(50.0, row.CpuPercent, 1); // la valeur affichée est déjà là
    }

    [Fact]
    public void Meter_WindowSlides_ACalmMinuteClearsTheMark()
    {
        var clock = NewClock();
        var meter = new ConsumptionMeter(clock, processorCount: 4);
        meter.Begin(Id);
        clock.Advance(TimeSpan.FromSeconds(60));
        meter.Sample(Id, Cpu(60 * 4 * 0.10), 50 * MiB);
        Assert.True(Row(meter).High);

        clock.Advance(TimeSpan.FromSeconds(60));
        meter.Sample(Id, Cpu(60 * 4 * 0.10 + 60 * 4 * 0.001), 50 * MiB);

        Assert.False(Row(meter).High);
    }

    [Fact]
    public void Meter_OneSamplePerMinute_StillGivesTheExactAverage()
    {
        // Le temps processeur cumulé du job donne la moyenne exacte, même avec un seul relevé par minute.
        var clock = NewClock();
        var meter = new ConsumptionMeter(clock, processorCount: 8);
        meter.Begin(Id);
        double cpu = 0;
        foreach (var share in new[] { 0.01, 0.01, 0.05 })
        {
            clock.Advance(TimeSpan.FromSeconds(60));
            cpu += 60 * 8 * share;
            meter.Sample(Id, Cpu(cpu), 30 * MiB);
        }

        var row = Row(meter);
        Assert.Equal(5.0, row.CpuPercent, 1);
        Assert.True(row.High);
    }

    [Fact]
    public void Meter_CpuIsClampedToTheMachine()
    {
        var clock = NewClock();
        var meter = new ConsumptionMeter(clock, processorCount: 2);
        meter.Begin(Id);
        clock.Advance(TimeSpan.FromSeconds(10));
        meter.Sample(Id, Cpu(1000), 10 * MiB);

        Assert.Equal(100, Row(meter).CpuPercent);
    }

    [Fact]
    public void Meter_CounterGoesBack_IsANewHost_NotANegativeUse()
    {
        // L'hôte a été relancé sans qu'on l'ait dit : son compteur repart de zéro.
        var clock = NewClock();
        var meter = new ConsumptionMeter(clock, processorCount: 4);
        meter.Begin(Id);
        clock.Advance(TimeSpan.FromSeconds(60));
        meter.Sample(Id, Cpu(100), 50 * MiB);
        clock.Advance(TimeSpan.FromSeconds(60));

        meter.Sample(Id, Cpu(1), 50 * MiB);

        Assert.Equal(0, Row(meter).CpuPercent);
        Assert.False(Row(meter).High);
    }

    [Fact]
    public void Meter_SampleWithoutBegin_StartsASeries()
    {
        var clock = NewClock();
        var meter = new ConsumptionMeter(clock, processorCount: 4);

        meter.Sample(Id, Cpu(5), 70 * MiB, "Démo");

        var row = Row(meter);
        Assert.Equal(("Démo", 70 * MiB, 0.0), (row.DisplayName, row.MemoryBytes, row.CpuPercent));
    }

    [Fact]
    public void Meter_DisplayNameDefaultsToTheId()
    {
        var meter = new ConsumptionMeter(NewClock(), processorCount: 4);

        meter.Sample(Id, Cpu(0), MiB);

        Assert.Equal(Id, Row(meter).DisplayName);
    }

    [Fact]
    public void Meter_End_RemovesTheModule()
    {
        var meter = new ConsumptionMeter(NewClock(), processorCount: 4);
        meter.Sample(Id, Cpu(0), MiB);

        meter.End(Id);

        Assert.DoesNotContain(meter.Read(Service), r => r.Id == Id);
    }

    [Fact]
    public void Meter_Read_OriginalModulesRowComesFirstAndComesFromTheServiceProcess()
    {
        var meter = new ConsumptionMeter(NewClock(), processorCount: 4);
        meter.Sample("zed.module", Cpu(0), MiB, "Zed");
        meter.Sample("alex.demo", Cpu(0), MiB, "Alex");

        var rows = meter.Read(Service);

        Assert.Equal(["Modules d'origine (dans le service)", "Alex", "Zed"], rows.Select(r => r.DisplayName));
        var original = rows[0];
        Assert.Equal((false, 0.7, 80 * MiB, false), (original.IsCommunity, original.CpuPercent, original.MemoryBytes, original.High));
        Assert.DoesNotContain('.', original.Id); // jamais l'identifiant d'un module de la communauté
    }

    [Fact]
    public void Meter_NothingSampled_OnlyTheServiceRow()
    {
        var rows = new ConsumptionMeter(NewClock()).Read(Service);

        Assert.Single(rows);
        Assert.False(rows[0].IsCommunity);
    }

    [Fact]
    public void Meter_FiveSecondSampling_KeepsABoundedHistory()
    {
        var clock = NewClock();
        var meter = new ConsumptionMeter(clock, processorCount: 4);
        meter.Begin(Id);
        for (var i = 0; i < 2000; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            meter.Sample(Id, Cpu(i * 0.05), 40 * MiB);
        }

        Assert.InRange(meter.PointCount(Id), 2, 20);
    }

    [Fact]
    public void Meter_EveryFiveSeconds_StillAveragesOverTheMinute()
    {
        var clock = NewClock();
        var meter = new ConsumptionMeter(clock, processorCount: 4);
        meter.Begin(Id);
        double cpu = 0;
        for (var i = 0; i < 100; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            cpu += 5 * 4 * 0.03; // 3 % de la machine, sans interruption
            meter.Sample(Id, Cpu(cpu), 40 * MiB);
        }

        var row = Row(meter);
        Assert.Equal(3.0, row.CpuPercent, 1);
        Assert.True(row.High);
    }

    // --- Le rythme des relevés ----------------------------------------------------------------------------------------------------

    private sealed class Rig : IDisposable
    {
        private int _reads;

        public Rig()
        {
            Meter = new ConsumptionMeter(Clock, processorCount: 4);
            Sampler = new ConsumptionSampler(Clock, Meter, id =>
            {
                Interlocked.Increment(ref _reads);
                return Reading is null ? new HostReading(Cpu(0), 40 * MiB, "Démo") : Reading(id);
            });
        }

        public CountingClock Clock { get; } = new();

        public ConsumptionMeter Meter { get; }

        public ConsumptionSampler Sampler { get; }

        public Func<string, HostReading?>? Reading { get; set; }

        public int Reads => Volatile.Read(ref _reads);

        public void Dispose() => Sampler.Dispose();
    }

    [Fact]
    public void Meter_NoHostRunning_TakesNoSample()
    {
        using var rig = new Rig();

        rig.Sampler.Touch(); // la fenêtre lit, mais aucun hôte ne tourne
        rig.Clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(0, rig.Reads);
        Assert.Equal(0, rig.Clock.TimersCreated); // pas même un minuteur
        Assert.Equal(0, PointsOf(rig.Meter));
    }

    [Fact]
    public void Meter_WindowNotReading_OneSamplePerMinute()
    {
        using var rig = new Rig();
        rig.Sampler.HostStarted(Id);

        for (var minute = 1; minute <= 5; minute++)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(minute - 1, rig.Reads);
            rig.Clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(minute, rig.Reads);
        }

        Assert.Equal(1, rig.Clock.TimersCreated); // un seul minuteur pour tous les hôtes
    }

    [Fact]
    public void Meter_WindowReading_SamplesEveryFiveSeconds()
    {
        using var rig = new Rig();
        rig.Sampler.HostStarted(Id);

        rig.Sampler.Touch(); // la fenêtre commence à lire : un relevé tout de suite
        Assert.Equal(1, rig.Reads);
        for (var second = 5; second <= 30; second += 5)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(5));
            rig.Sampler.Touch(); // elle relit toutes les 5 s
        }

        Assert.Equal(1 + 6, rig.Reads); // 1 immédiat + 6 relevés de 5 s (les Touch suivants n'en ajoutent pas)
    }

    [Fact]
    public void Meter_WindowStopsReading_ReturnsToOneSamplePerMinute()
    {
        using var rig = new Rig();
        rig.Sampler.HostStarted(Id);
        rig.Sampler.Touch();

        // Plus aucune requête : le rythme de 5 s dure encore 15 s, puis le relevé suivant remet la minute.
        rig.Clock.AdvanceInSteps(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5));
        var settled = rig.Reads;
        Assert.Equal(1 + 4, settled);

        rig.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(settled, rig.Reads); // plus rien pendant presque une minute
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(settled + 1, rig.Reads);
        rig.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(settled + 2, rig.Reads);
    }

    [Fact]
    public void Meter_WindowReadsAgainLater_ReturnsToFiveSeconds()
    {
        using var rig = new Rig();
        rig.Sampler.HostStarted(Id);
        rig.Sampler.Touch();
        rig.Clock.AdvanceInSteps(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5)); // elle s'est tue depuis longtemps
        var before = rig.Reads;

        rig.Sampler.Touch();
        rig.Clock.AdvanceInSteps(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));

        Assert.Equal(before + 1 + 2, rig.Reads); // le relevé immédiat, puis ceux de 5 s
    }

    [Fact]
    public void Meter_HostStarts_TakesNoImmediateSample_JustABaselineForTheMeter()
    {
        using var rig = new Rig();

        rig.Sampler.HostStarted(Id);

        Assert.Equal(0, rig.Reads); // le départ de l'hôte ne lit rien : le compteur du job part de zéro
        Assert.Equal(1, PointsOf(rig.Meter, Id));
    }

    [Fact]
    public void Meter_LastHostStops_TheTimerIsReleased_AndNothingIsSampledAnymore()
    {
        using var rig = new Rig();
        rig.Sampler.HostStarted(Id);
        rig.Clock.AdvanceInSteps(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30));
        var reads = rig.Reads;

        rig.Sampler.HostStopped(Id);
        rig.Sampler.Touch();
        rig.Clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(reads, rig.Reads);
        Assert.Equal(0, rig.Clock.TimersAlive);
        Assert.DoesNotContain(rig.Meter.Read(Service), r => r.Id == Id);
    }

    [Fact]
    public void Meter_TwoHosts_ShareOneTimer_AndTheTimerStaysUntilTheLastOneStops()
    {
        using var rig = new Rig();
        rig.Sampler.HostStarted("alex.un");
        rig.Sampler.HostStarted("alex.deux");

        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, rig.Reads);
        Assert.Equal(1, rig.Clock.TimersAlive);

        rig.Sampler.HostStopped("alex.un");
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(3, rig.Reads);
        Assert.Equal(1, rig.Clock.TimersAlive);

        rig.Sampler.HostStopped("alex.deux");
        Assert.Equal(0, rig.Clock.TimersAlive);
    }

    [Fact]
    public void Meter_HostStartedTwiceForTheSameId_IsOneHost()
    {
        using var rig = new Rig();
        rig.Sampler.HostStarted(Id);
        rig.Sampler.HostStarted(Id);

        rig.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, rig.Reads);
        Assert.Equal(1, rig.Clock.TimersCreated);
    }

    [Fact]
    public void Meter_HostGoneWhenSampled_IsSkippedWithoutBreakingTheOthers()
    {
        using var rig = new Rig();
        rig.Reading = id => id == "alex.parti" ? null : new HostReading(Cpu(0), MiB, "Reste");
        rig.Sampler.HostStarted("alex.parti");
        rig.Sampler.HostStarted("alex.reste");

        rig.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(2, rig.Reads);
        Assert.Contains(rig.Meter.Read(Service), r => r.Id == "alex.reste");
        Assert.Equal(1, rig.Meter.PointCount("alex.parti")); // rien de plus que son point de départ
    }

    [Fact]
    public void Meter_ReadingThrows_NextTickStillRuns()
    {
        using var rig = new Rig();
        var fail = true;
        rig.Reading = _ => fail ? throw new InvalidOperationException("mesure impossible") : new HostReading(Cpu(0), MiB, null);
        rig.Sampler.HostStarted(Id);

        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        fail = false;
        rig.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(2, rig.Reads);
        Assert.Equal(MiB, Row(rig.Meter).MemoryBytes);
    }

    [Fact]
    public void Meter_SamplesLandInTheMeter()
    {
        using var rig = new Rig();
        rig.Reading = _ => new HostReading(Cpu(60 * 4 * 0.05), 90 * MiB, "Démo");
        rig.Sampler.HostStarted(Id);

        rig.Clock.Advance(TimeSpan.FromMinutes(1));

        var row = Row(rig.Meter);
        Assert.Equal(("Démo", 90 * MiB, true), (row.DisplayName, row.MemoryBytes, row.High));
        Assert.Equal(5.0, row.CpuPercent, 1);
    }

    [Fact]
    public void Sampler_Dispose_ReleasesTheTimer_AndLaterCallsDoNothing()
    {
        var rig = new Rig();
        rig.Sampler.HostStarted(Id);
        rig.Sampler.Dispose();

        rig.Sampler.HostStarted("alex.autre");
        rig.Sampler.Touch();
        rig.Clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0, rig.Reads);
        Assert.Equal(0, rig.Clock.TimersAlive);
        rig.Sampler.Dispose(); // sans effet la seconde fois
    }

    private static int PointsOf(ConsumptionMeter meter, string id = Id) => meter.PointCount(id);
}
