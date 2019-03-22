﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace viafront3.Models.ManageViewModels
{
    public class ApiViewModel : BaseViewModel
    {
        public IList<Device> Devices { get; set; }
        public string DeleteDeviceKey { get; set; }
    }
}
