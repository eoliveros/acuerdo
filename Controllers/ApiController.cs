﻿using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Internal;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using viafront3.Models;
using viafront3.Models.ApiViewModels;
using viafront3.Data;
using viafront3.Services;
using via_jsonrpc;

namespace viafront3.Controllers
{
    [Produces("application/json")]
    [Route("api/v1/[action]")]
    [ApiController]
    [IgnoreAntiforgeryToken]
    public class ApiController : BaseWalletController
    {
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly IEmailSender _emailSender;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly ApiSettings _apiSettings;
        private readonly ITripwire _tripwire;
        private readonly IUserLocks _userLocks;
        private readonly FiatPaymentProcessorSettings _fiatPaymentSettings;

        public ApiController(
            ILogger<ApiController> logger,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            ApplicationDbContext context,
            IEmailSender emailSender,
            IOptions<ExchangeSettings> settings,
            IOptions<ApiSettings> apiSettings,
            RoleManager<IdentityRole> roleManager,
            IOptions<KycSettings> kycSettings,
            IWalletProvider walletProvider,
            ITripwire tripwire,
            IUserLocks userLocks,
            IOptions<FiatPaymentProcessorSettings> fiatPaymentSettings) : base(logger, userManager, context, settings, walletProvider, kycSettings)
        {
            _signInManager = signInManager;
            _emailSender = emailSender;
            _roleManager = roleManager;
            _apiSettings = apiSettings.Value;
            _tripwire = tripwire;
            _userLocks = userLocks;
            _fiatPaymentSettings = fiatPaymentSettings.Value;
        }

        [HttpPost]
        public async Task<ActionResult<ApiToken>> AccountCreate([FromBody] ApiAccountCreate req) 
        {
            var existingUser = await _userManager.FindByEmailAsync(req.Email);
            if (existingUser != null)
                return new ApiToken { Token = Utils.CreateToken() }; // reply with fake token if user already exists (so robot cant test for user emails)
            var date = DateTimeOffset.Now.ToUnixTimeSeconds();
            var token = Utils.CreateToken();
            var secret = Utils.CreateToken(32);
            var accountReq = new AccountCreationRequest { ApplicationUserId = null, Date = date, Token = token, Secret = secret, Completed = false,
                RequestedEmail = req.Email, RequestedDeviceName = req.DeviceName };
            _context.AccountCreationRequests.Add(accountReq);
            var callbackUrl = Url.AccountCreationConfirmationLink(token, Request.Scheme);
            await _emailSender.SendEmailApiAccountCreationRequest(req.Email, _apiSettings.CreationExpiryMinutes, callbackUrl);
            _context.SaveChanges();
            return new ApiToken { Token = token };
        }

        [HttpPost]
        public async Task<ActionResult<Models.ApiViewModels.ApiKey>> AccountCreateStatus([FromBody] ApiToken token) 
        {
            var accountReq = _context.AccountCreationRequests.SingleOrDefault(r => r.Token == token.Token);
            if (accountReq == null)
                return new Models.ApiViewModels.ApiKey { Completed = false }; // fake reply if token not found (so robot cant test for user emails)
            if (!accountReq.Completed)
                return new Models.ApiViewModels.ApiKey { Completed = false };
            var user = await _userManager.FindByEmailAsync(accountReq.RequestedEmail);
            if (user == null)
                return new Models.ApiViewModels.ApiKey { Completed = false };
            // check expiry
            if (accountReq.Date + (_apiSettings.CreationExpiryMinutes * 60) + 60 /* grace time */ < DateTimeOffset.Now.ToUnixTimeSeconds())
                return BadRequest("expired");
            // create new apikey
            var key = Utils.CreateToken();
            var secret = Utils.CreateToken(32);
            var apikey = new Models.ApiKey
            { 
                ApplicationUserId = accountReq.ApplicationUserId,
                CreationRequestId = accountReq.Id,
                Name = accountReq.RequestedDeviceName,
                Key = key,
                Secret = secret,
                Nonce = 0
            };
            _context.ApiKeys.Add(apikey);
            // save db and return connection details
            _context.SaveChanges();
            return new Models.ApiViewModels.ApiKey { Completed = true, Key = apikey.Key, Secret = apikey.Secret };
        }

        [HttpPost]
        public IActionResult AccountCreateCancel([FromBody] ApiToken token) 
        {
            var accountReq = _context.AccountCreationRequests.SingleOrDefault(r => r.Token == token.Token);
            if (accountReq == null)
                return Ok(); // fake reply if token not found (so robot cant test for user emails)
            _context.AccountCreationRequests.Remove(accountReq);
            _context.SaveChanges();
            return Ok();
        }

        [HttpPost]
        public async Task<ActionResult<ApiToken>> ApiKeyCreate([FromBody] ApiKeyCreate req) 
        {
            var user = await _userManager.FindByEmailAsync(req.Email);
            if (user == null)
                return new ApiToken { Token = Utils.CreateToken() }; // reply with fake token if user already exists (so robot cant test for user emails)
            var date = DateTimeOffset.Now.ToUnixTimeSeconds();
            var token = Utils.CreateToken();
            var secret = Utils.CreateToken(32);
            var accountReq = new ApiKeyCreationRequest { ApplicationUserId = user.Id, Date = date, Token = token, Secret = secret, Completed = false,
                RequestedDeviceName = req.DeviceName };
            _context.ApiKeyCreationRequests.Add(accountReq);
            var callbackUrl = Url.ApiKeyCreationConfirmationLink(token, Request.Scheme);
            await _emailSender.SendEmailApiKeyCreationRequest(req.Email, _apiSettings.CreationExpiryMinutes, callbackUrl);
            _context.SaveChanges();
            return new ApiToken { Token = token };       
        }

        [HttpPost]
        public async Task<ActionResult<Models.ApiViewModels.ApiKey>> ApiKeyCreateStatus([FromBody] ApiToken token) 
        {
            var apiKeyReq = _context.ApiKeyCreationRequests.SingleOrDefault(r => r.Token == token.Token);
            if (apiKeyReq == null)
                return NotFound();
            if (!apiKeyReq.Completed)
                return new Models.ApiViewModels.ApiKey { Completed = false };
            var apikey = _context.ApiKeys.SingleOrDefault(d => d.CreationRequestId == apiKeyReq.Id);
            if (apikey != null)
                return new Models.ApiViewModels.ApiKey { Completed = true };
            // check expiry
            if (apiKeyReq.Date + (_apiSettings.CreationExpiryMinutes * 60) + 60 /* grace time */ < DateTimeOffset.Now.ToUnixTimeSeconds())
                return BadRequest("expired");
            // get user
            var user = await _userManager.FindByIdAsync(apiKeyReq.ApplicationUserId);
            if (user == null)
                return BadRequest();
            // create new apikey
            apikey = Utils.CreateApiKey(user, apiKeyReq.Id, apiKeyReq.RequestedDeviceName);
            _context.ApiKeys.Add(apikey);
            // save db and return connection details
            _context.SaveChanges();
            return new Models.ApiViewModels.ApiKey { Completed = true, Key = apikey.Key, Secret = apikey.Secret };      
        }

        [HttpPost]
        public IActionResult ApiKeyCreateCancel([FromBody] ApiToken token) 
        {
            var apiKeyReq = _context.ApiKeyCreationRequests.SingleOrDefault(r => r.Token == token.Token);
            if (apiKeyReq == null)
                return NotFound(); // TODO: leaks account existence
            _context.ApiKeyCreationRequests.Remove(apiKeyReq);
            _context.SaveChanges();
            return Ok();
        }

        bool CompareDigest(string sig1Base64, string sig2Base64)
        {
            var sig1 = Convert.FromBase64String(sig1Base64);
            var sig2 = Convert.FromBase64String(sig2Base64);
            if (sig1.Length != sig2.Length)
                return false;
            bool ret = true;
            for (int i = 0; i < sig1.Length; i++)
                ret = ret & (sig1[i] == sig2[i]);
            return ret;
        }

        Models.ApiKey AuthKey(string key, long nonce, out string error)
        {
            error = "";
            // get auth header
            var headerValue = Request.Headers["X-Signature"];
            if (!headerValue.Any())
            {
                error = "authentication failed";
                return null;
            }
            var signature = headerValue.ToString();
            // read raw body text
            Request.EnableRewind();
            Request.Body.Seek(0, System.IO.SeekOrigin.Begin);
            using (var stream = new System.IO.StreamReader(HttpContext.Request.Body))
            {
                string requestBody = stream.ReadToEnd();
                // find apikey that matches key
                var apikey = _context.ApiKeys.SingleOrDefault(a => a.Key == key);
                if (apikey == null)
                {
                    error = "authentication failed";
                    return null;
                }
                // check signature
                var ourSig = HMacWithSha256(apikey.Secret, requestBody);
                if (!CompareDigest(ourSig, signature))
                {
                    error = "authentication failed";
                    return null;
                }
                // check nonce
                if (nonce <= apikey.Nonce)
                {
                    error = "old nonce";
                    return null;
                }
                // update nonce in db
                apikey.Nonce = nonce;
                _context.ApiKeys.Update(apikey);
                _context.SaveChanges();
                return apikey;
            }
        }

        [HttpPost]
        public IActionResult ApiKeyDestroy([FromBody] ApiAuth req) 
        {
            string error;
            var apikey = AuthKey(req.Key, req.Nonce, out error);
            if (apikey == null)
                return BadRequest(error);
            _context.ApiKeys.Remove(apikey);
            _context.SaveChanges();
            return Ok();          
        }

        [HttpPost]
        public IActionResult ApiKeyValidate([FromBody] ApiAuth req) 
        {
            string error;
            var apikey = AuthKey(req.Key, req.Nonce, out error);
            if (apikey == null)
                return BadRequest(error);
            return Ok();
        }

        [HttpPost]
        public ActionResult<ApiAccountBalance> AccountBalance([FromBody] ApiAuth req) 
        {
            string error;
            var apikey = AuthKey(req.Key, req.Nonce, out error);
            if (apikey == null)
                return BadRequest(error);
            var xch = _context.Exchange.SingleOrDefault(x => x.ApplicationUserId == apikey.ApplicationUserId);
            if (xch == null)
                return BadRequest(); 
            try
            {
                //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
                var via = new ViaJsonRpc(_settings.AccessHttpUrl);
                var balances = via.BalanceQuery(xch.Id);
                var model = new ApiAccountBalance { Assets = balances };
                return model;
            }
            catch (ViaJsonException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost]
        public async Task<ActionResult<ApiAccountKyc>> AccountKyc([FromBody] ApiAuth req) 
        {
            string error;
            var apikey = AuthKey(req.Key, req.Nonce, out error);
            if (apikey == null)
                return BadRequest(error);
            var user = await _userManager.FindByIdAsync(apikey.ApplicationUserId);
            if (user == null)
                return BadRequest();
            var level = user.Kyc != null ? user.Kyc.Level : 0;
            KycLevel kycLevel = null;
            if (level < _kycSettings.Levels.Count())
                kycLevel = _kycSettings.Levels[level];
            else
                return BadRequest();
            var model = new ApiAccountKyc
            {
                Level = level.ToString(),
                WithdrawalLimit = kycLevel.WithdrawalLimit,
                WithdrawalAsset = _kycSettings.WithdrawalAsset,
                WithdrawalPeriod = _kycSettings.WithdrawalPeriod.ToString(),
                WithdrawalTotal = user.WithdrawalTotalThisPeriod(_kycSettings).ToString(),

            };
            return model;
        }

        [HttpPost]
        public ActionResult<ApiAccountKycRequest> AccountKycUpgrade([FromBody] ApiAuth req) 
        {
            var apikey = AuthKey(req.Key, req.Nonce, out string error);
            if (apikey == null)
                return BadRequest(error);
            // call kyc server to create request
            var model = CreateKycRequest(_kycSettings, apikey.ApplicationUserId);
            if (model != null)
                return model;
            return BadRequest();
        }

        [HttpPost]
        public async Task<ActionResult<ApiAccountKycRequest>> AccountKycUpgradeStatus([FromBody] ApiAccountKycRequestStatus req) 
        {
            var apikey = AuthKey(req.Key, req.Nonce, out string error);
            if (apikey == null)
                return BadRequest(error);
            // call kyc server to check request
            var model = await CheckKycRequest(_kycSettings, apikey.ApplicationUserId, req.Token);
            if (model != null)
                return model;
            return BadRequest();
        }

        [HttpGet]
        [HttpPost]
        public ActionResult<ApiMarketList> MarketList() 
        {
            try
            {
                //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
                var via = new ViaJsonRpc(_settings.AccessHttpUrl);
                var markets = via.MarketListQuery();
                var model = new ApiMarketList { Markets = new List<string>() };
                foreach (var market in markets)
                    model.Markets.Add(market.name);
                return model;
            }
            catch (ViaJsonException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost]
        public ActionResult<ApiMarketStatus> MarketStatus([FromBody] ApiMarketPeriod market) 
        {
            try
            {
                //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
                var via = new ViaJsonRpc(_settings.AccessHttpUrl);
                var marketStatus = via.MarketStatusQuery(market.Market, market.Period ?? 86400);
                var model = new ApiMarketStatus
                {
                    Period = marketStatus.period,
                    Open = marketStatus.open,
                    Close = marketStatus.close,
                    High = marketStatus.high,
                    Low = marketStatus.low,
                    Volume = marketStatus.volume,
                };
                return model;
            }
            catch (ViaJsonException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost]
        public ActionResult<ApiMarketDetail> MarketDetail([FromBody] ApiMarket market) 
        {
            if (!_settings.Markets.ContainsKey(market.Market))
                return BadRequest("invalid request");
            var marketSettings = _settings.Markets[market.Market];
            var model = new ApiMarketDetail
            {
                TakerFeeRate = _settings.TakerFeeRate,
                MakerFeeRate = _settings.MakerFeeRate,
                MinAmount = marketSettings.AmountInterval,
                TradeAsset = marketSettings.AmountUnit,
                PriceAsset = marketSettings.PriceUnit,
                TradeDecimals = marketSettings.AmountDecimals,
                PriceDecimals = marketSettings.PriceDecimals,
            };
            return model;
        }

        [HttpPost]
        public ActionResult<ApiMarketDepthResponse> MarketDepth([FromBody] ApiMarketDepth market) 
        {
            try
            {
                //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
                var via = new ViaJsonRpc(_settings.AccessHttpUrl);
                var orderDepth = via.OrderDepthQuery(market.Market, market.Limit ?? 20, market.Merge);
                var model = new ApiMarketDepthResponse
                {
                    Asks = orderDepth.asks,
                    Bids = orderDepth.bids,
                };
                return model;
            }
            catch (ViaJsonException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost]
        public ActionResult<ApiMarketHistoryResponse> MarketHistory([FromBody] ApiMarketHistory market) 
        {
            try
            {
                //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
                var via = new ViaJsonRpc(_settings.AccessHttpUrl);
                var history = via.MarketHistoryQuery(market.Market, market.Limit ?? 100, 0);
                var model = new ApiMarketHistoryResponse { Trades = new List<ApiMarketTrade>() };
                foreach (var trade in history)
                {
                    model.Trades.Add(new ApiMarketTrade {
                        Date = (int)trade.time,
                        Price = trade.price,
                        Amount = trade.amount,
                        Type = trade.type,
                    });
                };
                return model;
            }
            catch (ViaJsonException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        ApiOrder FormatOrder(Order order)
        {
            var status = "not executed";
            var dealStock = decimal.Parse(order.deal_stock, System.Globalization.NumberStyles.Any);
            var amount = decimal.Parse(order.amount, System.Globalization.NumberStyles.Any);
            if (dealStock > 0)
                status = "partially executed";
            if (dealStock == amount)
                status = "executed";
            var model = new ApiOrder
            {
                Id = order.id,
                Market = order.market,
                Type = order.type == OrderType.Limit ? "limit" : "market",
                Side = order.side == OrderSide.Bid ? "buy" : "sell",
                Amount = order.amount,
                Price = order.price,
                Status = status,
                DateCreated = (int)order.ctime,
                DateModified = (int)order.mtime,
                AmountTraded = order.deal_stock,
                ExecutedValue = order.deal_money,
                FeePaid = order.deal_fee,
                MakerFeeRate = order.maker_fee,
                TakerFeeRate = order.taker_fee,
            };
            return model;        
        }

        [HttpPost]
        public ActionResult<ApiOrder> OrderLimit([FromBody] ApiOrderCreateLimit req) 
        {
            (var success, var error) = Utils.ValidateOrderParams(_settings, req, req.Price);
            if (!success)
                return BadRequest(error);

            var apikey = AuthKey(req.Key, req.Nonce, out error);
            if (apikey == null)
                return BadRequest(error);
            var xch = _context.Exchange.SingleOrDefault(x => x.ApplicationUserId == apikey.ApplicationUserId);
            if (xch == null)
                return BadRequest(); 
            try
            {
                // lock process of performing trade
                lock (_userLocks.GetLock(apikey.ApplicationUserId))
                {
                    //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
                    var via = new ViaJsonRpc(_settings.AccessHttpUrl);
                    (var side, var error2) = Utils.GetOrderSide(req.Side);
                    if (error2 != null)
                        return BadRequest(error2);
                    var order = via.OrderLimitQuery(xch.Id, req.Market, side, req.Amount, req.Price, _settings.TakerFeeRate, _settings.MakerFeeRate, "viafront api");
                    return FormatOrder(order);
                }
            }
            catch (ViaJsonException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost]
        public ActionResult<ApiOrder> OrderMarket([FromBody] ApiOrderCreateMarket req) 
        {
            (var success, var error) = Utils.ValidateOrderParams(_settings, req, null, marketOrder: true);
            if (!success)
                return BadRequest(error);

            var apikey = AuthKey(req.Key, req.Nonce, out error);
            if (apikey == null)
                return BadRequest(error);
            var xch = _context.Exchange.SingleOrDefault(x => x.ApplicationUserId == apikey.ApplicationUserId);
            if (xch == null)
                return BadRequest(); 
            try
            {
                // lock process of performing trade
                lock (_userLocks.GetLock(apikey.ApplicationUserId))
                {
                    //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
                    var via = new ViaJsonRpc(_settings.AccessHttpUrl);
                    (var side, var error2) = Utils.GetOrderSide(req.Side);
                    if (error2 != null)
                        return BadRequest(error2);
                    Order order;
                    if (_settings.MarketOrderBidAmountMoney)
                        order = via.OrderMarketQuery(xch.Id, req.Market, side, req.Amount, _settings.TakerFeeRate, "viafront api", _settings.MarketOrderBidAmountMoney);
                    else
                        order = via.OrderMarketQuery(xch.Id, req.Market, side, req.Amount, _settings.TakerFeeRate, "viafront api");
                    return FormatOrder(order);
                }
            }
            catch (ViaJsonException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost]
        public ActionResult<ApiOrdersResponse> OrdersPending([FromBody] ApiOrders req) 
        {
            string error;
            var apikey = AuthKey(req.Key, req.Nonce, out error);
            if (apikey == null)
                return BadRequest(error);
            var xch = _context.Exchange.SingleOrDefault(x => x.ApplicationUserId == apikey.ApplicationUserId);
            if (xch == null)
                return BadRequest(); 
            try
            {
                //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
                var via = new ViaJsonRpc(_settings.AccessHttpUrl);
                var ordersPending = via.OrdersPendingQuery(xch.Id, req.Market, req.Offset, req.Limit);
                var model = new ApiOrdersResponse { Offset = ordersPending.offset, Limit = ordersPending.limit, Orders = new List<ApiOrder>() };
                foreach (var order in ordersPending.records)
                    model.Orders.Add(FormatOrder(order));
                return model;
            }
            catch (ViaJsonException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost]
        public ActionResult<ApiOrdersResponse> OrdersExecuted([FromBody] ApiOrders req) 
        {
            string error;
            var apikey = AuthKey(req.Key, req.Nonce, out error);
            if (apikey == null)
                return BadRequest(error);
            var xch = _context.Exchange.SingleOrDefault(x => x.ApplicationUserId == apikey.ApplicationUserId);
            if (xch == null)
                return BadRequest(); 
            try
            {
                //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
                var via = new ViaJsonRpc(_settings.AccessHttpUrl);
                var ordersCompleted = via.OrdersCompletedQuery(xch.Id, req.Market, 0, 0, req.Offset, req.Limit, OrderSide.Any);
                var model = new ApiOrdersResponse { Offset = ordersCompleted.offset, Limit = ordersCompleted.limit, Orders = new List<ApiOrder>() };
                foreach (var order in ordersCompleted.records)
                    model.Orders.Add(FormatOrder(order));
                return model;
            }
            catch (ViaJsonException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost]
        public ActionResult<ApiOrder> OrderPendingStatus([FromBody] ApiOrderPendingStatus req) 
        {
            string error;
            var apikey = AuthKey(req.Key, req.Nonce, out error);
            if (apikey == null)
                return BadRequest(error);
            var xch = _context.Exchange.SingleOrDefault(x => x.ApplicationUserId == apikey.ApplicationUserId);
            if (xch == null)
                return BadRequest(); 
            try
            {
                //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
                var via = new ViaJsonRpc(_settings.AccessHttpUrl);
                var order = via.OrderPendingDetails(req.Market, req.Id);
                if (order == null)
                    return BadRequest("invalid parameter");
                if (order.user != xch.Id)
                    return BadRequest("invalid parameter");
                return FormatOrder(order);
            }
            catch (ViaJsonException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost]
        public ActionResult<ApiOrder> OrderExecutedStatus([FromBody] ApiOrderExecutedStatus req) 
        {
            string error;
            var apikey = AuthKey(req.Key, req.Nonce, out error);
            if (apikey == null)
                return BadRequest(error);
            var xch = _context.Exchange.SingleOrDefault(x => x.ApplicationUserId == apikey.ApplicationUserId);
            if (xch == null)
                return BadRequest(); 
            try
            {
                //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
                var via = new ViaJsonRpc(_settings.AccessHttpUrl);
                var order = via.OrderCompletedDetails(req.Id);
                if (order == null)
                    return BadRequest("invalid parameter");
                if (order.user != xch.Id)
                    return BadRequest("invalid parameter");
                return FormatOrder(order);
            }
            catch (ViaJsonException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost]
        public ActionResult<ApiOrder> OrderCancel([FromBody] ApiOrderCancel req) 
        {
            string error;
            var apikey = AuthKey(req.Key, req.Nonce, out error);
            if (apikey == null)
                return BadRequest(error);
            var xch = _context.Exchange.SingleOrDefault(x => x.ApplicationUserId == apikey.ApplicationUserId);
            if (xch == null)
                return BadRequest(); 
            try
            {
                // lock process of performing trade
                lock (_userLocks.GetLock(apikey.ApplicationUserId))
                {
                    //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
                    var via = new ViaJsonRpc(_settings.AccessHttpUrl);
                    var order = via.OrderCancelQuery(xch.Id, req.Market, req.Id);
                    if (order == null)
                        return BadRequest("invalid parameter");
                    return FormatOrder(order);
                }
            }
            catch (ViaJsonException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        ApiTrade FormatTrade(MarketTransactionRecord trade, string market, string tradeAsset, string priceAsset)
        {
            var feeAsset = priceAsset;
            if (trade.side == OrderSide.Bid)
                feeAsset = tradeAsset;
            var model = new ApiTrade
            {
                Id = trade.id,
                Market = market,
                Role = trade.role == 1 ? "maker" : "taker",
                Side = trade.side == OrderSide.Bid ? "buy" : "sell",
                Amount = trade.amount,
                Price = trade.price,
                ExecutedValue = trade.deal,
                Fee = trade.fee,
                FeeAsset = feeAsset,
                Date = (int)trade.time,
                OrderId = trade.deal_order_id,
            };
            return model;        
        }

        [HttpPost]
        public ActionResult<ApiTradesResponse> TradesExecuted([FromBody] ApiTrades req) 
        {
            if (!_settings.Markets.ContainsKey(req.Market))
                return BadRequest("invalid request");
            var marketSettings = _settings.Markets[req.Market];

            string error;
            var apikey = AuthKey(req.Key, req.Nonce, out error);
            if (apikey == null)
                return BadRequest(error);
            var xch = _context.Exchange.SingleOrDefault(x => x.ApplicationUserId == apikey.ApplicationUserId);
            if (xch == null)
                return BadRequest(); 
            try
            {
                //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
                var via = new ViaJsonRpc(_settings.AccessHttpUrl);
                var trades = via.MarketTransactionHistoryQuery(xch.Id, req.Market, req.Offset, req.Limit);
                var model = new ApiTradesResponse { Offset = trades.offset, Limit = trades.limit, Trades = new List<ApiTrade>() };
                foreach (var trade in trades.records)
                    model.Trades.Add(FormatTrade(trade, req.Market, marketSettings.AmountUnit, marketSettings.PriceUnit));
                return model;
            }
            catch (ViaJsonException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet]
        [HttpPost]
        public ActionResult<ApiBrokerMarkets> BrokerMarkets() 
        {
            var model = new ApiBrokerMarkets { SellMarkets = _apiSettings.Broker.SellMarkets, BuyMarkets = _apiSettings.Broker.BuyMarkets };
            return model;
        }

        bool ValidateBrokerMarket(string market, string side, out OrderSide orderSide)
        {
            orderSide = OrderSide.Any;
            // validate market
            if (market == null)
            {
                _logger.LogError("market parameter is null");
                return false;
            }
            if (!_settings.Markets.ContainsKey(market))
            {
                _logger.LogError($"market ('{market}') does not exist");
                return false;
            }
            if (side == "buy")
            {
                orderSide = OrderSide.Bid;
                if (_apiSettings.Broker.BuyMarkets == null || !_apiSettings.Broker.BuyMarkets.Contains(market))
                {
                    _logger.LogError($"market ('{market}') is not a valid buy market");
                    return false;
                }
            }
            else if (side == "sell")
            {
                orderSide = OrderSide.Ask;
                if (_apiSettings.Broker.SellMarkets == null || !_apiSettings.Broker.SellMarkets.Contains(market))
                {
                    _logger.LogError($"market ('{market}') is not a valid sell market");
                    return false;
                }
            }
            else
            {
                _logger.LogError($"order side ('{side}') is not a valid");
                return false;
            }
            return true;
        }

        ApiBrokerQuoteResponse _brokerQuote(string market, string amount, OrderSide side, out string error, out decimal avgPrice)
        {
            error = null;
            avgPrice = 0;
            var marketSettings = _settings.Markets[market];
            try
            {
                // get orderbook
                //TODO: move this to a ViaRpcProvider in /Services (like IWalletProvider)
                var via = new ViaJsonRpc(_settings.AccessHttpUrl);
                var orderDepth = via.OrderDepthQuery(market, 100, marketSettings.PriceInterval);
                // calculate price
                decimal amountSend = 0;
                decimal amountReceive = 0;
                var assetSend = "";
                var assetReceive = "";
                var amountLeft = decimal.Parse(amount);
                if (side == OrderSide.Bid)
                {
                    assetSend = marketSettings.PriceUnit;
                    assetReceive = marketSettings.AmountUnit;
                    amountReceive = amountLeft;

                    var depth = orderDepth.asks;
                    while (amountLeft > 0)
                    {
                        if (depth.Count() == 0)
                        {
                            error = "insufficient liquidity";
                            return null;
                        }
                        var priceItem = decimal.Parse(depth[0][0]);
                        var amountItem = decimal.Parse(depth[0][1]);
                        depth.RemoveAt(0);
                        var amountToUse = amountItem;
                        if (amountLeft < amountItem)
                            amountToUse = amountLeft;
                        amountSend += priceItem * amountToUse;
                        amountLeft -= amountToUse;
                    }
                    if (amountSend > 0)
                        avgPrice = amountReceive / amountSend;
                    amountSend *= (1 + _apiSettings.Broker.Fee);
                }
                else
                {
                    assetSend = marketSettings.AmountUnit;
                    assetReceive = marketSettings.PriceUnit;
                    amountSend = amountLeft;

                    var depth = orderDepth.bids;
                    while (amountLeft > 0)
                    {
                        if (depth.Count() == 0)
                        {
                            error = "insufficient liquidity";
                            return null;
                        }
                        var priceItem = decimal.Parse(depth[0][0]);
                        var amountItem = decimal.Parse(depth[0][1]);
                        depth.RemoveAt(0);
                        var amountToUse = amountItem;
                        if (amountLeft < amountItem)
                            amountToUse = amountLeft;
                        amountReceive += priceItem * amountToUse;
                        amountLeft -= amountToUse;
                    }
                    if (amountSend > 0)
                        avgPrice = amountSend / amountReceive;
                    amountReceive *= (1 - _apiSettings.Broker.Fee);
                }
                var model = new ApiBrokerQuoteResponse
                {
                    AssetSend = assetSend,
                    AmountSend = amountSend,
                    AssetReceive = assetReceive,
                    AmountReceive = amountReceive,
                    TimeLimit = _apiSettings.Broker.TimeLimitMinutes,
                };
                return model;
            }
            catch (ViaJsonException ex)
            {
                error = ex.Message;
                return null;
            }
        }

        [HttpPost]
        public ActionResult<ApiBrokerQuoteResponse> BrokerQuote([FromBody] ApiBrokerQuote req) 
        {
            // if tripwire tripped cancel
            if (!_tripwire.TradingEnabled() || !_tripwire.WithdrawalsEnabled())
            {
                _logger.LogError("Tripwire tripped, exiting BrokerQuote()");
                return BadRequest();
            }
            // validate market
            OrderSide side;
            if (!ValidateBrokerMarket(req.Market, req.Side, out side))
            {
                _logger.LogError($"Failed to validate broker market {req.Market} (side: {req.Side})");
                return BadRequest();
            }
            // validate auth
            string error;
            var apikey = AuthKey(req.Key, req.Nonce, out error);
            if (apikey == null)
            {
                _logger.LogError($"Failed to validate apikey: {req.Key}");
                return BadRequest(error);
            }
            var xch = _context.Exchange.SingleOrDefault(x => x.ApplicationUserId == apikey.ApplicationUserId);
            if (xch == null)
            {
                _logger.LogError($"Failed get exchange for user id: {apikey.ApplicationUserId}");
                return BadRequest();
            }
            // get quote
            decimal avgPrice;
            var model = _brokerQuote(req.Market, req.Amount, side, out error, out avgPrice);
            if (model == null)
            {
                _logger.LogError($"Failed to create broker quote (market: {req.Market}, amount: {req.Amount}, side: {side})");
                return BadRequest(error);
            }
            return model;
        }

        bool ValidateRecipient(string market, OrderSide side, string recipient)
        {
            var marketSettings = _settings.Markets[market];
            var assetToReceive = marketSettings.AmountUnit;
            if (side == OrderSide.Ask)
                assetToReceive = marketSettings.PriceUnit;
            if (_walletProvider.IsChain(assetToReceive))
            {
                var wallet = _walletProvider.GetChain(assetToReceive);
                return wallet.ValidateAddress(recipient);
            }
            else
                return Utils.ValidateBankAccount(recipient);
        }

        private static ApiBrokerOrder FormatOrder(BrokerOrder order)
        {
            return new ApiBrokerOrder
            {
                AssetReceive = order.AssetReceive,
                AmountReceive = order.AmountReceive,
                AssetSend = order.AssetSend,
                AmountSend = order.AmountSend,
                Expiry = order.Expiry,
                Token = order.Token,
                InvoiceId = order.InvoiceId,
                PaymentAddress = order.PaymentAddress,
                PaymentUrl = order.PaymentUrl,
                TxIdPayment = order.TxIdPayment,
                Recipient = order.Recipient,
                TxIdRecipient = order.TxIdRecipient,
                Status = order.Status,
            };
        }

        [HttpPost]
        public ActionResult<ApiBrokerOrder> BrokerCreate([FromBody] ApiBrokerCreate req)
        {
            // if tripwire tripped cancel
            if (!_tripwire.TradingEnabled() || !_tripwire.WithdrawalsEnabled())
            {
                _logger.LogError("Tripwire tripped, exiting BrokerCreate()");
                return BadRequest();
            }
            // validate market
            OrderSide side;
            if (!ValidateBrokerMarket(req.Market, req.Side, out side))
                return BadRequest("invalid market");
            if (!ValidateRecipient(req.Market, side, req.Recipient))
                return BadRequest("invalid recipient");
            // validate auth
            string error;
            var apikey = AuthKey(req.Key, req.Nonce, out error);
            if (apikey == null)
                return BadRequest(error);
            // get quote
            decimal avgPrice;
            var quote = _brokerQuote(req.Market, req.Amount, side, out error, out avgPrice);
            if (quote == null)
            {
                _logger.LogError($"Failed to create broker quote (market: {req.Market}, amount: {req.Amount}, side: {side})");
                return BadRequest(error);
            }
            // create order
            string token = Utils.CreateToken();
            var order = new BrokerOrder
            {
                ApplicationUserId = apikey.ApplicationUserId,
                AssetReceive = quote.AssetReceive,
                AmountReceive = quote.AmountReceive,
                AssetSend = quote.AssetSend,
                AmountSend = quote.AmountSend,
                Date = DateTimeOffset.Now.ToUnixTimeSeconds(),
                Expiry = DateTimeOffset.Now.ToUnixTimeSeconds() + _apiSettings.Broker.TimeLimitMinutes * 60,
                Fee = _apiSettings.Broker.Fee,
                Market = req.Market,
                Side = side,
                Price = avgPrice,
                Token = token,
                InvoiceId = null,
                PaymentAddress = null,
                PaymentUrl = null,
                Recipient = req.Recipient,
                Status = BrokerOrderStatus.Created.ToString(),
            };
            _context.BrokerOrders.Add(order);
            _context.SaveChanges();
            // respond
            return FormatOrder(order);
        }

        [HttpPost]
        public async Task<ActionResult<ApiBrokerOrder>> BrokerAccept([FromBody] ApiBrokerStatus req)
        {
            // if tripwire tripped cancel
            if (!_tripwire.TradingEnabled() || !_tripwire.WithdrawalsEnabled())
            {
                _logger.LogError("Tripwire tripped, exiting BrokerAccept()");
                return BadRequest();
            }
            // validate auth
            string error;
            var apikey = AuthKey(req.Key, req.Nonce, out error);
            if (apikey == null)
                return BadRequest(error);
            // get order
            var order = _context.BrokerOrders.SingleOrDefault(o => o.ApplicationUserId == apikey.ApplicationUserId && o.Token == req.Token);
            if (order == null)
                return BadRequest();
            // update order status
            if (order.Status == BrokerOrderStatus.Created.ToString())
            {
                if (DateTimeOffset.Now.ToUnixTimeSeconds() < order.Expiry)
                    order.Status = BrokerOrderStatus.Ready.ToString();
                else
                    order.Status = BrokerOrderStatus.Expired.ToString();
            }
            if (order.Status == BrokerOrderStatus.Ready.ToString())
            {
                // get user
                var user = await _userManager.FindByIdAsync(apikey.ApplicationUserId);
                if (user == null)
                    return BadRequest();
                // check kyc limits
                (var success, var withdrawalAssetAmount, var error2) = ValidateWithdrawlLimit(user, order.AssetReceive, order.AmountReceive);
                if (!success)
                    return BadRequest(error2);
                // register withdrawal with kyc limits
                user.AddWithdrawal(_context, order.AssetReceive, order.AmountReceive, withdrawalAssetAmount);
            }
            // check/create broker user
            var brokerUser = await _userManager.FindByNameAsync(_apiSettings.Broker.BrokerTag);
            if (brokerUser == null)
            {
                (var result, var user) = await CreateUser(_signInManager, _emailSender, _apiSettings.Broker.BrokerTag, email: null, password: null, sendEmail: false, signIn: false);
                if (!result.Succeeded)
                {
                    _logger.LogError("Failed to create broker user");
                    return BadRequest();
                }
                brokerUser = user;
            }
            // check broker exchange id
            if (brokerUser.Exchange == null)
            {
                _logger.LogError("Failed to get broker exchange id");
                return BadRequest();
            }
            // create payment details
            if (order.Status == BrokerOrderStatus.Ready.ToString())
            {
                string invoiceId = null;
                string paymentAddress = null;
                string paymentUrl = null;
                if (_walletProvider.IsChain(order.AssetSend))
                {
                    var wallet = _walletProvider.GetChain(order.AssetSend);
                    var assetSettings = _walletProvider.ChainAssetSettings(order.AssetSend);
                    if (!wallet.HasTag(brokerUser.Id))
                    {
                        wallet.NewTag(brokerUser.Id);
                        wallet.Save();
                    }
                    if (assetSettings.LedgerModel == LedgerModel.Account)
                    {
                        invoiceId = Utils.CreateToken();
                        paymentAddress = wallet.NewOrExistingAddress(brokerUser.Id).Address;
                    }
                    else // UTXO
                        paymentAddress = wallet.NewAddress(brokerUser.Id).Address;
                    wallet.Save();
                }
                else // fiat
                {
                    if (!_fiatPaymentSettings.PaymentProcessorEnabled)
                    {
                        _logger.LogError("fiat payments not enabled");
                        return BadRequest();
                    }
                    if (!_fiatPaymentSettings.PaymentProcessorAssets.Contains(order.AssetSend))
                    {
                        _logger.LogError($"fiat payments of '${order.AssetSend}' not enabled");
                        return BadRequest();
                    }
                    var fiatReq = CreateFiatPaymentRequest(_fiatPaymentSettings, order.Token, order.AssetSend, order.AmountSend);
                    if (fiatReq == null)
                    {
                        _logger.LogError("fiat payment request creation failed");
                        return BadRequest();
                    }
                    paymentUrl = fiatReq.ServiceUrl;
                }
                //  update order payment details
                order.InvoiceId = invoiceId;
                order.PaymentAddress = paymentAddress;
                order.PaymentUrl = paymentUrl;
            }
            // save order and withdrawal record
            _context.BrokerOrders.Update(order);
            _context.SaveChanges();
            return FormatOrder(order);
        }

        [HttpPost]
        public ActionResult<ApiBrokerOrder> BrokerStatus([FromBody] ApiBrokerStatus req)
        {
            // validate auth
            string error;
            var apikey = AuthKey(req.Key, req.Nonce, out error);
            if (apikey == null)
                return BadRequest(error);
            // get order
            var order = _context.BrokerOrders.SingleOrDefault(o => o.ApplicationUserId == apikey.ApplicationUserId && o.Token == req.Token);
            if (order == null)
                return BadRequest();
            return FormatOrder(order);
        }
    }
}