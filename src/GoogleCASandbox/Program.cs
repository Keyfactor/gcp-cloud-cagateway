using CAProxy.AnyGateway.Interfaces;
using Google.Cloud.Security.PrivateCA.V1Beta1;
using Google.Protobuf.WellKnownTypes;
using Keyfactor.AnyGateway.Google;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Google.Protobuf;

namespace GoogleCASandbox
{
    class Program
    {
        static void Main(string[] args)
        {
            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", @"C:\cms\concise-frame-296019-2e104088b76a.json");

            CertificateAuthorityServiceClient gcp =  CertificateAuthorityServiceClient.Create();

            var caName = CertificateAuthorityName.FromProjectLocationCertificateAuthority("concise-frame-296019", "us-east1", "ca-enterprise-subordinate-sandbox-new");

            for (int i = 0; i <= 100; i++)
            {
                ByteString publicKey = ByteString.CopyFrom($"MYPUBLICKEY{i}", Encoding.ASCII);


                DateTime now = DateTime.Now;
                var response = gcp.CreateCertificate(new CreateCertificateRequest()
                {
                    CertificateId = $"loadtest-{i}-{now:HH}{now:mm}{now:ss}",
                    ParentAsCertificateAuthorityName = caName,
                    Certificate = new Certificate()
                    {
                        Lifetime = Duration.FromTimeSpan(new TimeSpan(1, 0, 0, 0, 0)),
                        Config = new CertificateConfig()
                        {
                            PublicKey = new PublicKey() { Key = publicKey, Type=PublicKey.Types.KeyType.PemRsaKey},
                            SubjectConfig = new CertificateConfig.Types.SubjectConfig()
                            {
                                CommonName = $"loadcert-{now:MMM}-{now:ffffff}"
                            }
                        }
                    }
                });

                Console.WriteLine($"Created Load Test Certificate {response.CertificateName.CertificateId}");
            }
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
