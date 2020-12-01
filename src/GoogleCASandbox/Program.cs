using CAProxy.AnyGateway.Interfaces;
using Keyfactor.AnyGateway.Google;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace GoogleCASandbox
{
    class Program
    {
        static void Main(string[] args)
        {
            GoogleCAProxy caProxy = new GoogleCAProxy();

            caProxy.Initialize(new ConfigProvider() { CAConnectionData = new Dictionary<string, object>() {
                ["ProjectId"] = "concise-frame-296019",
                ["LocationId"] = "us-east1",
                ["CAId"] = "kf-cpr-enterprise-sandbox-ca",
                ["GCPCredentialFilePath"] = "C:\\CMS\\concise-frame-296019-2e104088b76a.json"
            } }); ;


        }

        static bool CompareFiles(string file1, string file2)
        {
            if (!File.Exists(file1))
                throw new FileNotFoundException($"Cannot find {file1} for comparison");

            if (!File.Exists(file2))
                throw new FileNotFoundException($"Cannot find {file2} for comparison");


            byte[] file1Hash = SHA1.Create().ComputeHash(File.ReadAllBytes(file1));
            byte[] file2Hash = SHA1.Create().ComputeHash(File.ReadAllBytes(file2));

            return Convert.ToBase64String(file1Hash).Equals(Convert.ToBase64String(file2Hash));

        }
    }

    class ConfigProvider : ICAConnectorConfigProvider
    {
        public Dictionary<string, object> CAConnectionData { get; set; }
    }


}
