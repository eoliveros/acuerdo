﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using via_jsonrpc;

namespace viafront3.Models.InternalViewModels
{
    public class UserInfo
    {
        public ApplicationUser User { set; get; }
        public int ExchangeId { set; get; } 
        public List<string> Roles { set; get; } 
    }

    public class UsersViewModel : BaseViewModel
    {
        public List<UserInfo> UserInfos { get; set; }
    }
}
