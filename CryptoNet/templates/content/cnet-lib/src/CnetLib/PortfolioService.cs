using Crypto.Net.Core;
using Crypto.Net.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace CnetLib;

/// <summary>A holding: an address on a registered network and its balance.</summary>
public sealed record Holding(string Network, string Address, Amount Balance);

/// <summary>Reads balances through the clients registered with <c>AddCryptoNet</c>.</summary>
public sealed class PortfolioService(IChainRegistry chains)
{
    public async Task<IReadOnlyList<Holding>> GetHoldingsAsync(IEnumerable<(string Network, string Address)> positions, CancellationToken ct = default)
    {
        var result = new List<Holding>();
        foreach (var (network, address) in positions)
        {
            var registration = chains.Registrations.First(r => r.Name == network);
            var balance = await chains.GetClient(network).GetBalanceAsync(new Address(address, registration.Chain.Id), ct);
            result.Add(new Holding(network, address, balance));
        }
        return result;
    }
}

public static class CnetLibServiceCollectionExtensions
{
    /// <summary>Registers <see cref="PortfolioService"/>; call <c>AddCryptoNet</c> to register the networks it reads.</summary>
    public static IServiceCollection AddCnetLib(this IServiceCollection services) => services.AddSingleton<PortfolioService>();
}
