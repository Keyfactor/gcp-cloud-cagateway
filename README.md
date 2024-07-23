<h1 align="center" style="border-bottom: none">
    Google Cloud CA
</h1>

<p align="center">
  <!-- Badges -->
<img src="https://img.shields.io/badge/integration_status-production-3D1973?style=flat-square" alt="Integration Status: production" />
<a href="https://github.com/Keyfactor/gcp-cloud-cagateway/releases"><img src="https://img.shields.io/github/v/release/Keyfactor/gcp-cloud-cagateway?style=flat-square" alt="Release" /></a>
<img src="https://img.shields.io/github/issues/Keyfactor/gcp-cloud-cagateway?style=flat-square" alt="Issues" />
<img src="https://img.shields.io/github/downloads/Keyfactor/gcp-cloud-cagateway/total?style=flat-square&label=downloads&color=28B905" alt="GitHub Downloads (all assets, all releases)" />
</p>

<p align="center">
  <!-- TOC -->
  <a href="#support">
    <b>Support</b>
  </a> 
  ·
  <a href="#license">
    <b>License</b>
  </a>
  ·
  <a href="https://github.com/orgs/Keyfactor/repositories?q=cagateway">
    <b>Related Integrations</b>
  </a>
</p>

## Support
The Google Cloud CA is open source and there is **no SLA**. Keyfactor will address issues as resources become available. Keyfactor customers may request escalation by opening up a support ticket through their Keyfactor representative. 

> To report a problem or suggest a new feature, use the **[Issues](../../issues)** tab. If you want to contribute actual bug fixes or proposed enhancements, use the **[Pull requests](../../pulls)** tab.# Notes
See the Google website for details on the [Google Certificate Authority Service](https://cloud.google.com/certificate-authority-service)

It should be noted that currently, due to the design of the DevOps tier of CA, Enterprise tier CAs are only supported by the AnyGateway.

# Compatibility
This AnyGateway is designed to be used with version 21.3.2 of the Keyfactor AnyGateway Framework

# Prerequisites

## [Authentication](https://cloud.google.com/docs/authentication/production)
A JSON file generated for a Google Service Account will need to be created and placed on the AnyGateway Server.
The path of this file into the GOOGLE_APPLICATION_CREDENTIALS environment variable to be used during a CA session.
Since the AnyGateway is required to run as the Network Service account, the registry will need to be modified to provide the service access to the Environment variable above.
The GOOGLE_APPLICATION_CREDENTIALS variable should be placed in the following registry location and read access provided:

* HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment

## [Authorization](https://cloud.google.com/certificate-authority-service/docs/reference/permissions-and-roles)
Currently the only method supported for authentication is a Service Credential with the following IAM authorizations. Any built in role with the below authorizations will function as well. 
* privateca.caPools.list
* privateca.caPools.get
* privateca.certificateTemplates.list
* privateca.certificateTemplates.get
* privateca.certificateTemplates.use
* privateca.certificateAuthorities.list
* privateca.certificateAuthorities.get
* privateca.certificates.create
* privateca.certificates.get
* privateca.certificates.list
* privateca.certificates.update
* privateca.reusableConfigs.get
* privateca.reusableConfigs.list
* resourcemanager.projects.get

## Certificate Chain
In order to enroll for certificates the Keyfactor Command server must trust the Private CA chain.  Once you create your Root and/or Subordinate CA, make sure to import the certificate chain into the Command Server certificate store

# Install
* Install the AnyCA Gateway Framework using the MSI from the Software Download Portal

* Download latest successful build from [GitHub](https://github.com/Keyfactor/google-cloud-cagateway)

* Copy *.dll to the Program Files\Keyfactor\ Keyfactor AnyGateway directory

* Update the CAProxyServer.config file
  * Update the CAConnection section to point at the GoogleCAProxy class
  ```xml
  <alias alias="CAConnector" type="Keyfactor.AnyGateway.Google.GoogleCAProxy, GoogleCAProxy"/>
  ```
  * Depending on the version of the AnyCA Gateway installed, additional binding redirects may need to be applied from the app.config. These redirections will be added to the CAProxyServer.config file

# Configuration
The following sections will breakdown the required configurations for the AnyGatewayConfig.json file that will be imported to configure the Google CA. 

## Templates
The Google CA has introduced the concept of Templates for the V1 release.
The product ID mapped below must be the Template Name from the cloud console.
The API does not provide certificate lifetime information, but any value can be placed here.
If the value is over the configured value, the Google CA will set to the maximum value as determined by the template configuration. 
 ```json
   "Templates": {
    "GoogleCAWebServer": {
      "ProductID": "", /*Value not used, so set to empty string. 'ProductID' element must be present.*/
      "Parameters": {
        "Lifetime": "300",/*days*/
      }
    }
}
 ```

## Security
The security section does not change specifically for the Google CA. Refer to the Keyfactor AnyGateway Documentation for more detail
```json
  /*Grant permissions on the CA to users or groups in the local domain.
	READ: Enumerate and read contents of certificates.
	ENROLL: Request certificates from the CA.
	OFFICER: Perform certificate functions such as issuance and revocation. This is equivalent to "Issue and Manage" permission on the Microsoft CA.
	ADMINISTRATOR: Configure/reconfigure the gateway.
	Valid permission settings are "Allow", "None", and "Deny".*/
    "Security": {
        "Keyfactor\\Administrator": {
            "READ": "Allow",
            "ENROLL": "Allow",
            "OFFICER": "Allow",
            "ADMINISTRATOR": "Allow"
        },
        "Keyfactor\\gateway_test": {
            "READ": "Allow",
            "ENROLL": "Allow",
            "OFFICER": "Allow",
            "ADMINISTRATOR": "Allow"
        },		
        "Keyfactor\\SVC_TimerService": {
            "READ": "Allow",
            "ENROLL": "Allow",
            "OFFICER": "Allow",
            "ADMINISTRATOR": "None"
        },
        "Keyfactor\\SVC_AppPool": {
            "READ": "Allow",
            "ENROLL": "Allow",
            "OFFICER": "Allow",
            "ADMINISTRATOR": "Allow"
        }
    }
```

## CerificateManagers
The Certificate Managers section is optional.
	If configured, all users or groups granted OFFICER permissions under the Security section
	must be configured for at least one Template and one Requester. 
	Uses "<All>" to specify all templates. Uses "Everyone" to specify all requesters.
	Valid permission values are "Allow" and "Deny".
```json
  "CertificateManagers":{
		"DOMAIN\\Username":{
			"Templates":{
				"MyTemplateShortName":{
					"Requesters":{
						"Everyone":"Allow",
						"DOMAIN\\Groupname":"Deny"
					}
				},
				"<All>":{
					"Requesters":{
						"Everyone":"Allow"
					}
				}
			}
		}
	}
```

## CAConnection
The CA Connection section will determine which CA in the Google cloud is integrated with Keyfactor. There are 3 required configuration fields
* ProjectId  
This is the Resource ID of the project that contains the Google CA Service
* LocationId  
This is the resource ID of the geographical location (i.e. us-east1) within the Google Cloud
* CAId  
This is the resource Id of any one of the CAs created in the CA pool using the [Google Cloud Console](https://console.cloud.google.com)
* CAPoolId
This is the resource id of the CA Pool created using the [Google Cloud Console](https://console.cloud.google.com)

```json
"CAConnection": {
    "ProjectId": "concise-frame-296019",
    "LocationId": "us-east1",
    "CAId":"ca-enterprise-subordinate-sandbox-tls",
    "CAPoolId":"gcp-test-pool"
}
```

## GatewayRegistration
There are no Google Specific Changes for the GatewayRegistration section. Refer to the Keyfactor AnyGateway Documentation for more detail on required changed to support the AnyCA Gateway
```json
  "GatewayRegistration": {
    "LogicalName": "GoogleCASandbox",
    "GatewayCertificate": {
      "StoreName": "CA",
      "StoreLocation": "LocalMachine",
      "Thumbprint": "bc6d6b168ce5c08a690c15e03be596bbaa095ebf"
    }
  }
```

## ServiceSettings
There are no Google Specific Changes for the GatewayRegistration section. Refer to the Keyfactor AnyGateway Documentation for more detail on required changed to support the AnyCA Gateway
```json
  "ServiceSettings": {
    "ViewIdleMinutes": 8,
    "FullScanPeriodHours": 1,
	"PartialScanPeriodMinutes": 60
  }
```



## License

Apache License 2.0, see [LICENSE](LICENSE).

## Related Integrations

See all [Keyfactor CA Gateways](https://github.com/orgs/Keyfactor/repositories?q=cagateway).