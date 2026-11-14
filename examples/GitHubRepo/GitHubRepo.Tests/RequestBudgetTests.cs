using Microsoft.Extensions.Time.Testing;

namespace GitHubRepo.Tests;

/// <summary>Le budget de requêtes du module : une fenêtre glissante, comptée requête par requête.</summary>
public sealed class RequestBudgetTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    [Fact]
    public void CanAfford_StopsAtTheLimit()
    {
        var budget = new RequestBudget(50, Hour, new FakeTimeProvider());
        for (var i = 0; i < 48; i++)
        {
            budget.Record();
        }

        Assert.True(budget.CanAfford(2));
        Assert.False(budget.CanAfford(3));
        Assert.Equal(48, budget.Used);
    }

    [Fact]
    public void Requests_SlideOutOfTheWindow_AndTheBudgetComesBack()
    {
        var time = new FakeTimeProvider();
        var budget = new RequestBudget(50, Hour, time);
        for (var i = 0; i < 10; i++)
        {
            budget.Record(); // 10 requêtes à l'heure 0
        }

        time.Advance(TimeSpan.FromMinutes(30));
        for (var i = 0; i < 40; i++)
        {
            budget.Record(); // 40 à la demi-heure : le budget est dépensé
        }

        Assert.False(budget.CanAfford(1));

        time.Advance(TimeSpan.FromMinutes(30)); // une heure pile : les 10 premières sortent de la fenêtre
        Assert.Equal(40, budget.Used);
        Assert.True(budget.CanAfford(10));
        Assert.False(budget.CanAfford(11));

        time.Advance(TimeSpan.FromMinutes(30)); // les 40 autres sortent à leur tour
        Assert.Equal(0, budget.Used);
    }
}
