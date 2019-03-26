#!/usr/bin/python3

import sys
import argparse
import requests
import time
import hmac
import hashlib
import base64
import json

URL_BASE = "http://localhost:5000/api/v1/"

EXIT_NO_COMMAND = 1

def construct_parser():
    # construct argument parser
    parser = argparse.ArgumentParser()

    subparsers = parser.add_subparsers(dest="command")

    ## Account / Device

    parser_acct_create = subparsers.add_parser("account_create", help="Create an account")
    parser_acct_create.add_argument("email", metavar="EMAIL", type=str, help="the email")
    parser_acct_create.add_argument("device_name", metavar="DEVICE_NAME", type=str, help="the name for the created device")

    parser_acct_create_status = subparsers.add_parser("account_create_status", help="Check an account creation request")
    parser_acct_create_status.add_argument("token", metavar="TOKEN", type=str, help="the token")

    parser_acct_create_cancel = subparsers.add_parser("account_create_cancel", help="Cancel an account creation request")
    parser_acct_create_cancel.add_argument("token", metavar="TOKEN", type=str, help="the token")

    parser_device_create = subparsers.add_parser("device_create", help="Create an device")
    parser_device_create.add_argument("email", metavar="EMAIL", type=str, help="the email of the account")
    parser_device_create.add_argument("device_name", metavar="DEVICE_NAME", type=str, help="the name for the created device")

    parser_device_create_status = subparsers.add_parser("device_create_status", help="Check an device creation request")
    parser_device_create_status.add_argument("token", metavar="TOKEN", type=str, help="the token")

    parser_device_create_cancel = subparsers.add_parser("device_create_cancel", help="Cancel an device creation request")
    parser_device_create_cancel.add_argument("token", metavar="TOKEN", type=str, help="the token")

    parser_dev_destroy = subparsers.add_parser("device_destroy", help="Destroy device")
    parser_dev_destroy.add_argument("device_key", metavar="DEVICE_KEY", type=str, help="the device key")
    parser_dev_destroy.add_argument("device_secret", metavar="DEVICE_SECRET", type=str, help="the device secret")

    parser_dev_validate = subparsers.add_parser("device_validate", help="Validate device authentication")
    parser_dev_validate.add_argument("device_key", metavar="DEVICE_KEY", type=str, help="the device key")
    parser_dev_validate.add_argument("device_secret", metavar="DEVICE_SECRET", type=str, help="the device secret")

    ## Market

    parser_market_list = subparsers.add_parser("market_list", help="Get the list of markets")

    parser_market_status = subparsers.add_parser("market_status", help="Get the status of a market")
    parser_market_status.add_argument("market", metavar="MARKET", type=str, help="the market to query")
    parser_market_status.add_argument("period", metavar="PERIOD", type=int, nargs="?", default=86400, help="the time period to query in seconds")

    parser_market_detail = subparsers.add_parser("market_detail", help="Get the details of a market")
    parser_market_detail.add_argument("market", metavar="MARKET", type=str, help="the market to query")

    parser_market_depth = subparsers.add_parser("market_depth", help="Get the depth of a market")
    parser_market_depth.add_argument("market", metavar="MARKET", type=str, help="the market to query")
    parser_market_depth.add_argument("merge", metavar="MERGE", type=str, help="the smallest unit to merge (0.1 0.01, 0.001, etc")
    parser_market_depth.add_argument("limit", metavar="LIMIT", type=int, nargs="?", default=20, help="the maximum records to return")

    parser_market_history = subparsers.add_parser("market_history", help="Get the history of a market")
    parser_market_history.add_argument("market", metavar="MARKET", type=str, help="the market to query")
    parser_market_history.add_argument("limit", metavar="LIMIT", type=int, nargs="?", default=20, help="the maximum records to return")

    return parser

def create_sig(device_key, device_secret, message):
    _hmac = hmac.new(device_secret.encode('latin-1'), msg=message.encode('latin-1'), digestmod=hashlib.sha256)
    signature = _hmac.digest()
    signature = base64.b64encode(signature).decode("utf-8")
    return signature

def req(endpoint, params=None, device_key=None, device_secret=None):
    if device_key:
        if not params:
            params = {}
        params["nonce"] = int(time.time())
        params["key"] = device_key
    url = URL_BASE + endpoint
    if params:
        headers = {"Content-type": "application/json"}
        body = json.dumps(params)
        if device_key:
            headers["X-Signature"] = create_sig(device_key, device_secret, body)
        r = requests.post(url, headers=headers, data=body)
    else:
        r = requests.get(url)
    return r

def check_request_status(r):
    try:
        r.raise_for_status()
    except Exception as e:
        print("::ERROR::")
        print(str(r.status_code) + " - " + r.url)
        print(r.text)
        raise e

def account_create(args):
    print(":: calling account create..")
    r = req("AccountCreate", {"email": args.email, "deviceName": args.device_name})
    check_request_status(r)
    token = r.json()["token"]
    print("token: %s" % token)

def account_create_status(args):
    print(":: calling account creation status..")
    r = req("AccountCreateStatus", {"token": args.token})
    check_request_status(r)
    api_device = r.json()
    print(api_device)

def account_create_cancel(args):
    print(":: calling account creation cancel..")
    r = req("AccountCreateCancel", {"token": args.token})
    check_request_status(r)
    print("ok")

def device_create(args):
    print(":: calling device create..")
    r = req("DeviceCreate", {"email": args.email, "deviceName": args.device_name})
    check_request_status(r)
    token = r.json()["token"]
    print("token: %s" % token)

def device_create_status(args):
    print(":: calling device creation status..")
    r = req("DeviceCreateStatus", {"token": args.token})
    check_request_status(r)
    api_device = r.json()
    print(api_device)

def device_create_cancel(args):
    print(":: calling device creation cancel..")
    r = req("DeviceCreateCancel", {"token": args.token})
    check_request_status(r)
    print("ok")

def device_destroy(args):
    print(":: calling device destroy..")
    r = req("DeviceDestroy", None, args.device_key, args.device_secret)
    check_request_status(r)
    print("ok")

def device_validate(args):
    print(":: calling device validate..")
    r = req("DeviceValidate", None, args.device_key, args.device_secret)
    check_request_status(r)
    print("ok")

def market_list(args):
    print(":: calling market list..")
    r = req("MarketList")
    check_request_status(r)
    print(r.text)

def market_status(args):
    print(":: calling market status..")
    params = {"market": args.market}
    if args.period:
        params["period"] = args.period
    r = req("MarketStatus", params)
    check_request_status(r)
    print(r.text)

def market_detail(args):
    print(":: calling market detail..")
    r = req("MarketDetail", {"market": args.market})
    check_request_status(r)
    print(r.text)

def market_depth(args):
    print(":: calling market depth..")
    params = {"market": args.market, "merge": args.merge}
    if args.limit:
        params["limit"] = args.limit
    r = req("MarketDepth", params)
    check_request_status(r)
    print(r.text)

def market_history(args):
    print(":: calling market history..")
    params = {"market": args.market}
    if args.limit:
        params["limit"] = args.limit
    r = req("MarketHistory", params)
    check_request_status(r)
    print(r.text)

if __name__ == "__main__":
    # parse arguments
    parser = construct_parser()
    args = parser.parse_args()

    # set appropriate function
    function = None
    if args.command == "account_create":
        function = account_create
    elif args.command == "account_create_status":
        function = account_create_status
    elif args.command == "account_create_cancel":
        function = account_create_cancel
    elif args.command == "device_create":
        function = device_create
    elif args.command == "device_create_status":
        function = device_create_status
    elif args.command == "device_create_cancel":
        function = device_create_cancel
    elif args.command == "device_destroy":
        function = device_destroy
    elif args.command == "device_validate":
        function = device_validate
    elif args.command == "market_list":
        function = market_list
    elif args.command == "market_status":
        function = market_status
    elif args.command == "market_detail":
        function = market_detail
    elif args.command == "market_depth":
        function = market_depth
    elif args.command == "market_history":
        function = market_history
    else:
        parser.print_help()
        sys.exit(EXIT_NO_COMMAND)

    if function:
        function(args)
