using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;

namespace Llama.Core.License
{
    public static class License
    {
        public static string AssemblyLocation = Assembly.GetExecutingAssembly().Location;
        public static string LlamaFolder = Path.GetDirectoryName(AssemblyLocation);
        public static string LicenseLocation = Path.Combine(LlamaFolder, "data.bin");

        private static int validationCounter = 0;
        private static DateTime? validationFirstTimeRun = DateTime.Now;
        private static DateTime? validationLastTimeRun = null;

        public static bool IsValid
        {
            get
            {
                var addresses = GetMacAddress();

                try
                {
                    var users = DeserializeBinary(LicenseLocation);

                    foreach (var user in users)
                    {
                        if (user.user_name == "FreeVersion")
                        {
                            if (DateTime.Now < user.expiring_date)
                                return true;
                        }

                        foreach (var macAddress in addresses)
                        {
                            if (macAddress == user.mac_address && DateTime.Now < user.expiring_date)
                                return true;
                        }
                    }
                }
                catch
                {
                    return false;
                }

                return false;
            }
        }

        /// <summary>
        /// Returns the highest active license tier, or Free if none found.
        /// </summary>
        public static LicenseType CurrentTier
        {
            get
            {
                var addresses = GetMacAddress();
                var bestTier = LicenseType.Free;

                try
                {
                    var users = DeserializeBinary(LicenseLocation);

                    foreach (var user in users)
                    {
                        if (DateTime.Now >= user.expiring_date)
                            continue;

                        bool matches = user.user_name == "FreeVersion"
                            || addresses.Any(mac => mac == user.mac_address);

                        if (matches && user.license_type > bestTier)
                            bestTier = user.license_type;
                    }
                }
                catch
                {
                    return LicenseType.Free;
                }

                return bestTier;
            }
        }

        public static List<string> GetMacAddress()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Select(x => x.GetPhysicalAddress().ToString())
                .ToList();
        }

        public static List<User> DeserializeBinary(string filePath)
        {
            byte[] binaryData = File.ReadAllBytes(filePath);
            var deserializedObject = MessagePack.MessagePackSerializer.Typeless.Deserialize(binaryData);
            return (List<User>)deserializedObject;
        }

        public static byte[] SerializeJsonToBinary(string filePath)
        {
            string jsonData = File.ReadAllText(filePath);
            var myObject = Newtonsoft.Json.JsonConvert.DeserializeObject<List<User>>(jsonData);
            return MessagePack.MessagePackSerializer.Typeless.Serialize(myObject);
        }

        public static void SerializeBinaryToFile(string filePath, byte[] binaryData)
        {
            File.WriteAllBytes(filePath, binaryData);
        }

        /// <summary>
        /// Validates license and invokes callbacks based on tier.
        /// Professional / Academic: no element limits.
        /// Free: capped at maxElements; exceeding shows the license form.
        /// </summary>
        public static bool ValidateLicense(
            int elementCount,
            bool forceCheck = false,
            Action showFormCallback = null,
            int maxElements = 100)
        {
            validationLastTimeRun = DateTime.Now;

            if (forceCheck || validationCounter == 0 || validationLastTimeRun - validationFirstTimeRun > TimeSpan.FromMinutes(5))
            {
                var tier = CurrentTier;

                if (tier == LicenseType.Professional || tier == LicenseType.Academic)
                {
                    // Licensed: no element limits
                }
                else
                {
                    // Free tier
                    if (elementCount > maxElements)
                    {
                        showFormCallback?.Invoke();
                        return false;
                    }
                }

                if (!forceCheck)
                {
                    validationFirstTimeRun = DateTime.Now;
                    validationCounter++;
                }
            }

            return true;
        }
    }
}
