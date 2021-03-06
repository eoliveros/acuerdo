#!/bin/bash

set -e

. influxdb.sh
. ssh_users.sh

DEPLOY_TEST=test
DEPLOY_PRODUCTION=production
DEPLOY_LOCAL=local
DEPLOY_USER=$1
DEPLOY_TYPE=$2

display_usage() { 
    echo -e "\nUsage:

    ansible_deploy.sh <DEPLOY_USER> <DEPLOY_TYPE ($DEPLOY_TEST | $DEPLOY_PRODUCTION | $DEPLOY_LOCAL)> 

    "
} 

# if less than two arguments supplied, display usage 
if [  $# -le 0 ]
then 
    display_usage
    exit 1
fi 

# check whether user had supplied -h or --help . If yes display usage 
if [[ ( $@ == "--help" ) ||  ( $@ == "-h" ) ]] 
then 
    display_usage
    exit 0
fi 

# check whether user has a valid DEPLOY_TYPE
if [[ ( $DEPLOY_TYPE != "$DEPLOY_TEST" ) &&  ( $DEPLOY_TYPE != "$DEPLOY_PRODUCTION" ) && ( $DEPLOY_TYPE != "$DEPLOY_LOCAL" ) ]] 
then 
    display_usage
    echo !!\"$DEPLOY_TYPE\" is not valid
    exit 2
fi 

DOMAIN=example.com #TODO replace example.com with your domain
ADMIN_EMAIL=admin@$DOMAIN
ALERT_EMAIL=alert@$DOMAIN
VAGRANT=false

# set deploy variables for production
DEPLOY_HOST=backend.$DOMAIN
DEPLOY_HOST_INTERNAL=backend-internal.$DOMAIN
FRONTEND_HOST=internal.$DOMAIN
DEBUG_HOST=
TESTNET=

clr=$'\e[0m'
# red background for production deploy
color=$'\e[48;2;255;0;0m'

# set deploy variables for test
if [[ ( $DEPLOY_TYPE == "$DEPLOY_TEST" ) ]]
then 
    # yellow background for production deploy
    color=$'\e[48;2;255;255;0m'
    DEPLOY_HOST=backend.test.$DOMAIN
    DEPLOY_HOST_INTERNAL=backend-internal.test.$DOMAIN
    FRONTEND_HOST=test-internal.$DOMAIN
    TESTNET=true
fi 
ADMIN_HOST=`dig +short $FRONTEND_HOST`
# set deploy variables for local
if [[ ( $DEPLOY_TYPE == "$DEPLOY_LOCAL" ) ]]
then 
    color=$white
    DEPLOY_HOST=10.50.1.100
    DEPLOY_HOST_INTERNAL=10.50.1.100
    FRONTEND_HOST=$DEPLOY_HOST
    TESTNET=true
    ADMIN_HOST=$FRONTEND_HOST
    DEBUG_HOST=10.50.1.1 # dev pc
fi 

# read mysql user/pass from local file
MYSQL_USER_FILE=creds/$DEPLOY_TYPE/mysql_user
MYSQL_PASS_FILE=creds/$DEPLOY_TYPE/mysql_pass
MYSQL_USER=$(cat $MYSQL_USER_FILE)
MYSQL_PASS=$(cat $MYSQL_PASS_FILE)

# read backblaze b2 creds
B2_ACCT_ID_FILE=creds/$DEPLOY_TYPE/b2_acct_id
B2_APP_KEY_FILE=creds/$DEPLOY_TYPE/b2_app_key
B2_BUCKET_FILE=creds/$DEPLOY_TYPE/b2_bucket
B2_ACCT_ID=$(cat $B2_ACCT_ID_FILE)
B2_APP_KEY=$(cat $B2_APP_KEY_FILE)
B2_BUCKET=$(cat $B2_BUCKET_FILE)

# backup public key
GPG_PUBLIC_KEY=creds/$DEPLOY_TYPE/gpg_pub.key
if [[ ! -f $GPG_PUBLIC_KEY ]]; then
    echo '$GPG_PUBLIC_KEY' does not exist
    exit 3
fi
GPG_PUBLIC_KEY=`realpath $GPG_PUBLIC_KEY`

# backup databases
BACKUP_DBS="viafront trade_log trade_history btc_wallet waves_wallet zap_wallet nzd_wallet"

AUTH_URL=http://$FRONTEND_HOST:5000/Internal/WebsocketAuth

MYSQL_HOST=127.0.0.1
REDIS_HOST=127.0.0.1
KAFKA_HOST=127.0.0.1
MATCH_HOST=127.0.0.1
PRICE_HOST=127.0.0.1
DATA_HOST=127.0.0.1
HTTP_HOST=127.0.0.1
WS_HOST=127.0.0.1
ALERT_HOST=127.0.0.1

ROOT_DIR=/opt/viabtc
CONF_DIR=/opt/viabtc_conf
REDIS_PASS=

IF_EXTERNAL=eth0
IF_INTERNAL=eth1

# create archive
(cd ../viabtc_exchange_server; git archive --format=zip HEAD > viabtc_xch.zip)

# read influxdb details 
INFLUXDB_DIR=creds/$DEPLOY_TYPE
discover_influxdb $INFLUXDB_DIR INFLUXDB_SERVER INFLUXDB_USER INFLUXDB_PASS

# read ssh users
SSH_USERS_DIR=creds/$DEPLOY_TYPE/ssh_users
discover_ssh_users $SSH_USERS_DIR USE_SSH_USERS SSH_USERS SSH_USER_PUBKEYS

# print variables
echo $color":: DEPLOYMENT DETAILS ::"$clr
echo "   - DEPLOY_USER:     $DEPLOY_USER"
echo "   - DEPLOY_HOST:     $DEPLOY_HOST"
echo "   - TESTNET:         $TESTNET"
echo "   - ADMIN_EMAIL:     $ADMIN_EMAIL"
echo "   - ADMIN_HOST:      $ADMIN_HOST"
echo "   - DEBUG_HOST:      $DEBUG_HOST"
echo "   - MYSQL_USER:      $MYSQL_USER"
echo "   - MYSQL_PASS:      *${#MYSQL_PASS} chars*"
echo "   - B2_ACCT_ID:      $B2_ACCT_ID"
echo "   - B2_APP_KEY:      *${#B2_APP_KEY} chars*"
echo "   - B2_BUCKET:       $B2_BUCKET"
echo "   - CODE ARCHIVE:    viabtc_xch.zip"
echo "   - USE_SSH_USERS:   $USE_SSH_USERS"
echo "   - SSH_USERS:       $SSH_USERS"
echo "   - INFLUXDB_SERVER: $INFLUXDB_SERVER"

# ask user to continue
read -p "Are you sure? " -n 1 -r
echo # (optional) move to a new line
if [[ $REPLY =~ ^[Yy]$ ]]
then
    # do dangerous stuff
    echo ok lets go!!!
    INVENTORY_HOST=$DEPLOY_HOST
    if [[ ( $DEPLOY_TYPE == "$DEPLOY_LOCAL" ) ]]; then 
        DEPLOY_HOST=acuerdo.local
    fi 
    SSH_VARS="{\"use_ssh_users\": $USE_SSH_USERS, \"ssh_users\": $SSH_USERS, \"ssh_user_pubkeys\": $SSH_USER_PUBKEYS}"
    echo "$SSH_VARS" > ssh_vars.json
    ansible-playbook --inventory "$INVENTORY_HOST," --user "$DEPLOY_USER" -v \
        --extra-vars "admin_email=$ADMIN_EMAIL deploy_type=$DEPLOY_TYPE deploy_host=$DEPLOY_HOST vagrant=$VAGRANT testnet=$TESTNET admin_host=$ADMIN_HOST debug_host=$DEBUG_HOST mysql_host=$MYSQL_HOST mysql_user=$MYSQL_USER mysql_pass=$MYSQL_PASS redis_host=$REDIS_HOST kafka_host=$KAFKA_HOST match_host=$MATCH_HOST price_host=$PRICE_HOST data_host=$DATA_HOST http_host=$HTTP_HOST ws_host=$WS_HOST alert_host=$ALERT_HOST root_dir=$ROOT_DIR conf_dir=$CONF_DIR smtp_host=$DEPLOY_HOST_INTERNAL smtp_relay_host=$FRONTEND_HOST mysql_user_match_host=$MATCH_HOST mysql_user_data_host=$DATA_HOST redis_pass=$REDIS_PASS auth_url=$AUTH_URL kafka_advertised_listener=$DEPLOY_HOST_INTERNAL alert_email=$ALERT_EMAIL if_external=$IF_EXTERNAL if_internal=$IF_INTERNAL b2_acct_id=$B2_ACCT_ID b2_app_key=$B2_APP_KEY b2_bucket=$B2_BUCKET gpg_public_key=$GPG_PUBLIC_KEY backup_dbs='$BACKUP_DBS'" \
        --extra-vars "influxdb_server=$INFLUXDB_SERVER influxdb_user=$INFLUXDB_USER influxdb_pass=$INFLUXDB_PASS" \
        --extra-vars "@ssh_vars.json" \
        ../viabtc_exchange_server/provisioning/deploy.yml
fi
