using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Common;
using BTCPayServer.Configuration;
using BTCPayServer.Security;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NBitcoin.Payment;
using NBitcoin.Secp256k1;
using Newtonsoft.Json;
using NNostr.Client;
using WalletWasabi.Backend.Controllers;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Blockchain.TransactionOutputs;

namespace BTCPayServer.Plugins.Wabisabi
{
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Route("plugins/{storeId}/Wabisabi")]
    public partial class WabisabiStoreController : Controller
    {
        private readonly WabisabiService _WabisabiService;
        private readonly WalletProvider _walletProvider;
        private readonly IBTCPayServerClientFactory _btcPayServerClientFactory;
        private readonly IExplorerClientProvider _explorerClientProvider;
        private readonly WabisabiCoordinatorService _wabisabiCoordinatorService;
        private readonly IAuthorizationService _authorizationService;
        private readonly WabisabiCoordinatorClientInstanceManager _instanceManager;
        private readonly Socks5HttpClientHandler _socks5HttpClientHandler;
        public WabisabiStoreController(WabisabiService WabisabiService, WalletProvider walletProvider,
            IBTCPayServerClientFactory btcPayServerClientFactory,
            IExplorerClientProvider explorerClientProvider,
            WabisabiCoordinatorService wabisabiCoordinatorService,
            WabisabiCoordinatorClientInstanceManager instanceManager, 
            IAuthorizationService authorizationService,
            IServiceProvider serviceProvider)
        {
            _WabisabiService = WabisabiService;
            _walletProvider = walletProvider;
            _btcPayServerClientFactory = btcPayServerClientFactory;
            _explorerClientProvider = explorerClientProvider;
            _wabisabiCoordinatorService = wabisabiCoordinatorService;
            _authorizationService = authorizationService;
            _instanceManager = instanceManager;
            _socks5HttpClientHandler = serviceProvider.GetRequiredService<Socks5HttpClientHandler>();
        }

        [HttpGet("")]
        public async Task<IActionResult> UpdateWabisabiStoreSettings(string storeId)
        {
            WabisabiStoreSettings Wabisabi = null;
            try
            {
                Wabisabi = await _WabisabiService.GetWabisabiForStore(storeId);
            }
            catch (Exception)
            {
                // ignored
            }

            return View(Wabisabi);
        }


        [HttpPost("")]
        public async Task<IActionResult> UpdateWabisabiStoreSettings(string storeId, WabisabiStoreSettings vm,
            string command)
        {
            var pieces = command.Split(":");
            var actualCommand = pieces[0];
            var commandIndex = pieces.Length > 1 ? pieces[1] : null;
            vm.AnonymitySetTarget = Math.Max(0, vm.AnonymitySetTarget);
            vm.FeeRateMedianTimeFrameHours = Math.Max(0, vm.FeeRateMedianTimeFrameHours);
            ModelState.Clear();

            WabisabiCoordinatorSettings coordSettings;
            switch (actualCommand)
            {
                case "accept-terms":
                    
                    var coord = vm.Settings.SingleOrDefault(settings => settings.Coordinator == commandIndex);
                    coord.RoundWhenEnabled = null;
                    
                    await _WabisabiService.SetWabisabiForStore(storeId, vm, commandIndex);
                    TempData["SuccessMessage"] = $"{commandIndex} terms accepted";
                    return RedirectToAction(nameof(UpdateWabisabiStoreSettings), new {storeId});
                case "remove-coordinator":
                    if (!(await _authorizationService.AuthorizeAsync(User, null,
                            new PolicyRequirement(Policies.CanModifyServerSettings))).Succeeded)
                    { return View(vm);
                    }
                    coordSettings = await _wabisabiCoordinatorService.GetSettings();
                    if (coordSettings.DiscoveredCoordinators.RemoveAll(discoveredCoordinator =>
                            discoveredCoordinator.Name == commandIndex) > 0)
                    {
                        TempData["SuccessMessage"] = $"Coordinator {commandIndex} stopped and removed";
                        await _wabisabiCoordinatorService.UpdateSettings(coordSettings);
                        await _instanceManager.RemoveCoordinator(commandIndex);
                        return RedirectToAction(nameof(UpdateWabisabiStoreSettings), new {storeId});
                    }
                    else
                    {
                        TempData["ErrorMessage"] =
                            $"Coordinator {commandIndex} could not be removed because it was not found";
                    }

                    return View(vm);
                case "check":
                    await _walletProvider.Check(storeId, CancellationToken.None);
                    TempData["SuccessMessage"] = "Store wallet re-checked";
                    return RedirectToAction(nameof(UpdateWabisabiStoreSettings), new {storeId});
                case "exclude-label-add":
                    vm.InputLabelsExcluded.Add("");
                    return View(vm);

                case "exclude-label-remove":
                    vm.InputLabelsExcluded.Remove(commandIndex);
                    return View(vm);
                case "include-label-add":
                    vm.InputLabelsAllowed.Add("");
                    return View(vm);
                case "include-label-remove":
                    vm.InputLabelsAllowed.Remove(commandIndex);
                    return View(vm);

                case "save":
                    vm.InputLabelsAllowed = vm.InputLabelsAllowed.Where(s => !string.IsNullOrEmpty(s)).Distinct()
                        .ToList();
                    vm.InputLabelsExcluded = vm.InputLabelsExcluded.Where(s => !string.IsNullOrEmpty(s)).Distinct()
                        .ToList();
                    await _WabisabiService.SetWabisabiForStore(storeId, vm);
                    TempData["SuccessMessage"] = "Wabisabi settings modified";
                    return RedirectToAction(nameof(UpdateWabisabiStoreSettings), new {storeId});

                default:
                    return View(vm);
            }
        }

        [HttpPost("add-coordinator")]
        [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyServerSettings)]
        public async Task<IActionResult> AddCoordinator(string storeId, [FromForm] DiscoveredCoordinator viewModel, string command )
        {
            
            var coordSettings = await _wabisabiCoordinatorService.GetSettings();
            if (command == "discover")
            {
                
                var network = _explorerClientProvider.GetExplorerClient("BTC").Network.NBitcoinNetwork;
                    
                if (Uri.TryCreate(viewModel.Relay, UriKind.Absolute, out var relayUri))
                {

                    if (network.ChainName == ChainName.Regtest)
                    {
                        var eventsTomake = Random.Shared.Next(1, 5);
                        var evts = new List<NostrEvent>();
                        for (var i = 0; i < eventsTomake; i++)
                        {
                            ECPrivKey.TryCreate(new ReadOnlySpan<byte>(RandomNumberGenerator.GetBytes(32)),
                                out var key);
                            evts.Add(await Nostr.CreateCoordinatorDiscoveryEvent(network, key.ToHex(),
                                new Uri($"https://{Guid.NewGuid()}.com"), "fake regtest coord test"));
                        }

                        await Nostr.Publish(relayUri, evts.ToArray(),null,CancellationToken.None);
                    }

                    try
                    {
                        var result = await Nostr.Discover(
                            _socks5HttpClientHandler, relayUri,
                            network,
                            coordSettings.Key?.CreateXOnlyPubKey().ToHex(), CancellationToken.None);
                        if(result.Any())
                            TempData["DiscoveredCoordinators"] = JsonConvert.SerializeObject(result);
                        else
                            TempData["ErrorMessage"] = $"No coordinators found.";
                    }
                    catch (Exception e)
                    {
                        
                        TempData["ErrorMessage"] = $"Could not discover coordinators: {e.Message}";
                    }
                   
                }
                else
                {
                    TempData["ErrorMessage"] = $"No relay uri was provided";
                }
                
                return RedirectToAction(nameof(UpdateWabisabiStoreSettings), new {storeId});
            }
            
           
            if (viewModel.Name is not null && 
                viewModel.Uri is not null  && 
                !new []{"local", "zksnacks"}.Contains(viewModel.Name.ToLowerInvariant()) &&
                coordSettings.DiscoveredCoordinators.All(discoveredCoordinator =>
                    discoveredCoordinator.Name != viewModel.Name  && discoveredCoordinator.Uri != viewModel.Uri))
            {
                coordSettings.DiscoveredCoordinators.Add(viewModel);
                await _wabisabiCoordinatorService.UpdateSettings(coordSettings);
                _instanceManager.AddCoordinator(viewModel.Name, viewModel.Name, provider => viewModel.Uri, viewModel.Description);

                TempData["SuccessMessage"] = $"Coordinator {viewModel.Name } added and started";
            }

            else
            {
                TempData["ErrorMessage"] =
                    $"Coordinator {viewModel.Name} could not be added because the name/url was not unique";
            }
            return RedirectToAction(nameof(UpdateWabisabiStoreSettings), new {storeId});
        }
        
        [Route("coinjoins")]
        public async Task<IActionResult> ListCoinjoins(string storeId, CoinjoinsViewModel viewModel,
            [FromServices] WalletRepository walletRepository)
        {
            var objects = await _WabisabiService.GetCoinjoinHistory(storeId);

            viewModel ??= new CoinjoinsViewModel();
            viewModel.Coinjoins = objects
                .Skip(viewModel.Skip)
                .Take(viewModel.Count).ToList();
            viewModel.Total = objects.Count();
            return View(viewModel);
        }

        [HttpGet("spend")]
        public async Task<IActionResult> Spend(string storeId)
        {
            if ((await _walletProvider.GetWalletAsync(storeId)) is not BTCPayWallet wallet)
            {
                return NotFound();
            }

            return View(new SpendViewModel() { });
        }

        [HttpPost("spend")]
        public async Task<IActionResult> Spend(string storeId, SpendViewModel spendViewModel, string command)
        {
            if ((await _walletProvider.GetWalletAsync(storeId)) is not BTCPayWallet wallet)
            {
                return NotFound();
            }

            var n = _explorerClientProvider.GetExplorerClient("BTC").Network.NBitcoinNetwork;
            if (string.IsNullOrEmpty(spendViewModel.Destination))
            {
                ModelState.AddModelError(nameof(spendViewModel.Destination),
                    "A destination is required");
            }
            else
            {
                try
                {
                    BitcoinAddress.Create(spendViewModel.Destination, n);
                }
                catch (Exception e)
                {
                    try
                    {
                        new BitcoinUrlBuilder(spendViewModel.Destination, n);
                    }
                    catch (Exception exception)
                    {
                        ModelState.AddModelError(nameof(spendViewModel.Destination),
                            "A destination must be a bitcoin address or a bip21 payment link");
                    }
                }
            }

            if (spendViewModel.Amount is null)
            {
                try
                {
                    spendViewModel.Amount =
                        new BitcoinUrlBuilder(spendViewModel.Destination, n).Amount.ToDecimal(MoneyUnit.BTC);
                }
                catch (Exception e)
                {
                    ModelState.AddModelError(nameof(spendViewModel.Amount),
                        "An amount was not specified and the destination did not have an amount specified");
                }
            }

            if (!ModelState.IsValid)
            {
                return View();
            }

            if (command == "payout")
            {
                var client = await _btcPayServerClientFactory.Create(null, storeId);
                await client.CreatePayout(storeId,
                    new CreatePayoutThroughStoreRequest()
                    {
                        Approved = true, Amount = spendViewModel.Amount, Destination = spendViewModel.Destination
                    });

                TempData["SuccessMessage"] =
                    "The payment has been scheduled. If payment batching is enabled in the coinjoin settings, and the coordinator supports sending that amount and that address type, it will be batched.";

                return RedirectToAction("UpdateWabisabiStoreSettings", new {storeId});
            }

            var coins = await wallet.GetAllCoins();
            if (command == "compute-with-selection")
            {
                if (spendViewModel.SelectedCoins?.Any() is true)
                {
                    coins = (CoinsView) coins.FilterBy(coin =>
                        spendViewModel.SelectedCoins.Contains(coin.Outpoint.ToString()));
                }
            }

            if (command == "compute-with-selection" || command == "compute")
            {
                if (spendViewModel.Amount is null)
                {
                    spendViewModel.Amount =
                        new BitcoinUrlBuilder(spendViewModel.Destination, n).Amount.ToDecimal(MoneyUnit.BTC);
                }

                var defaultCoinSelector = new DefaultCoinSelector();
                var defaultSelection =
                    (defaultCoinSelector.Select(coins.Select(coin => coin.Coin).ToArray(),
                        new Money((decimal) spendViewModel.Amount, MoneyUnit.BTC)) ?? Array.Empty<ICoin>())
                    .ToArray();
                var selector = new SmartCoinSelector(coins.ToList());
                var smartSelection = selector.Select(defaultSelection,
                    new Money((decimal) spendViewModel.Amount, MoneyUnit.BTC));
                spendViewModel.SelectedCoins = smartSelection.Select(coin => coin.Outpoint.ToString()).ToArray();
                return View(spendViewModel);
            }

            if (command == "send")
            {
                var userid = HttpContext.User.Claims.Single(claim => claim.Type == ClaimTypes.NameIdentifier).Value;
                var client = await _btcPayServerClientFactory.Create(userid, storeId);
                var tx = await client.CreateOnChainTransaction(storeId, "BTC",
                    new CreateOnChainTransactionRequest()
                    {
                        SelectedInputs = spendViewModel.SelectedCoins?.Select(OutPoint.Parse).ToList(),
                        Destinations =
                            new List<CreateOnChainTransactionRequest.CreateOnChainTransactionRequestDestination>()
                            {
                                new CreateOnChainTransactionRequest.CreateOnChainTransactionRequestDestination()
                                {
                                    Destination = spendViewModel.Destination, Amount = spendViewModel.Amount
                                }
                            }
                    });

                TempData["SuccessMessage"] =
                    $"The tx {tx.TransactionHash} has been broadcast.";

                return RedirectToAction("UpdateWabisabiStoreSettings", new {storeId});
            }

            return View(spendViewModel);
        }

        [HttpGet("select-coins")]
        public async Task<IActionResult> ComputeCoinSelection(string storeId, decimal amount)
        {
            if ((await _walletProvider.GetWalletAsync(storeId)) is not BTCPayWallet wallet)
            {
                return NotFound();
            }

            
            var coins = await wallet.GetAllCoins();
            var defaultCoinSelector = new DefaultCoinSelector();
            var defaultSelection =
                (defaultCoinSelector.Select(coins.Select(coin => coin.Coin).ToArray(),
                    new Money(amount, MoneyUnit.BTC)) ?? Array.Empty<ICoin>())
                .ToArray();
            var selector = new SmartCoinSelector(coins.ToList());
            var smartSelection = selector.Select(defaultSelection,
                new Money((decimal) amount, MoneyUnit.BTC));
            return Ok(smartSelection.Select(coin => coin.Outpoint.ToString()).ToArray());
        }
        
        public class SpendViewModel
        {
            public string Destination { get; set; }
            public decimal? Amount { get; set; }
            public string[] SelectedCoins { get; set; }
        }
    }
}