﻿using System;
using System.Collections.Generic;
using System.Linq;
using via_jsonrpc;

namespace viafront3.Models.ApiViewModels
{
    public struct ApiToken
    {
        public string Token;
    }

    public struct ApiDevice
    {
        public bool Completed;
        public string DeviceKey;
        public string DeviceSecret;
    }

    public class ApiAccountCreate
    {
        public string Email { get; set; }
        public string DeviceName { get; set; }
    }

    public class ApiDeviceCreate
    {
        public string Email { get; set; }
        public string DeviceName { get; set; }
    }

    public class ApiAuth
    {
        public String Key { get; set; }
        public long Nonce { get; set; }
    }

    public class ApiAccountBalance
    {
        public Dictionary<string, Balance> Assets { get; set; }
    }

    public class ApiMarketList
    {
        public List<String> Markets { get; set; }
    }
    
    public class ApiMarketPeriod
    {
        public string Market { get; set; }
        public int? Period { get; set; }
    }

    public class ApiMarketStatus
    {
        public int Period { get; set; }
        public string Open { get; set; }
        public string Close { get; set; }
        public string High { get; set; }
        public string Low { get; set; }
        public string Volume { get; set; }
    }

    public class ApiMarket
    {
        public string Market { get; set; }
    }

    public class ApiMarketDetail
    {
        public string TakerFeeRate { get; set; }
        public string MakerFeeRate { get; set; }
        public string MinAmount { get; set; }
        public string TradeAsset { get; set; }
        public string PriceAsset { get; set; }
        public int TradeDecimals { get; set; }
        public int PriceDecimals { get; set; }        
    }

    public class ApiMarketDepth
    {
        public string Market { get; set; }
        public string Merge { get; set; }
        public int? Limit { get; set; }
    }

    public class ApiMarketDepthResponse
    {
        public IList<IList<string>> Asks { get; set; }
        public IList<IList<string>> Bids { get; set; }
    }

    public class ApiMarketHistory
    {
        public string Market { get; set; }
        public int? Limit { get; set; }
    }

    public class ApiMarketTrade
    {
        public int Date { get; set; }
        public string Price { get; set; }
        public string Amount { get; set; }
        public string Type { get; set; }
    }

    public class ApiMarketHistoryResponse
    {
        public IList<ApiMarketTrade> Trades;
    }

    public class ApiOrderCreateMarket : ApiAuth
    {
        public string Market { get; set; }
        public string Side { get; set; }
        public string Amount { get; set; }
    }

    public class ApiOrderCreateLimit : ApiOrderCreateMarket
    {
        public string Price { get; set; }
    }

    public class ApiOrder
    {
        public int Id { get; set; }
        public string Market { get; set; }
        public string Type { get; set; }
        public string Side { get; set; }
        public string Amount { get; set; }
        public string Price { get; set; }
        public string Status { get; set; }
        public int DateCreated { get; set; }
        public int DateModified { get; set; }
        public string AmountTraded { get; set; }
        public string ExecutedValue { get; set; }
        public string FeePaid { get; set; }
        public string MakerFeeRate { get; set; }
        public string TakerFeeRate { get; set; }
    }

    public class ApiOrders: ApiAuth
    {
        public string Market { get; set; }
        public int Offset { get; set; }
        public int Limit { get; set; }       
    }

    public class ApiOrdersResponse
    {
        public int Offset { get; set; }
        public int Limit { get; set; }       
        public IList<ApiOrder> Orders;   
    }

    public class ApiOrderPendingStatus: ApiAuth
    {
        public string Market { get; set; }
        public int Id { get; set; }
    }

    public class ApiOrderExecutedStatus: ApiAuth
    {
        public int Id { get; set; }
    }

    public class ApiOrderCancel: ApiAuth
    {
        public string Market { get; set; }
        public int Id { get; set; }
    }

    public class ApiTrade
    {
        public int Id { get; set; }
        public string Market { get; set; }
        public string Role { get; set; }
        public string Side { get; set; }
        public string Amount { get; set; }
        public string Price { get; set; }
        public string ExecutedValue { get; set; }
        public string Fee { get; set; }
        public string FeeAsset { get; set; }
        public int Date { get; set; }
        public int OrderId { get; set; }
    }

    public class ApiTrades: ApiAuth
    {
        public string Market { get; set; }
        public int Offset { get; set; }
        public int Limit { get; set; }       
    }

    public class ApiTradesResponse
    {
        public int Offset { get; set; }
        public int Limit { get; set; }       
        public IList<ApiTrade> Trades;   
    }
}