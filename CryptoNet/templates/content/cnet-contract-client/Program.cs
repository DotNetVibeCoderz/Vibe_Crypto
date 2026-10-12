using CnetContracts;
using Crypto.Net.Evm;
using Crypto.Net.Solana;
using Crypto.Net.Wallet;

// Erc20 was generated from Contracts/Erc20.abi.json; CounterProgram from Contracts/Counter.idl.json.
// Add any ABI (or Hardhat/Foundry artifact) as *.abi.json and any Anchor IDL as *.idl.json, then rebuild.
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await using var rpc = new EvmRpcClient(EvmChain.Sepolia);
var usdc = new Erc20(rpc, "0x1c7D4B196Cb0C7B01d743Fbc6116a902379C7238"); // Circle USDC on Sepolia
Console.WriteLine($"Token   : {await usdc.NameAsync(ct: cts.Token)} ({await usdc.SymbolAsync(ct: cts.Token)}), {await usdc.DecimalsAsync(ct: cts.Token)} decimals");
Console.WriteLine($"Supply  : {await usdc.TotalSupplyAsync(ct: cts.Token)} base units");

// Typed calldata and events, no network needed.
byte[] calldata = Erc20.EncodeTransfer("0x000000000000000000000000000000000000dEaD", 1_000_000);
Console.WriteLine($"Calldata: {Convert.ToHexStringLower(calldata)[..24]}…  topic0 {Erc20.Events.Transfer.Topic0[..18]}…");

// Anchor: Borsh-encoded instruction with the 8-byte discriminator.
using var wallet = HdWallet.Generate();
using var user = wallet.GetSolanaAccount(0, SolanaChain.Devnet);
var (counter, _) = SolanaAddress.FindProgramAddress([SolanaAddress.Utf8Seed("counter"), SolanaAddress.Decode(user.Address.Value)], CounterProgram.ProgramId);
var ix = CounterProgram.Increment(counter, user.Address.Value, 5);
Console.WriteLine($"Anchor  : {ix.Data.Length} bytes of instruction data for program {ix.ProgramId[..8]}…, {ix.Accounts.Count} accounts");
