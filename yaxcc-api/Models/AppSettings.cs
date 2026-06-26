using System;
using System.Collections.Generic;
using System.Text;

namespace yaxcc_api.Models
{
    public class AppSettings
    {
        public GeneralSettings General { get; set; } = new();
    }

    public class GeneralSettings
    {
        public bool DisableSwagger { get; set; } = false;
    }
}
