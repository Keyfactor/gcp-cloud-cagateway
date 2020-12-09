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
using Google.Cloud.Security.PrivateCA.V1Beta1;
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
        const string LIFETIME_KEY = "Lifetime";

        private CertificateAuthorityServiceClient GcpClient { get; set; }
        private ICAConnectorConfigProvider Config { get; set; }

        private string ProjectId { get; set; }
        private string LocationId { get; set; }
        private string CAId { get; set; }


        public override EnrollmentResult Enroll(ICertificateDataReader certificateDataReader, string csr, string subject, Dictionary<string, string[]> san, EnrollmentProductInfo productInfo, PKIConstants.X509.RequestFormat requestFormat, RequestUtilities.EnrollmentType enrollmentType)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            try
            {
                //TODO: Determine if enrollment type changes behavior of the call to the CA?

                GcpClient = CertificateAuthorityServiceClient.Create();
              
                var parentAsTypedName = CertificateAuthorityName.FromProjectLocationCertificateAuthority(ProjectId, LocationId, CAId);

                if (!int.TryParse(productInfo.ProductParameters[LIFETIME_KEY], out int lifetimeInDays))
                {
                    throw new ArgumentException($"Unable to parse certificate {LIFETIME_KEY} from Product Parameters for Product Id {productInfo.ProductID}");
                }

                var certificate = new Certificate()
                {
                    PemCsr = $"-----BEGIN NEW CERTIFICATE REQUEST-----\n{pemify(csr)}\n-----END NEW CERTIFICATE REQUEST-----",
                    Lifetime = Duration.FromTimeSpan(new TimeSpan(lifetimeInDays, 0, 0, 0, 0))
                };

                //TODO: https://googleapis.github.io/google-cloud-dotnet/docs/faq.html#how-can-i-trace-grpc-issues
                //GrpcEnvironment.SetLogger(new GcpLogger());

                DateTime now = DateTime.Now;
                var createCertificateRequest = new CreateCertificateRequest() { 
                    ParentAsCertificateAuthorityName = parentAsTypedName,
                    Certificate = certificate,
                    //RequestId="",//this needs to be durable between reties 
                    CertificateId = $"{now:yyyy}{now:MM}{now:dd}-{now:HH}{now:mm}{now:ss}"//ID is required for Enterprise tier CAs and ignored for other.  
                };
                              
                var response = GcpClient.CreateCertificate(createCertificateRequest);

                return new EnrollmentResult
                {
                    Status = 20,
                    CARequestID = response.CertificateName.CertificateId,
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

        public override CAConnectorCertificate GetSingleRecord(string caRequestID)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            try
            {
                GcpClient = CertificateAuthorityServiceClient.Create();
                var cloudCert = GcpClient.GetCertificate(new CertificateName(ProjectId, LocationId, CAId, caRequestID));

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

        public override void Initialize(ICAConnectorConfigProvider configProvider)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            try
            {
                Config = configProvider;
                ProjectId = Config.CAConnectionData[PROJECT_ID_KEY] as string;
                LocationId = Config.CAConnectionData[LOCATION_ID_KEY] as string;
                CAId = Config.CAConnectionData[CA_ID_KEY] as string;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        public override void Ping()
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);

        }

        public override int Revoke(string caRequestID, string hexSerialNumber, uint revocationReason)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            try
            {
                GcpClient = CertificateAuthorityServiceClient.Create();
                CertificateName certId = new CertificateName(ProjectId, LocationId, CAId, caRequestID);

                RevokeCertificateRequest request = new RevokeCertificateRequest()
                { 
                    CertificateName = certId,
                    Reason = (RevocationReason)revocationReason
                };

                Logger.Trace($"Revoking certificate id {certId}");
                var response = GcpClient.RevokeCertificate(request);
                return 21;
            }
            catch (RpcException gEx)
            {
                Logger.Error($"Unable to revoke certificate. Status Code: {gEx.StatusCode} | Status:{gEx.Status}");
                return -1;
            }
            catch (Exception ex)
            {
                Logger.Error($"Unable to revoke certificate. {ex.Message}");
                return -1;
            }
        }


        public override void Synchronize(ICertificateDataReader certificateDataReader,
                                         BlockingCollection<CAConnectorCertificate> blockingBuffer,
                                         CertificateAuthoritySyncInfo certificateAuthoritySyncInfo,
                                         CancellationToken cancelToken)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
            try
            {
                GcpClient = CertificateAuthorityServiceClient.Create();

                CertificateAuthorityName caName = CertificateAuthorityName.FromProjectLocationCertificateAuthority(ProjectId, LocationId, CAId);

                if (certificateAuthoritySyncInfo.DoFullSync)
                {
                    var ca = GcpClient.GetCertificateAuthority(caName);
                    ProcessCACertificateList(ca, blockingBuffer, cancelToken); 
                }

                ListCertificatesRequest syncRequest = new ListCertificatesRequest()
                {
                    ParentAsCertificateAuthorityName = caName,
                };

                if (!certificateAuthoritySyncInfo.DoFullSync)
                {
                    Timestamp lastSyncTime = certificateAuthoritySyncInfo.LastFullSync.Value.ToUniversalTime().ToTimestamp();
                    Logger.Trace($"Executing an incremental sync.  Filter list by update_time >= {lastSyncTime.ToDateTime().ToLocalTime()}");
                    syncRequest.Filter = $"update_time >= {lastSyncTime}";
                }

                var responseList = GcpClient.ListCertificates(syncRequest); //TODO: How does this perform with load?
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
                        Logger.Warn($"Adding of {caCert.CARequestID} to queue was blocked. Took a total of {blockedCount} to process.");
                    }
                    caCertsProcessed++;
                }
                else
                {
                    blockedCount++;
                }
            } while (caCertsProcessed >= 1);
        }

        public override void ValidateCAConnectionInfo(Dictionary<string, object> connectionInfo)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);
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

        public override void ValidateProductInfo(EnrollmentProductInfo productInfo, Dictionary<string, object> connectionInfo)
        {
            Logger.MethodEntry(ILogExtensions.MethodLogLevel.Debug);

        }

        #region Private Helper Methods

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
        /// <returns>List of error messages for items failing validation</returns>
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
        /// <returns></returns>
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

        private static Func<String, String> pemify = (ss => ss.Length <= 64 ? ss : ss.Substring(0, 64) + "\n" + pemify(ss.Substring(64)));

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
