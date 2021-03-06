﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using System.Numerics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using viafront3.Models;
using viafront3.Models.WalletViewModels;
using viafront3.Services;
using viafront3.Data;
using via_jsonrpc;
using xchwallet;

namespace viafront3.Controllers
{
    [Authorize(Roles = Utils.EmailConfirmedRole)]
    [Route("[controller]/[action]")]
    public class WalletController : BaseWalletController
    {
        private readonly IEmailSender _emailSender;
        private readonly ITripwire _tripwire;
        private readonly IUserLocks _userLocks;

        public WalletController(
          ILogger<WalletController> logger,
          UserManager<ApplicationUser> userManager,
          ApplicationDbContext context,
          IOptions<ExchangeSettings> settings,
          IWalletProvider walletProvider,
          IOptions<KycSettings> kycSettings,
          IEmailSender emailSender,
          ITripwire tripwire,
          IUserLocks userLocks) : base(logger, userManager, context, settings, walletProvider, kycSettings)
        {
            _emailSender = emailSender;
            _tripwire = tripwire;
            _userLocks = userLocks;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var user = await GetUser(required: true);

            //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
            var via = new ViaJsonRpc(_settings.AccessHttpUrl);
            var balances = Utils.GetUsedBalances(_settings, via, user.Exchange);

            var model = new BalanceViewModel
            {
                User = user,
                AssetSettings = _settings.Assets,
                Balances = balances
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> Deposits()
        {
            var user = await GetUser(required: true);
            var model = new BaseViewModel
            {
                User = user
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> Deposit(string asset)
        {
            var user = await GetUser(required: true);

            if (_walletProvider.IsFiat(asset))
                return RedirectToAction("DepositFiat", new {asset=asset});

            var wallet = _walletProvider.GetChain(asset);
            var addrs = wallet.GetAddresses(user.Id);
            WalletAddr addr = null;
            if (addrs.Any())
                addr = addrs.First();
            else
            {
                if (!wallet.HasTag(user.Id))
                {
                    wallet.NewTag(user.Id);
                    wallet.Save();
                }
                addr = wallet.NewAddress(user.Id);
                wallet.Save();
            }

            var model = new DepositViewModel
            {
                User = user,
                Asset = asset,
                DepositAddress = addr.Address,
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> Transactions(string asset, string address, int offset=0, int limit=10)
        {
            var user = await GetUser(required: true);

            // get wallet address
            var wallet = _walletProvider.GetChain(asset);
            var addrs = wallet.GetAddresses(user.Id);
            WalletAddr addr = null;
            if (addrs.Any())
                addr = addrs.First();
            else
                addr = wallet.NewAddress(user.Id);

            var chainAssetSettings = _walletProvider.ChainAssetSettings(asset);

            var incommingTxs = wallet.GetAddrTransactions(addr.Address);
            if (incommingTxs != null)
                incommingTxs = incommingTxs.Where(t => t.Direction == WalletDirection.Incomming).OrderByDescending(t => t.ChainTx.Date);
            else
                incommingTxs = new List<WalletTx>();

            var model = new UserTransactionsViewModel
            {
                User = user,
                Asset = asset,
                DepositAddress = addr.Address,
                ChainAssetSettings = _walletProvider.ChainAssetSettings(asset),
                AssetSettings = _settings.Assets[asset],
                Wallet = wallet,
                TransactionsIncomming = incommingTxs.Skip(offset).Take(limit),
                TxsIncommingOffset = offset,
                TxsIncommingLimit = limit,
                TxsIncommingCount = incommingTxs.Count(),
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> DepositFiat(string asset)
        {
            var user = await GetUser(required: true);

            var model = new DepositFiatViewModel
            {
                User = user,
                Asset = asset,
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DepositFiat(DepositFiatViewModel model)
        {
            var user = await GetUser(required: true);

            var wallet = _walletProvider.GetFiat(model.Asset);
            var amount = wallet.StringToAmount(model.Amount.ToString());
            if (amount <= 0)
            {
                this.FlashError("Amount must be greater then 0");
                return View(model);
            }
            var decimals = _settings.Assets[model.Asset].Decimals;
            if (Utils.GetDecimalPlaces(model.Amount) > decimals)
            {
                this.FlashError($"Amount must have a maximum of {decimals} digits after the decimal place");
                return View(model);
            }
            if (!wallet.HasTag(user.Id))
            {
                wallet.NewTag(user.Id);
                wallet.Save();
            }
            var tx = wallet.RegisterPendingDeposit(user.Id, amount);
            model.PendingTx = tx;
            model.Account = wallet.GetAccount();
            wallet.Save();

            // send email: deposit created
            await _emailSender.SendEmailFiatDepositCreatedAsync(user.Email, model.Asset, wallet.AmountToString(tx.Amount), tx.DepositCode, wallet.GetAccount());

            return View("DepositFiatCreated", model);
        }

        [HttpGet]
        public async Task<IActionResult> FiatTransactionView(string asset, int offset=0, int limit=10)
        {
            var user = await GetUser(required: true);

            // get wallet address
            var wallet = _walletProvider.GetFiat(asset);
            var txs = wallet.GetTransactions(user.Id).OrderByDescending(t => t.Date);
           
            var model = new FiatTransactionsViewModel
            {
                User = user,
                Asset = asset,
                AssetSettings = _settings.Assets,
                Wallet = wallet,
                Transactions = txs.Skip(offset).Take(limit),
                TxsOffset = offset,
                TxsLimit = limit,
                TxsCount = txs.Count(),
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> Withdrawals()
        {
            var user = await GetUser(required: true);
            var model = new BaseViewModel
            {
                User = user
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> Withdraw(string asset)
        {
            var user = await GetUser(required: true);

            if (_walletProvider.IsFiat(asset))
                return RedirectToAction("WithdrawFiat", new {asset=asset});

            //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
            var via = new ViaJsonRpc(_settings.AccessHttpUrl);
            var balance = via.BalanceQuery(user.Exchange.Id, asset);

            var model = new WithdrawViewModel
            {
                User = user,
                AssetSettings = _settings.Assets,
                Asset = asset,
                BalanceAvailable = balance.Available,
                TwoFactorRequired = user.TwoFactorEnabled,
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Withdraw(WithdrawViewModel model)
        {
            var user = await GetUser(required: true);

            //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
            var via = new ViaJsonRpc(_settings.AccessHttpUrl);
            var balance = via.BalanceQuery(user.Exchange.Id, model.Asset);
            // fill in model in case we need to error out early
            model.User = user;
            model.AssetSettings = _settings.Assets;
            model.BalanceAvailable = balance.Available;

            // if tripwire tripped cancel
            if (!_tripwire.WithdrawalsEnabled())
            {
                _logger.LogError("Tripwire tripped, exiting Withdraw()");
                this.FlashError($"Withdrawals not enabled");
                return View(model);
            }
            await _tripwire.RegisterEvent(TripwireEventType.WithdrawalAttempt);

            if (!ModelState.IsValid)
            {
                // redisplay form
                return View(model);
            }

            // check 2fa authentication
            if (user.TwoFactorEnabled)
            {
                if (model.TwoFactorCode == null)
                    model.TwoFactorCode = "";
                var authenticatorCode = model.TwoFactorCode.Replace(" ", string.Empty).Replace("-", string.Empty);
                if (!await _userManager.VerifyTwoFactorTokenAsync(user, _userManager.Options.Tokens.AuthenticatorTokenProvider, authenticatorCode))
                {
                    this.FlashError($"Invalid authenticator code");
                    return View(model);
                }
            }

            // lock process of checking balance and performing withdrawal
            lock (_userLocks.GetLock(user.Id))
            {
                //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
                balance = via.BalanceQuery(user.Exchange.Id, model.Asset);
                model.BalanceAvailable = balance.Available;

                var wallet = _walletProvider.GetChain(model.Asset);

                // validate amount
                var amountInt = wallet.StringToAmount(model.Amount.ToString());
                var availableInt = wallet.StringToAmount(balance.Available);
                if (amountInt > availableInt)
                {
                    this.FlashError("Amount must be less then or equal to available balance");
                    return View(model);
                }
                if (amountInt <= 0)
                {
                    this.FlashError("Amount must be greather then or equal to 0");
                    return View(model);
                }

                // validate address
                if (!wallet.ValidateAddress(model.WithdrawalAddress))
                {
                    this.FlashError("Withdrawal address is not valid");
                    return View(model);
                }

                // validate kyc level
                (var success, var withdrawalAssetAmount, var error) = ValidateWithdrawlLimit(user, model.Asset, model.Amount);
                if (!success)
                {
                    this.FlashError(error);
                    return View(model);
                }

                var consolidatedFundsTag = _walletProvider.ConsolidatedFundsTag();

                using (var dbtx = wallet.BeginDbTransaction())
                {
                    // ensure tag exists
                    if (!wallet.HasTag(consolidatedFundsTag))
                    {
                        wallet.NewTag(consolidatedFundsTag);
                        wallet.Save();
                    }

                    // register withdrawal with wallet
                    var tag = wallet.GetTag(user.Id);
                    if (tag == null)
                        tag = wallet.NewTag(user.Id);
                    var spend = wallet.RegisterPendingSpend(consolidatedFundsTag, consolidatedFundsTag,
                        model.WithdrawalAddress, amountInt, tag);
                    wallet.Save();
                    var businessId = spend.Id;

                    // register withdrawal with the exchange backend
                    var negativeAmount = -model.Amount;
                    try
                    {
                        via.BalanceUpdateQuery(user.Exchange.Id, model.Asset, "withdraw", businessId, negativeAmount.ToString(), null);
                    }
                    catch (ViaJsonException ex)
                    {
                        _logger.LogError(ex, "Failed to update (withdraw) user balance (xch id: {0}, asset: {1}, businessId: {2}, amount {3}",
                            user.Exchange.Id, model.Asset, businessId, negativeAmount);
                        if (ex.Err == ViaError.BALANCE_UPDATE__BALANCE_NOT_ENOUGH)
                        {
                            dbtx.Rollback();
                            this.FlashError("Balance not enough");
                            return View(model);
                        }
                        throw;
                    }

                    dbtx.Commit();
                }

                // register withdrawal with kyc limits
                user.AddWithdrawal(_context, model.Asset, model.Amount, withdrawalAssetAmount);
                _context.SaveChanges();
            }

            // register withdrawal with tripwire
            await _tripwire.RegisterEvent(TripwireEventType.Withdrawal);

            this.FlashSuccess(string.Format("Created withdrawal: {0} {1} to {2}", model.Amount, model.Asset, model.WithdrawalAddress));
            // send email: withdrawal created
            await _emailSender.SendEmailChainWithdrawalCreatedAsync(user.Email, model.Asset, model.Amount.ToString());

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> WithdrawalHistory(string asset, int pendingOffset=0, int pendingLimit=10, int outgoingOffset=0, int outgoingLimit=10)
        {
            var user = await GetUser(required: true);

            var wallet = _walletProvider.GetChain(asset);

            var spends = wallet.PendingSpendsGet(_walletProvider.ConsolidatedFundsTag(), new PendingSpendState[] { PendingSpendState.Pending, PendingSpendState.Error }, user.Id);
            var outgoingTxs = wallet.GetTransactions(_walletProvider.ConsolidatedFundsTag(), user.Id).OrderByDescending(t => t.ChainTx.Date);

            var model = new WithdrawalHistoryViewModel
            {
                User = user,
                Wallet = wallet,
                Asset = asset,
                AssetSettings = _settings.Assets[asset],
                PendingWithdrawals = spends.Skip(pendingOffset).Take(pendingLimit),
                PendingOffset = pendingOffset,
                PendingLimit = pendingLimit,
                PendingCount = spends.Count(),
                OutgoingTransactions = outgoingTxs.Skip(outgoingOffset).Take(outgoingLimit),
                OutgoingOffset = outgoingOffset,
                OutgoingLimit = outgoingLimit,
                OutgoingCount = outgoingTxs.Count(),
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> WithdrawFiat(string asset)
        {
            var user = await GetUser(required: true);

            //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
            var via = new ViaJsonRpc(_settings.AccessHttpUrl);
            var balance = via.BalanceQuery(user.Exchange.Id, asset);

            var model = new WithdrawFiatViewModel
            {
                User = user,
                AssetSettings = _settings.Assets,
                Asset = asset,
                BalanceAvailable = balance.Available,
                TwoFactorRequired = user.TwoFactorEnabled,
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> WithdrawFiat(WithdrawFiatViewModel model)
        {
            var user = await GetUser(required: true);

            //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
            var via = new ViaJsonRpc(_settings.AccessHttpUrl);
            var balance = via.BalanceQuery(user.Exchange.Id, model.Asset);
            // fill in model in case we need to error out early
            model.User = user;
            model.AssetSettings = _settings.Assets;
            model.BalanceAvailable = balance.Available;

            // if tripwire tripped cancel
            if (!_tripwire.WithdrawalsEnabled())
            {
                _logger.LogError("Tripwire tripped, exiting WithdrawFiat()");
                this.FlashError($"Withdrawals not enabled");
                return View(model);
            }
            await _tripwire.RegisterEvent(TripwireEventType.WithdrawalAttempt);

            // check 2fa authentication
            if (user.TwoFactorEnabled)
            {
                if (model.TwoFactorCode == null)
                    model.TwoFactorCode = "";
                var authenticatorCode = model.TwoFactorCode.Replace(" ", string.Empty).Replace("-", string.Empty);
                if (!await _userManager.VerifyTwoFactorTokenAsync(user, _userManager.Options.Tokens.AuthenticatorTokenProvider, authenticatorCode))
                {
                    this.FlashError($"Invalid authenticator code");
                    return View(model);
                }
            }

            var wallet = _walletProvider.GetFiat(model.Asset);
            var amountInt = wallet.StringToAmount(model.Amount.ToString());
            if (amountInt <= 0)
            {
                this.FlashError("Amount must be greater then 0");
                return View(model);
            }
            var decimals = _settings.Assets[model.Asset].Decimals;
            if (Utils.GetDecimalPlaces(model.Amount) > decimals)
            {
                this.FlashError($"Amount must have a maximum of {decimals} digits after the decimal place");
                return View(model);
            }

            if (!Utils.ValidateBankAccount(model.WithdrawalAccount))
            {
                this.FlashError($"Bank account invalid");
                return View(model);
            }

            // lock process of checking balance and performing withdrawal
            lock (_userLocks.GetLock(user.Id))
            {
                //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
                balance = via.BalanceQuery(user.Exchange.Id, model.Asset);
                // validate amount
                var availableInt = wallet.StringToAmount(balance.Available);
                if (amountInt > availableInt)
                {
                    this.FlashError("Amount must be less then or equal to available balance");
                    return View(model);
                }

                // validate kyc level
                (var success, var withdrawalAssetAmount, var error) = ValidateWithdrawlLimit(user, model.Asset, model.Amount);
                if (!success)
                {
                    this.FlashError(error);
                    return View(model);
                }

                // create pending withdrawal
                var account = new BankAccount { AccountNumber = model.WithdrawalAccount };
                model.PendingTx = wallet.RegisterPendingWithdrawal(user.Id, amountInt, account);

                // register new withdrawal with the exchange backend
                var source = new Dictionary<string, object>();
                var amountStr = (-model.Amount).ToString();
                var depositCodeInt = long.Parse(model.PendingTx.DepositCode);
                via.BalanceUpdateQuery(user.Exchange.Id, model.Asset, "withdraw", depositCodeInt, amountStr, source);
                Console.WriteLine($"Updated exchange backend");

                // save wallet (after we have posted the withdrawal to the backend)
                wallet.Save();

                // register withdrawal with kyc limits
                user.AddWithdrawal(_context, model.Asset, model.Amount, withdrawalAssetAmount);
                _context.SaveChanges();
            }

                // register withdrawal with tripwire
                await _tripwire.RegisterEvent(TripwireEventType.Withdrawal);

                // send email: withdrawal created
                await _emailSender.SendEmailFiatWithdrawalCreatedAsync(user.Email, model.Asset, model.Amount.ToString(), model.PendingTx.DepositCode);

            return View("WithdrawalFiatCreated", model);
        }
    }
}
