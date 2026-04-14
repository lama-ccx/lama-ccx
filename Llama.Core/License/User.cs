using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Llama.Core.License
{
    public enum LicenseType
    {
        Free = 0,
        Academic = 1,
        Professional = 2
    }

    public class User
    {
        public string user_name { get; set; }
        public string mac_address { get; set; }
        public DateTime expiring_date { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public LicenseType license_type { get; set; }
    }
}
