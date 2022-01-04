using CAProxy.AnyGateway;
using CAProxy.AnyGateway.Interfaces;
using CAProxy.AnyGateway.Models;
using CAProxy.Common;
using CSS.PKI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
//TODO Update for V1
using Google.Cloud.Security.PrivateCA.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Google.Api.Gax;
using CSS.Common.Logging;


namespace Keyfactor.AnyGateway.Google
{
    public class GoogleCAProxy : BaseCAConnector
    {
        const string AUTH_ENV_VARIABLE_NAME = "GOOGLE_APPLICATION_CREDENTIALS";
        const string PROJECT_ID_KEY = "ProjectId";
        const string LOCATION_ID_KEY = "LocationId";
        const string CA_ID_KEY = "CAId";
        const string CA_POOL_ID_KEY = "CAPoolId";
        const string LIFETIME_KEY = "Lifetime";

        private CertificateAuthorityServiceClient GcpClient { get; set; }
        private ICAConnectorConfigProvider Config { get; set; }

        /// <summary>
        /// Project Location ID from the Google Cloud Console for the Private CA Project
        /// </summary>
        private string ProjectId { get; set; }
        /// <summary>
        /// Location ID (i.e. us-east1) from the Google Cloud Console for the Private CA deployment
        /// </summary>
        private string LocationId { get; set; }
        /// <summary>
        /// CA Resource ID from the Google Cloud Console for the Private CA to be monitored. To be marked obsolete at GA
        /// </summary>
        private string CAId { get; set; }
        /// <summary>
        /// CA Pool Resource ID from the Google Cloud Console.  This will only be used in the V1 release
        /// </summary>
        private string CAPoolId { get; set; }

        /// <summary>
        /// AnyGateway method to enroll for a certificate from Google CA
        /// </summary>
        /// <param name="certificateDataReader">Database access to existing CA Certificates</param>
        /// <param name="csr">base64 encoded string of the Certificate Request</param>
        /// <param name="subject">Distinguised name based on the CST</param>
        /// <param name="san">dns and/or ip SAN entries</param>
        /// <param name="productInfo">Request Attributes and Product parameters from AnyGateway Config JSON file</param>
        /// <param name="requestFormat"></param>
        /// <param name="enrollmentType"></param>
        /// <returns></returns>
        public override EnrollmentResult Enroll(ICertificateDataReader certificateDataReader, 
                                                string csr, 
                                                string subject, 
                                                Dictionary<string, string[]> san, 
                                                EnrollmentProductInfo productInfo, 
                                                PKIConstants.X509.RequestFormat requestFormat, 
                                                RequestUtilities.EnrollmentType enrollmentType)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            try
            {

                GcpClient = BuildClient();
                
                //var template = GcpClient.GetCertificateTemplate(productInfo.ProductID);
                
                //Logger.Trace($"Template {template.Name} found for enrollment");              
              
                var caPoolAsTypedName = CaPoolName.FromProjectLocationCaPool(ProjectId, LocationId, CAPoolId);

                Logger.Trace($"Enroll at CA Pool: {caPoolAsTypedName}");              

                if (!int.TryParse(productInfo.ProductParameters[LIFETIME_KEY], out int lifetimeInDays))
                {
                    Logger.Warn($"Unable to parse certificate {LIFETIME_KEY} from Product Parameters for Product Id {productInfo.ProductID}. Set Lifetime to 365 days.");
                    lifetimeInDays = 365;
                }
                Logger.Trace($"Submit Certificate Request for {subject} with {lifetimeInDays} days validity");
                var certificate = new Certificate()
                {
                    PemCsr = $"-----BEGIN NEW CERTIFICATE REQUEST-----\n{pemify(csr)}\n-----END NEW CERTIFICATE REQUEST-----",
                    Lifetime = Duration.FromTimeSpan(new TimeSpan(lifetimeInDays, 0, 0, 0, 0))//365 day default or defined by config
                };

                DateTime now = DateTime.Now;
                var createCertificateRequest = new CreateCertificateRequest() { 
                    ParentAsCaPoolName = caPoolAsTypedName,
                    Certificate = certificate,
                    //RequestId="",//if used, this needs to be durable between reties 
                    CertificateId = $"{now:yyyy}{now:MM}{now:dd}-{now:HH}{now:mm}{now:ss}"//ID is required for Enterprise tier CAs and ignored for other.  
                };

                var response = GcpClient.CreateCertificate(createCertificateRequest);

                return new EnrollmentResult
                {
                    Status = 20,
                    CARequestID = response.CertificateName?.CertificateId,
                    Certificate = response.PemCertificate
                };
            }
            catch (RpcException gEx)
            {
                return new EnrollmentResult
                {
                    Status = 30,
                    StatusMessage = $"Could not complete certificate enrollment. Status Code: {gEx.StatusCode} | Detail: {gEx.Status.Detail}"
                };
            }
            catch (Exception ex)
            {
                return new EnrollmentResult { 
                    Status=30,
                    StatusMessage = $"Could not complete certificate enrollment. {ex.Message}"
                };
            }
        }
        /// <summary>
        /// AnyGateway method to get a single certificate's detail from the CA
        /// </summary>
        /// <param name="caRequestID">CA Id returned during inital synchronization</param>
        /// <returns></returns>
        public override CAConnectorCertificate GetSingleRecord(string caRequestID)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            try
            {
                GcpClient = BuildClient();
                var cloudCert = GcpClient.GetCertificate(new CertificateName(ProjectId, LocationId, CAPoolId, caRequestID));

                return ProcessCAResponse(cloudCert);
            }
            catch (RpcException gEx)
            {
                throw new Exception($"Could not retrieve certificate. Status Code: {gEx.StatusCode} | Detail: {gEx.Status.Detail}");
            }
            catch (Exception ex)
            {
                throw new Exception($"Could retrieve certificate. Detail: {ex.Message}");
            }
        }

        /// <summary>
        /// AnyGateway method called before most AnyGateway functions
        /// </summary>
        /// <param name="configProvider">Existing configuration extracted from the AnyGateway database</param>
        public override void Initialize(ICAConnectorConfigProvider configProvider)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            try
            {
                Config = configProvider;
                ProjectId = Config.CAConnectionData[PROJECT_ID_KEY] as string;
                LocationId = Config.CAConnectionData[LOCATION_ID_KEY] as string;
                CAPoolId = Config.CAConnectionData[CA_POOL_ID_KEY] as string;
                CAId = Config.CAConnectionData[CA_ID_KEY] as string;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        /// <summary>
        /// Certutil response to the certutil -ping [-config host\logical] command
        /// </summary>
        public override void Ping()
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            Logger.MethodExit(ILogExtensions.MethodLogLevel.Debug);

        }

        /// <summary>
        /// AnyGateway method to revoke a certificate
        /// </summary>
        /// <param name="caRequestID"></param>
        /// <param name="hexSerialNumber"></param>
        /// <param name="revocationReason"></param>
        /// <returns></returns>
        public override int Revoke(string caRequestID, string hexSerialNumber, uint revocationReason)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            try
            {
                GcpClient = BuildClient();
                CertificateName certId = new CertificateName(ProjectId, LocationId, CAPoolId, caRequestID);

                RevokeCertificateRequest request = new RevokeCertificateRequest()
                { 
                    CertificateName = certId,
                    Reason = (RevocationReason)revocationReason
                };

                Logger.Trace($"Revoking certificate id {certId}");
                var response = GcpClient.RevokeCertificate(request);
                return Convert.ToInt32(PKIConstants.Microsoft.RequestDisposition.REVOKED);
                ;
            }
            catch (RpcException gEx)
            {
                Logger.Error($"Unable to revoke certificate. Status Code: {gEx.StatusCode} | Status:{gEx.Status}");
                throw gEx;
            }
            catch (Exception ex)
            {
                Logger.Error($"Unable to revoke certificate. {ex.Message}");
                throw ex;
            }
        }

        /// <summary>
        /// AnyGateway method to syncronize Google CA Certificates
        /// </summary>
        /// <param name="certificateDataReader">Database access to the current certificates for the CA</param>
        /// <param name="blockingBuffer"></param>
        /// <param name="certificateAuthoritySyncInfo">Detail about the CA being synchronized</param>
        /// <param name="cancelToken"></param>
        public override void Synchronize(ICertificateDataReader certificateDataReader,
                                         BlockingCollection<CAConnectorCertificate> blockingBuffer,
                                         CertificateAuthoritySyncInfo certificateAuthoritySyncInfo,
                                         CancellationToken cancelToken)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            try
            {
                GcpClient = BuildClient();

                //For sync we still need to specify the CA ID since the pool will not provide a list of certs.  
                //Do we have a CA in Keyfactor for each even thought issuance and revocation will be pool level? Probably
                CertificateAuthorityName caName = CertificateAuthorityName.FromProjectLocationCaPoolCertificateAuthority(ProjectId, LocationId, CAPoolId, CAId);
                if (certificateAuthoritySyncInfo.DoFullSync)
                {
                    var ca = GcpClient.GetCertificateAuthority(caName);
                    ProcessCACertificateList(ca, blockingBuffer, cancelToken); 
                }

                ListCertificatesRequest syncRequest = new ListCertificatesRequest()
                {
                    ParentAsCaPoolName = CaPoolName.FromProjectLocationCaPool(ProjectId,LocationId,CAPoolId),
                };

                if (!certificateAuthoritySyncInfo.DoFullSync)
                {
                    Timestamp lastSyncTime = certificateAuthoritySyncInfo.LastFullSync.Value.ToUniversalTime().ToTimestamp();
                    Logger.Trace($"Executing an incremental sync.  Filter list by update_time >= {lastSyncTime.ToDateTime().ToLocalTime()}");
                    syncRequest.Filter = $"update_time >= {lastSyncTime}";
                }

                var responseList = GcpClient.ListCertificates(syncRequest); 
                ProcessCertificateList(responseList, blockingBuffer, cancelToken);
            }
            catch (RpcException gEx)
            {
                Logger.Error($"Unable to get CA Certificate List. {gEx.StatusCode} | {gEx.Status}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Unhandled Exception: {ex}");
            }
        }

        /// <summary>
        /// AnyGateway method to validate connection detail (CAConnection section) during the Set-KeyfactorGatewayConfig cmdlet
        /// </summary>
        /// <param name="connectionInfo">CAConnection section of the AnyGateway JSON file</param>
        public override void ValidateCAConnectionInfo(Dictionary<string, object> connectionInfo)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            //connectionInfo is the currently imported values
            //CONFIG is the existing configuration from the initalize
            List<string> errors = new List<string>();

            Logger.Trace("Checking required CAConnection config");
            errors.AddRange(CheckRequiredValues(connectionInfo, PROJECT_ID_KEY, LOCATION_ID_KEY, CA_POOL_ID_KEY, CA_ID_KEY));

            Logger.Trace("Checking permissions for JSON license file");
            errors.AddRange(CheckEnvrionmentVariables());

            Logger.Trace("Checking connectivity and CA type");
            errors.AddRange(CheckCAConfig(connectionInfo));

            if (errors.Any())
            {
                throw new Exception(String.Join("|", errors.ToArray()));
            }
        }

        /// <summary>
        /// AnyGateway method to validate product info (Template section) during the Set-KeyfactorGatewayConfig cmdlet
        /// </summary>
        /// <param name="productInfo">Parameters section of the AnyGateway JSON file</param>
        /// <param name="connectionInfo">CAConnection section of the AnyGateway JSON file</param>
        public override void ValidateProductInfo(EnrollmentProductInfo productInfo, Dictionary<string, object> connectionInfo)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            //TODO: Evaluate Template (if avaiable) based on ProductInfo
            //https://cloud.google.com/certificate-authority-service/docs/reference/rest/v1/projects.locations.certificateTemplates#CertificateTemplate
            Logger.MethodExit(ILogExtensions.MethodLogLevel.Debug);
        }

        #region Private Helper Methods
        /// <summary>
        /// Method to process Issued certificates from the Goocle CA
        /// </summary>
        /// <param name="responseList"><see cref="ListCertificatesResponse"/> from a full or incremental sync request to the Google CA </param>
        /// <param name="blockingBuffer"></param>
        /// <param name="cancelToken"></param>
        private void ProcessCertificateList(PagedEnumerable<ListCertificatesResponse, Certificate> responseList, BlockingCollection<CAConnectorCertificate> blockingBuffer, CancellationToken cancelToken)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            int totalCertsProcessed = 0;
            int pagesProcessed = 0;
            foreach (var page in responseList.AsRawResponses())
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

                    CAConnectorCertificate caCert = ProcessCAResponse(cloudCert);

                    int blockedCount = 0;
                    if (blockingBuffer.TryAdd(caCert, 50, cancelToken))
                    {
                        if (blockedCount > 0)
                        {
                            Logger.Warn($"Adding of {caCert.CARequestID} to queue was blocked. Took a total of {blockedCount} tries to process.");
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
        /// <summary>
        /// Method to process the Issuing Certificate of a Google CA
        /// </summary>
        /// <param name="ca"><see cref="CertificateAuthority"/> to process certificate from</param>
        /// <param name="blockingBuffer">BlockingCollection provided by the Command platform for syncing CA certificates</param>
        /// <param name="cancelToken"></param>
        private void ProcessCACertificateList(CertificateAuthority ca, BlockingCollection<CAConnectorCertificate> blockingBuffer, CancellationToken cancelToken)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            int caCertsProcessed = 0;
            do
            {
                var caPemCert = ca.PemCaCertificates.ElementAt(caCertsProcessed);

                CAConnectorCertificate caCert = new CAConnectorCertificate
                {
                    CARequestID = ca.CertificateAuthorityName.CertificateAuthorityId,
                    Certificate = caPemCert,
                    ResolutionDate = ca.CreateTime.ToDateTime(),
                    SubmissionDate = ca.CreateTime.ToDateTime(),
                    Status = 20
                };

                int blockedCount = 0;
                if (blockingBuffer.TryAdd(caCert, 50, cancelToken))
                {
                    if (blockedCount > 0)
                    {
                        Logger.Warn($"Adding of {caCert.CARequestID} to queue was blocked. Took a total of {blockedCount} tries to process.");
                    }
                    caCertsProcessed++;
                }
                else
                {
                    blockedCount++;
                }
            } while (caCertsProcessed < (ca.PemCaCertificates.Count - 1) );
        }

        /// <summary>
        /// Validate CA Configuration by attempting to connect and validate <see cref="CertificateAuthority.Tier"/>
        /// </summary>
        /// <param name="connectionInfo">CAConnection Details object from the AnyGateway Config JSON file</param>
        /// <returns></returns>
        private static IEnumerable<string> CheckCAConfig(Dictionary<string, object> connectionInfo)
        {
            List<string> returnValue = new List<string>();
            try
            {
                var ca = BuildClient().GetCertificateAuthority(new GetCertificateAuthorityRequest
                {
                    CertificateAuthorityName = CertificateAuthorityName.FromProjectLocationCaPoolCertificateAuthority(
                        connectionInfo[PROJECT_ID_KEY] as string,
                        connectionInfo[LOCATION_ID_KEY] as string,
                        connectionInfo[CA_POOL_ID_KEY] as string,
                        connectionInfo[CA_ID_KEY] as string
                        )
                });

               if (ca.Tier == CaPool.Types.Tier.Devops)
               {
                returnValue.Add($"{ca.Tier} is an unsupported CA configuration");
               }
            }
            catch (RpcException gEx)
            {
                returnValue.Add($"Unable to connect to CA. Status Code: {gEx.StatusCode} | Status: {gEx.Status}");
            }
            catch (Exception ex)
            {
                returnValue.Add($"Unable to connect to CA. Detail: {ex.Message}");
            }

            return returnValue;
        }

        /// <summary>
        /// Determines if the provided keys have been configured
        /// </summary>
        /// <param name="connectionInfo">CAConnection Details object from the AnyGateway Config JSON file</param>
        /// <param name="args">List of keys to validate</param>
        /// <returns>List of error messages for items failing validation</returns>
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
        /// <returns><see cref="List{string}"/> the contains any error messages for items failing validation</returns>
        private static List<string> CheckEnvrionmentVariables()
        {
            List<string> errors = new List<string>();
            try
            {
                string envrionmentVariablePath = Environment.GetEnvironmentVariable(AUTH_ENV_VARIABLE_NAME, EnvironmentVariableTarget.Machine);
                if (String.IsNullOrEmpty(envrionmentVariablePath))
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
        /// <summary>
        /// Creates a Keyfactor AnyGateway Certificate Type from the GCP Certificate Type
        /// </summary>
        /// <param name="caCertificate"></param>
        /// <returns><see cref="CAConnectorCertificate"/> parsed from a <see cref="Certificate"/> object</returns>
        private CAConnectorCertificate ProcessCAResponse(Certificate caCertificate)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            return new CAConnectorCertificate()
            {
                CARequestID = caCertificate.CertificateName.CertificateId,//limited to 100 characters. use cert id only Required
                CSR = caCertificate.PemCsr,
                Certificate = caCertificate.PemCertificate,
                Status = caCertificate.RevocationDetails is null ? 20 : 21,//required
                SubmissionDate = caCertificate.CreateTime?.ToDateTime(),//Required
                ResolutionDate = caCertificate.CreateTime?.ToDateTime(),
                RevocationDate = caCertificate.RevocationDetails?.RevocationTime.ToDateTime(),
                RevocationReason = caCertificate.RevocationDetails is null ? -1 : (int)caCertificate.RevocationDetails.RevocationState //assumes revocation reasons match Keyfactor
            };

        }

        /// <summary>
        /// Add new line every 64 characters to propertly format a base64 string as PEM
        /// </summary>
        private static Func<String, String> pemify = (ss => ss.Length <= 64 ? ss : ss.Substring(0, 64) + "\n" + pemify(ss.Substring(64)));
        
        /// <summary>
        /// Build a new instance of the CertificateAuthorityServiceClient with explict credentials from the Server Envrionment Variable
        /// </summary>
        /// <returns></returns>
        private static CertificateAuthorityServiceClient BuildClient()
        {
            var caClient = new CertificateAuthorityServiceClientBuilder
            {
                CredentialsPath = Environment.GetEnvironmentVariable(AUTH_ENV_VARIABLE_NAME, EnvironmentVariableTarget.Machine)
            };
            return caClient.Build();
        }


        #endregion

        #region Obsolete Methods
        [Obsolete]
        public override EnrollmentResult Enroll(string csr, string subject, Dictionary<string, string[]> san, EnrollmentProductInfo productInfo, CSS.PKI.PKIConstants.X509.RequestFormat requestFormat, RequestUtilities.EnrollmentType enrollmentType)
        {
            throw new NotImplementedException();
        }

        [Obsolete]
        public override void Synchronize(ICertificateDataReader certificateDataReader,
                                         BlockingCollection<CertificateRecord> blockingBuffer,
                                         CertificateAuthoritySyncInfo certificateAuthoritySyncInfo,
                                         CancellationToken cancelToken,
                                         string logicalName)
        {
            throw new NotImplementedException();
        }
        #endregion
    }
}
