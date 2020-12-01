using CAProxy.AnyGateway;
using CAProxy.AnyGateway.Interfaces;
using CAProxy.AnyGateway.Models;
using CAProxy.Common;
using CSS.PKI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Cloud.Security.PrivateCA.V1Beta1;
using Google.Protobuf.WellKnownTypes;
using System.Security.Cryptography;
using System.IO;

namespace Keyfactor.AnyGateway.Google
{
    public class GoogleCAProxy : BaseCAConnector
    {
        const string AUTH_ENV_VARIABLE_NAME = "GOOGLE_APPLICATION_CREDENTIALS";
        const string PROJECT_ID_KEY = "ProjectId";
        const string LOCATION_ID_KEY = "LocationId";
        const string CA_ID_KEY = "CAId";

        private CertificateAuthorityServiceClient gcpClient { get; set; }
        private ICAConnectorConfigProvider Config { get; set; }

        [Obsolete]
        public override EnrollmentResult Enroll(string csr, string subject, Dictionary<string, string[]> san, EnrollmentProductInfo productInfo, CSS.PKI.PKIConstants.X509.RequestFormat requestFormat, RequestUtilities.EnrollmentType enrollmentType)
        {
            throw new NotImplementedException();
        }
        public override EnrollmentResult Enroll(ICertificateDataReader certificateDataReader, string csr, string subject, Dictionary<string, string[]> san, EnrollmentProductInfo productInfo, PKIConstants.X509.RequestFormat requestFormat, RequestUtilities.EnrollmentType enrollmentType)
        {
            throw new NotImplementedException();
        }

        public override CAConnectorCertificate GetSingleRecord(string caRequestID)
        {
            throw new NotImplementedException();
        }

        public override void Initialize(ICAConnectorConfigProvider configProvider)
        {
            try
            {
                Logger.Debug("Initialize GoogleCAProxy");
                //Save Validated Configuration object for later use
                Config = configProvider;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        public override void Ping()
        {

        }

        public override int Revoke(string caRequestID, string hexSerialNumber, uint revocationReason)
        {
            throw new NotImplementedException();
        }

        [Obsolete]
        public override void Synchronize(ICertificateDataReader certificateDataReader, BlockingCollection<CertificateRecord> blockingBuffer, CertificateAuthoritySyncInfo certificateAuthoritySyncInfo, CancellationToken cancelToken, string logicalName)
        {
            throw new NotImplementedException();
        }


        public override void Synchronize(ICertificateDataReader certificateDataReader, 
                                         BlockingCollection<CAConnectorCertificate> blockingBuffer, 
                                         CertificateAuthoritySyncInfo certificateAuthoritySyncInfo, 
                                         CancellationToken cancelToken)
        {
            gcpClient = CertificateAuthorityServiceClient.Create();

            string projectId = Config.CAConnectionData[PROJECT_ID_KEY] as string;
            string locationId = Config.CAConnectionData[LOCATION_ID_KEY] as string;
            string caId = Config.CAConnectionData[CA_ID_KEY] as string;

            //ListCertificatesRequest syncRequest = new ListCertificatesRequest() { Parent = "projects/concise-frame-296019/locations/us-east1/certificateAuthorities/kf-cpr-enterprise-sandbox-ca" };
            ListCertificatesRequest syncRequest = new ListCertificatesRequest() {
                ParentAsCertificateAuthorityName = CertificateAuthorityName.FromProjectLocationCertificateAuthority(projectId, locationId, caId),
            };

            if (!certificateAuthoritySyncInfo.DoFullSync)
            {
                Timestamp lastSyncTime = certificateAuthoritySyncInfo.LastFullSync.Value.ToUniversalTime().ToTimestamp();
                Logger.Trace($"Execute Incremental Sync.  Filter Certificate List by Update Time >= {lastSyncTime.ToDateTime().ToLocalTime()}");
                syncRequest.Filter = $"update_time >= {lastSyncTime}";
            }

            var responseTask =  gcpClient.ListCertificates(syncRequest); //TODO: How does this preform with load?

            int totalCertsProcessed = 0;
            int pagesProcessed = 0;
            foreach (var page in responseTask.AsRawResponses())
            {
                if (page.Count() == 0)
                {
                    Logger.Warn($"Incremental Sync Returned No Results");
                    continue;
                }

                int pageCertsProcessed = 0;
                do
                {
                    Certificate cloudCert = page.ElementAt(pageCertsProcessed);
                       
                    Logger.Trace($"Add {cloudCert.CertificateName.CertificateId} for processing");

                    CAConnectorCertificate caCert = new CAConnectorCertificate
                    {
                        CARequestID = cloudCert.CertificateName.CertificateId,//limited to 100 characters. use cert id only Required
                        CSR = cloudCert.PemCsr,
                        Certificate = cloudCert.PemCertificate,
                        Status = cloudCert.RevocationDetails is null ? 20 : 21,//required
                        SubmissionDate = cloudCert.CreateTime?.ToDateTime(),//Required
                        ResolutionDate = cloudCert.CreateTime?.ToDateTime(),
                        RevocationDate = cloudCert.RevocationDetails?.RevocationTime.ToDateTime(),
                        RevocationReason = cloudCert.RevocationDetails is null ? -1 : (int)cloudCert.RevocationDetails.RevocationState //assumes revocation reasons match Keyfactor
                    };

                    int blockedCount = 0;
                    if (blockingBuffer.TryAdd(caCert, 50, cancelToken))
                    {
                        if (blockedCount > 0) 
                        {
                            Logger.Warn($"Adding of {caCert.CARequestID} to queue was blocked. Took a total of {blockedCount} to process.");
                        }
                        pageCertsProcessed++;
                        totalCertsProcessed++;
                    }
                    else
                    {
                        blockedCount++;
                    }
       
                } while (pageCertsProcessed < page.Count());
                pagesProcessed++;
                Logger.Debug($"Completed processing of {pageCertsProcessed} certificates in page {pagesProcessed}");
            }
            Logger.Info($"Total Certificates Processed: {totalCertsProcessed} | Total Pages Processed: {pagesProcessed}");
        }

        public override void ValidateCAConnectionInfo(Dictionary<string, object> connectionInfo)
        {
            //connectionInfo is the currently imported values
            //CONFIG is the existing configuration from the initalize
            List<string> errors = new List<string>();

            Logger.Trace("Checking required CAConnection config");
            errors.AddRange(CheckRequiredValues(connectionInfo, PROJECT_ID_KEY, LOCATION_ID_KEY, CA_ID_KEY));

            Logger.Trace("Checking permissions for JSON license file");
            errors.AddRange(CheckEnvrionmentVariables());

            if (errors.Any())
            {
                throw new Exception(String.Join("|", errors.ToArray()));
            }
        }
        /// <summary>
        /// Determines if the provided keys have been configured
        /// </summary>
        /// <param name="connectionInfo">CAConnection Details object from the AnyGateway Config JSON file</param>
        /// <param name="args">List of keys to validate</param>
        /// <returns></returns>
        private static List<string> CheckRequiredValues(Dictionary<string, object> connectionInfo, params string[] args)
        {
            List<string> errors = new List<string>();
            foreach (string s in args)
            {
                if (String.IsNullOrEmpty(connectionInfo[s] as string))
                    errors.Add($"{s} is a required value");
            }
            return errors;
        }

        /// <summary>
        /// Determines if the AnyGateway service can read from the GOOGLE_APPLICATION_CREDENTIALS machine envrionment variable and read the contents of the 
        /// file.
        /// </summary>
        /// <returns>List of error messages for items failing validation</returns>
        private static List<string> CheckEnvrionmentVariables()
        {
            List<string> errors = new List<string>();
            try
            {
                string envrionmentVariablePath = Environment.GetEnvironmentVariable(AUTH_ENV_VARIABLE_NAME, EnvironmentVariableTarget.Machine);
                if(String.IsNullOrEmpty(envrionmentVariablePath))
                    errors.Add($"{AUTH_ENV_VARIABLE_NAME} must be conifgured with a JSON credential file");

                if (!envrionmentVariablePath.IsFullPathReadable())
                    errors.Add($"Cannot read license file at {envrionmentVariablePath}");
            }
            catch (System.Security.SecurityException)
            {
                errors.Add($"Access denied to {AUTH_ENV_VARIABLE_NAME} at \"HKLM\\System\\CurrentControlSet\\Control\\Session Manager\\Environment\" registry key");
            }

            return errors;
        }


        public override void ValidateProductInfo(EnrollmentProductInfo productInfo, Dictionary<string, object> connectionInfo)
        {
            
        }
    }
}
