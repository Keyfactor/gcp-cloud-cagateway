using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Threading;
using CAProxy.AnyGateway;
using CAProxy.AnyGateway.Interfaces;
using CAProxy.AnyGateway.Models;
using CAProxy.Common;
using CSS.PKI;
using Google.Api.Gax;
using Google.Cloud.Security.PrivateCA.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Keyfactor.Logging;
using Microsoft.Extensions.Logging;

namespace Keyfactor.AnyGateway.Google
{
    public class GoogleCAProxy : BaseCAConnector
    {
        private const string AuthEnvVariableName = "GOOGLE_APPLICATION_CREDENTIALS";
        private const string ProjectIdKey = "ProjectId";
        private const string LocationIdKey = "LocationId";
        private const string CaIdKey = "CAId";
        private const string CaPoolIdKey = "CAPoolId";
        private const string LifetimeKey = "Lifetime";
        private const string NoTemplateProductId = "Default";

        private static readonly ILogger Log = LogHandler.GetClassLogger<GoogleCAProxy>();

        private CertificateAuthorityServiceClient GcpClient { get; set; }
        private ICAConnectorConfigProvider Config { get; set; }

        /// <summary>
        ///     Project Location ID from the Google Cloud Console for the Private CA Project
        /// </summary>
        private string ProjectId { get; set; }

        /// <summary>
        ///     Location ID (i.e. us-east1) from the Google Cloud Console for the Private CA deployment
        /// </summary>
        private string LocationId { get; set; }

        /// <summary>
        ///     CA Resource ID from the Google Cloud Console for the Private CA to be monitored. To be marked obsolete at GA
        /// </summary>
        private string CaId { get; set; }

        /// <summary>
        ///     CA Pool Resource ID from the Google Cloud Console.  This will only be used in the V1 release
        /// </summary>
        private string CaPoolId { get; set; }

        /// <summary>
        ///     AnyGateway method to enroll for a certificate from Google CA
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
            Log.MethodEntry();

            try
            {
                GcpClient = BuildClient();
            }
            catch
            {
                Log.LogError("Failed to create GCP CAS client");
                throw;
            }

            int lifetimeInDays = 365; // Default value
            if (productInfo.ProductParameters.TryGetValue(LifetimeKey, out string lifetimeInDaysString))
            {
                if (!int.TryParse(lifetimeInDaysString, out lifetimeInDays))
                {
                    Log.LogWarning(
                        $"Unable to parse certificate {LifetimeKey} from Product Parameters for Product Id {productInfo.ProductID}. Using default value of 365 days.");
                }
            }
            else
            {
                Log.LogDebug(
                    $"LifetimeKey not found in Product Parameters for Product Id {productInfo.ProductID}. Using default value of 365 days.");
            }

            Log.LogDebug($"Configuring {typeof(Certificate)} for {subject} with {lifetimeInDays} days validity");
            Certificate certificate = new Certificate
            {
                PemCsr =
                    $"-----BEGIN NEW CERTIFICATE REQUEST-----\n{pemify(csr)}\n-----END NEW CERTIFICATE REQUEST-----",
                Lifetime = Duration.FromTimeSpan(new TimeSpan(lifetimeInDays, 0, 0, 0,
                    0)) //365 day default or defined by config
            };

            if (productInfo.ProductID == NoTemplateProductId)
            {
                Log.LogDebug(
                    $"{NoTemplateProductId} template selected - Certificate enrollment will defer to the baseline values and policy configured by the CA Pool.");
            }
            else
            {
                Log.LogDebug(
                    $"Configuring {typeof(Certificate)} with the {productInfo.ProductID} Certificate Template.");
                CertificateTemplateName template = new CertificateTemplateName(ProjectId, LocationId, productInfo.ProductID);
                certificate.CertificateTemplate = template.ToString();
            }

            DateTime now = DateTime.Now;
            CaPoolName caPoolAsTypedName = CaPoolName.FromProjectLocationCaPool(ProjectId, LocationId, CaPoolId);
            Log.LogDebug(
                $"Configuring {typeof(CreateCertificateRequest)} with the configured {typeof(Certificate)} to enroll {subject} with the {caPoolAsTypedName} CA Pool");
            CreateCertificateRequest createCertificateRequest = new CreateCertificateRequest
            {
                ParentAsCaPoolName = caPoolAsTypedName,
                Certificate = certificate,
                //RequestId="",//if used, this needs to be durable between reties 
                CertificateId =
                    $"{now:yyyy}{now:MM}{now:dd}-{now:HH}{now:mm}{now:ss}" //ID is required for Enterprise tier CAs and ignored for other.  
            };

            if (!string.IsNullOrEmpty(CaId))
            {
                Log.LogDebug(
                    $"CAConnection section contained a non-empty CAId - Certificate will be enrolled using the CA with ID {CaId}");
                createCertificateRequest.IssuingCertificateAuthorityId = CaId;
            }

            Certificate response;
            try
            {
                Log.LogDebug($"Submitting CreateCertificate RPC for {subject}");
                response = GcpClient.CreateCertificate(createCertificateRequest);
                Log.LogDebug($"RPC was successful - minted certificate with resource name {response.Name}");
            }
            catch (RpcException gEx)
            {
                string message =
                    $"Could not complete certificate enrollment. RPC was unsuccessful. Status Code: {gEx.StatusCode} | Detail: {gEx.Status.Detail}";
                Log.LogError(message);
                return new EnrollmentResult
                {
                    Status = 30,
                    StatusMessage = message
                };
            }
            catch (Exception ex)
            {
                string message = $"Exception caught - Could not complete certificate enrollment: {ex}";
                Log.LogError(message);
                return new EnrollmentResult
                {
                    Status = 30,
                    StatusMessage = message
                };
            }

            return new EnrollmentResult
            {
                Status = 20,
                CARequestID = response.CertificateName?.CertificateId,
                Certificate = response.PemCertificate
            };
        }

        /// <summary>
        ///     AnyGateway method to get a single certificate's detail from the CA
        /// </summary>
        /// <param name="caRequestID">CA Id returned during inital synchronization</param>
        /// <returns></returns>
        public override CAConnectorCertificate GetSingleRecord(string caRequestID)
        {
            Log.MethodEntry();
            try
            {
                GcpClient = BuildClient();
                Certificate cloudCert =
                    GcpClient.GetCertificate(new CertificateName(ProjectId, LocationId, CaPoolId, caRequestID));

                return ProcessCAResponse(cloudCert);
            }
            catch (RpcException gEx)
            {
                throw new Exception(
                    $"Could not retrieve certificate. Status Code: {gEx.StatusCode} | Detail: {gEx.Status.Detail}");
            }
            catch (Exception ex)
            {
                throw new Exception($"Could retrieve certificate. Detail: {ex.Message}");
            }
        }

        /// <summary>
        ///     AnyGateway method called before most AnyGateway functions
        /// </summary>
        /// <param name="configProvider">Existing configuration extracted from the AnyGateway database</param>
        public override void Initialize(ICAConnectorConfigProvider configProvider)
        {
            Log.MethodEntry();
            try
            {
                Config = configProvider;
                ProjectId = Config.CAConnectionData[ProjectIdKey] as string;
                LocationId = Config.CAConnectionData[LocationIdKey] as string;
                CaPoolId = Config.CAConnectionData[CaPoolIdKey] as string;
                CaId = Config.CAConnectionData[CaIdKey] as string;
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to initialize GCP CAS CAPlugin: {ex}");
            }
        }

        /// <summary>
        ///     Certutil response to the certutil -ping [-config host\logical] command
        /// </summary>
        public override void Ping()
        {
            Log.MethodEntry();
            Log.MethodExit();
        }

        /// <summary>
        ///     AnyGateway method to revoke a certificate
        /// </summary>
        /// <param name="caRequestID"></param>
        /// <param name="hexSerialNumber"></param>
        /// <param name="revocationReason"></param>
        /// <returns></returns>
        public override int Revoke(string caRequestID, string hexSerialNumber, uint revocationReason)
        {
            Log.MethodEntry();
            try
            {
                GcpClient = BuildClient();
                CertificateName certId = new CertificateName(ProjectId, LocationId, CaPoolId, caRequestID);

                RevokeCertificateRequest request = new RevokeCertificateRequest
                {
                    CertificateName = certId,
                    Reason = (RevocationReason)revocationReason
                };

                Log.LogTrace($"Revoking certificate id {certId}");
                Certificate response = GcpClient.RevokeCertificate(request);
                return Convert.ToInt32(PKIConstants.Microsoft.RequestDisposition.REVOKED);
                ;
            }
            catch (RpcException gEx)
            {
                Log.LogError($"Unable to revoke certificate. Status Code: {gEx.StatusCode} | Status:{gEx.Status}");
                throw gEx;
            }
            catch (Exception ex)
            {
                Log.LogError($"Unable to revoke certificate. {ex.Message}");
                throw ex;
            }
        }

        /// <summary>
        ///     AnyGateway method to syncronize Google CA Certificates
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
            Log.MethodEntry();
            try
            {
                GcpClient = BuildClient();

                //For sync we still need to specify the CA ID since the pool will not provide a list of certs.  
                //Do we have a CA in Keyfactor for each even thought issuance and revocation will be pool level? Probably
                CertificateAuthorityName caName =
                    CertificateAuthorityName.FromProjectLocationCaPoolCertificateAuthority(ProjectId, LocationId,
                        CaPoolId, CaId);
                if (certificateAuthoritySyncInfo.DoFullSync)
                {
                    CertificateAuthority ca = GcpClient.GetCertificateAuthority(caName);
                    ProcessCACertificateList(ca, blockingBuffer, cancelToken);
                }

                ListCertificatesRequest syncRequest = new ListCertificatesRequest
                {
                    ParentAsCaPoolName = CaPoolName.FromProjectLocationCaPool(ProjectId, LocationId, CaPoolId)
                };

                if (!certificateAuthoritySyncInfo.DoFullSync)
                {
                    Timestamp lastSyncTime =
                        certificateAuthoritySyncInfo.LastFullSync.Value.ToUniversalTime().ToTimestamp();
                    Log.LogTrace(
                        $"Executing an incremental sync.  Filter list by update_time >= {lastSyncTime.ToDateTime().ToLocalTime()}");
                    syncRequest.Filter = $"update_time >= {lastSyncTime}";
                }

                PagedEnumerable<ListCertificatesResponse, Certificate> responseList =
                    GcpClient.ListCertificates(syncRequest);
                ProcessCertificateList(responseList, blockingBuffer, cancelToken);
            }
            catch (RpcException gEx)
            {
                Log.LogError($"Unable to get CA Certificate List. {gEx.StatusCode} | {gEx.Status}");
            }
            catch (Exception ex)
            {
                Log.LogError($"Unhandled Exception: {ex}");
            }
        }

        /// <summary>
        ///     AnyGateway method to validate connection detail (CAConnection section) during the Set-KeyfactorGatewayConfig cmdlet
        /// </summary>
        /// <param name="connectionInfo">CAConnection section of the AnyGateway JSON file</param>
        public override void ValidateCAConnectionInfo(Dictionary<string, object> connectionInfo)
        {
            Log.MethodEntry();
            List<string> errors = new List<string>();

            Log.LogTrace("Checking required CAConnection config");
            errors.AddRange(CheckRequiredValues(connectionInfo, ProjectIdKey, LocationIdKey, CaPoolIdKey));

            Log.LogTrace("Checking permissions for JSON license file");
            errors.AddRange(CheckEnvrionmentVariables());

            Log.LogTrace("Checking connectivity and CA type");
            errors.AddRange(CheckCAConfig(connectionInfo));

            if (errors.Any()) throw new Exception(string.Join("|", errors.ToArray()));
        }

        /// <summary>
        ///     AnyGateway method to validate product info (Template section) during the Set-KeyfactorGatewayConfig cmdlet
        /// </summary>
        /// <param name="productInfo">Parameters section of the AnyGateway JSON file</param>
        /// <param name="connectionInfo">CAConnection section of the AnyGateway JSON file</param>
        public override void ValidateProductInfo(EnrollmentProductInfo productInfo,
            Dictionary<string, object> connectionInfo)
        {
            Log.MethodEntry();
            //TODO: Evaluate Template (if avaiable) based on ProductInfo
            //https://cloud.google.com/certificate-authority-service/docs/reference/rest/v1/projects.locations.certificateTemplates#CertificateTemplate
            Log.MethodExit();
        }

        #region Private Helper Methods

        /// <summary>
        ///     Method to process Issued certificates from the Goocle CA
        /// </summary>
        /// <param name="responseList">
        ///     <see cref="ListCertificatesResponse" /> from a full or incremental sync request to the
        ///     Google CA
        /// </param>
        /// <param name="blockingBuffer"></param>
        /// <param name="cancelToken"></param>
        private void ProcessCertificateList(PagedEnumerable<ListCertificatesResponse, Certificate> responseList,
            BlockingCollection<CAConnectorCertificate> blockingBuffer, CancellationToken cancelToken)
        {
            Log.MethodEntry();
            int totalCertsProcessed = 0;
            int pagesProcessed = 0;
            foreach (ListCertificatesResponse page in responseList.AsRawResponses())
            {
                if (page.Count() == 0)
                {
                    Log.LogWarning("Incremental Sync Returned No Results");
                    continue;
                }

                int pageCertsProcessed = 0;
                do
                {
                    Certificate cloudCert = page.ElementAt(pageCertsProcessed);

                    Log.LogTrace($"Add {cloudCert.CertificateName.CertificateId} for processing");

                    CAConnectorCertificate caCert = ProcessCAResponse(cloudCert);

                    int blockedCount = 0;
                    if (blockingBuffer.TryAdd(caCert, 50, cancelToken))
                    {
                        if (blockedCount > 0)
                            Log.LogWarning(
                                $"Adding of {caCert.CARequestID} to queue was blocked. Took a total of {blockedCount} tries to process.");
                        pageCertsProcessed++;
                        totalCertsProcessed++;
                    }
                    else
                    {
                        blockedCount++;
                    }
                } while (pageCertsProcessed < page.Count());

                pagesProcessed++;
                Log.LogDebug($"Completed processing of {pageCertsProcessed} certificates in page {pagesProcessed}");
            }

            Log.LogInformation(
                $"Total Certificates Processed: {totalCertsProcessed} | Total Pages Processed: {pagesProcessed}");
        }

        /// <summary>
        ///     Method to process the Issuing Certificate of a Google CA
        /// </summary>
        /// <param name="ca"><see cref="CertificateAuthority" /> to process certificate from</param>
        /// <param name="blockingBuffer">BlockingCollection provided by the Command platform for syncing CA certificates</param>
        /// <param name="cancelToken"></param>
        private void ProcessCACertificateList(CertificateAuthority ca,
            BlockingCollection<CAConnectorCertificate> blockingBuffer, CancellationToken cancelToken)
        {
            Log.MethodEntry();
            int caCertsProcessed = 0;
            do
            {
                string caPemCert = ca.PemCaCertificates.ElementAt(caCertsProcessed);

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
                        Log.LogWarning(
                            $"Adding of {caCert.CARequestID} to queue was blocked. Took a total of {blockedCount} tries to process.");
                    caCertsProcessed++;
                }
                else
                {
                    blockedCount++;
                }
            } while (caCertsProcessed < ca.PemCaCertificates.Count - 1);
        }

        private static IEnumerable<string> ValidateCaPool(Dictionary<string, object> connectionInfo)
        {
            List<string> returnValue = new List<string>();
            try
            {
                Log.LogDebug($"Validating that service account can access CA Pool with ID {connectionInfo[CaPoolIdKey] as string}");
                CaPool caPool = BuildClient().GetCaPool(new GetCaPoolRequest()
                {
                    CaPoolName = CaPoolName.FromProjectLocationCaPool(
                        connectionInfo[ProjectIdKey] as string,
                        connectionInfo[LocationIdKey] as string,
                        connectionInfo[CaPoolIdKey] as string
                        )
                });

                if (caPool.Tier == CaPool.Types.Tier.Devops)
                {
                    string message = $"{caPool.Tier} is an unsupported CA configuration";
                    Log.LogError(message);
                    returnValue.Add(message);
                }
            }
            catch (RpcException gEx)
            {
                string message = $"Unable to connect to CA Pool. Status Code: {gEx.StatusCode} | Status: {gEx.Status}";
                Log.LogError(message);
                returnValue.Add(message);
            }
            catch (Exception ex)
            {
                string message = $"Unable to connect to CA. Detail: {ex.Message}";
                Log.LogError(message);
                returnValue.Add(message);
            }

            return returnValue;
        }

        private static IEnumerable<string> ValidateCa(Dictionary<string, object> connectionInfo)
        {
            List<string> returnValue = new List<string>();
            try
            {
                Log.LogDebug($"Validating that service account can access CA with ID {connectionInfo[CaIdKey] as string}");
                CertificateAuthority ca = BuildClient().GetCertificateAuthority(new GetCertificateAuthorityRequest
                {
                    CertificateAuthorityName = CertificateAuthorityName.FromProjectLocationCaPoolCertificateAuthority(
                        connectionInfo[ProjectIdKey] as string,
                        connectionInfo[LocationIdKey] as string,
                        connectionInfo[CaPoolIdKey] as string,
                        connectionInfo[CaIdKey] as string
                    )
                });

                if (ca.Tier == CaPool.Types.Tier.Devops)
                {
                    string message = $"{ca.Tier} is an unsupported CA configuration";
                    Log.LogError(message);
                    returnValue.Add(message);
                }
            }
            catch (RpcException gEx)
            {
                string message = $"Unable to connect to CA. Status Code: {gEx.StatusCode} | Status: {gEx.Status}";
                Log.LogError(message);
                returnValue.Add(message);
            }
            catch (Exception ex)
            {
                string message = $"Unable to connect to CA. Detail: {ex.Message}";
                Log.LogError(message);
                returnValue.Add(message);
            }

            return returnValue;
        }
        /// <summary>
        ///     Validate CA Configuration by attempting to connect and validate <see cref="CertificateAuthority.Tier" />
        /// </summary>
        /// <param name="connectionInfo">CAConnection Details object from the AnyGateway Config JSON file</param>
        /// <returns></returns>
        private static IEnumerable<string> CheckCAConfig(Dictionary<string, object> connectionInfo)
        {
            if (connectionInfo.TryGetValue(CaIdKey, out object id) && !string.IsNullOrEmpty(id as string))
            {
                Log.LogDebug($"GCP CAS CAProxy configured with a non-empty {CaIdKey} - validating that {id as string} exists.");
                return ValidateCa(connectionInfo);
            }

            Log.LogDebug($"GCP CAS CAProxy configured with an empty or non-existant {CaIdKey} - validating that CA pool exists.");
            return ValidateCaPool(connectionInfo);
        }

        /// <summary>
        ///     Determines if the provided keys have been configured
        /// </summary>
        /// <param name="connectionInfo">CAConnection Details object from the AnyGateway Config JSON file</param>
        /// <param name="args">List of keys to validate</param>
        /// <returns>List of error messages for items failing validation</returns>
        private static List<string> CheckRequiredValues(Dictionary<string, object> connectionInfo, params string[] args)
        {
            List<string> errors = new List<string>();
            foreach (string s in args)
                if (string.IsNullOrEmpty(connectionInfo[s] as string))
                    errors.Add($"{s} is a required value");
            return errors;
        }

        /// <summary>
        ///     Determines if the AnyGateway service can read from the GOOGLE_APPLICATION_CREDENTIALS machine envrionment variable
        ///     and read the contents of the
        ///     file.
        /// </summary>
        /// <returns><see cref="List{string}" /> the contains any error messages for items failing validation</returns>
        private static List<string> CheckEnvrionmentVariables()
        {
            List<string> errors = new List<string>();
            try
            {
                string envrionmentVariablePath =
                    Environment.GetEnvironmentVariable(AuthEnvVariableName, EnvironmentVariableTarget.Machine);
                if (string.IsNullOrEmpty(envrionmentVariablePath))
                {
                    string message = $"{AuthEnvVariableName} must be conifgured with a JSON credential file";
                    Log.LogError(message);
                    errors.Add(message);
                }

                if (!envrionmentVariablePath.IsFullPathReadable())
                {
                    string message = $"Cannot read license file at {envrionmentVariablePath}";
                    Log.LogError(message);
                    errors.Add(message);
                }
            }
            catch (SecurityException)
            {
                string message =
                    $"Access denied to {AuthEnvVariableName} at \"HKLM\\System\\CurrentControlSet\\Control\\Session Manager\\Environment\" registry key";
                Log.LogError(message);
                errors.Add(message);
            }

            return errors;
        }

        /// <summary>
        ///     Creates a Keyfactor AnyGateway Certificate Type from the GCP Certificate Type
        /// </summary>
        /// <param name="caCertificate"></param>
        /// <returns><see cref="CAConnectorCertificate" /> parsed from a <see cref="Certificate" /> object</returns>
        private CAConnectorCertificate ProcessCAResponse(Certificate caCertificate)
        {
            Log.MethodEntry();
            return new CAConnectorCertificate
            {
                CARequestID =
                    caCertificate.CertificateName.CertificateId, //limited to 100 characters. use cert id only Required
                CSR = caCertificate.PemCsr,
                Certificate = caCertificate.PemCertificate,
                Status = caCertificate.RevocationDetails is null ? 20 : 21, //required
                SubmissionDate = caCertificate.CreateTime?.ToDateTime(), //Required
                ResolutionDate = caCertificate.CreateTime?.ToDateTime(),
                RevocationDate = caCertificate.RevocationDetails?.RevocationTime.ToDateTime(),
                RevocationReason = caCertificate.RevocationDetails is null
                    ? -1
                    : (int)caCertificate.RevocationDetails.RevocationState //assumes revocation reasons match Keyfactor
            };
        }

        /// <summary>
        ///     Add new line every 64 characters to propertly format a base64 string as PEM
        /// </summary>
        private static readonly Func<string, string> pemify = ss =>
            ss.Length <= 64 ? ss : ss.Substring(0, 64) + "\n" + pemify(ss.Substring(64));

        /// <summary>
        ///     Build a new instance of the CertificateAuthorityServiceClient with explict credentials from the Server Envrionment
        ///     Variable
        /// </summary>
        /// <returns></returns>
        private static CertificateAuthorityServiceClient BuildClient()
        {
            CertificateAuthorityServiceClientBuilder caClient = new CertificateAuthorityServiceClientBuilder
            {
                CredentialsPath =
                    Environment.GetEnvironmentVariable(AuthEnvVariableName, EnvironmentVariableTarget.Machine)
            };
            return caClient.Build();
        }

        #endregion

        #region Obsolete Methods

        [Obsolete]
        public override EnrollmentResult Enroll(string csr, string subject, Dictionary<string, string[]> san,
            EnrollmentProductInfo productInfo, PKIConstants.X509.RequestFormat requestFormat,
            RequestUtilities.EnrollmentType enrollmentType)
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