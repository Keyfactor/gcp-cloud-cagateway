## Overview

The [Google Cloud Platform (GCP) CA Services (CAS)](https://cloud.google.com/security/products/certificate-authority-service) AnyCA Gateway DCOM plugin extends the capabilities of connected GCP CAS CAs to [Keyfactor Command](https://www.keyfactor.com/products/command/) via the Keyfactor AnyCA Gateway DCOM. The plugin represents a fully featured AnyCA DCOM Plugin with the following capabilies:

* CA Sync:
    * Download all certificates issued by connected Enterprise tier CAs in GCP CAS (full sync).
    * Download all certificates issued by connected Enterprise tier CAs in GCP CAS issued after a specified time (incremental sync).
* Certificate enrollment for all published GoDaddy Certificate SKUs:
    * Support certificate enrollment (new keys/certificate).
* Certificate revocation:
    * Request revocation of a previously issued certificate.

> The GCP CAS AnyCA Gateway DCOM plugin is **not** supported for [DevOps Tier](https://cloud.google.com/certificate-authority-service/docs/tiers) Certificate Authority Pools.
> 
> DevOps tier CA Pools don't offer listing, describing, or revoking certificates.

## Compatibility

This AnyGateway is designed to be used with version 24.2 of the Keyfactor AnyCA Gateway DCOM Framework.

## Requirements

### Application Default Credentials

The GCP CAS AnyCA Gateway DCOM plugin connects to and authenticates with GCP CAS implicitly using [Application Default Credentials](https://cloud.google.com/docs/authentication/application-default-credentials). This means that all authentication-related configuration of the GCP CAS AnyCA Gateway REST plugin is implied by the environment where the AnyCA Gateway REST itself is running.

Please refer to [Google's documentation](https://cloud.google.com/docs/authentication/provide-credentials-adc) to configure ADC on the server running the AnyCA Gateway REST.

> The easiest way to configure ADC for non-production environments is to use [User Credentials](https://cloud.google.com/docs/authentication/provide-credentials-adc#local-dev).
>
> For production environments that use an ADC method requiring the `GOOGLE_APPLICATION_CREDENTIALS` environment variable, you must ensure the following:
>
> 1. The service account that the AnyCA Gateway REST runs under must have read permission to the GCP credential JSON file.
> 2. You must set the `GOOGLE_APPLICATION_CREDENTIALS` environment variable for the Windows Service running the AnyCA Gateway REST using the [Windows registry editor](https://learn.microsoft.com/en-us/troubleshoot/windows-server/performance/windows-registry-advanced-users).
>     * Refer to the [HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment](https://learn.microsoft.com/en-us/windows/win32/procthread/environment-variables) docs.

If the selected ADC mechanism is [Service Account Key](https://cloud.google.com/docs/authentication/provide-credentials-adc#wlif-key), it's recommended that a [custom role is created](https://cloud.google.com/iam/docs/creating-custom-roles) that has the following minimum permissions:

* `privateca.certificateTemplates.list`
* `privateca.certificateTemplates.use`
* `privateca.certificateAuthorities.get`
* `privateca.certificates.create`
* `privateca.certificates.get`
* `privateca.certificates.list`
* `privateca.certificates.update`
* `privateca.caPools.get`

> The built-in CA Service Operation Manager `roles/privateca.caManager` role can also be used, but is more permissive than a custom role with the above permissions.

### Root CA Configuration

Both the Keyfactor Command and AnyCA Gateway DCOM servers must trust the root CA, and if applicable, any subordinate CAs for all features to work as intended. Download the CA Certificate (and chain, if applicable) from GCP [CAS](https://console.cloud.google.com/security/cas), and import them into the appropriate certificate store on the AnyCA Gateway DCOM server.

* **Windows** - The root CA and applicable subordinate CAs must be imported into the Windows certificate store. The certificates can be imported using the Microsoft Management Console (MMC) or PowerShell. 
    * Certificates can be imported in MMC by "File" -> "Add/Remove Snap-in" -> "Certificates" -> "Add >" -> "Computer account" -> "Local computer".
    * Root CAs must go in the `Trusted Root Certification Authorities` certificate store.
    * Subordinate CAs must go in the `Intermediate Certification Authorities` certificate store.

> If the Root CA and chain are not known by the server hosting the AnyCA Gateway DCOM, the certificate chain _may not_ be returned to Command in certificate enrollment requests.

### Template Identification

The GCP CAS AnyCA Gateway DCOM plugin supports [GCP CAS Certificate Templates](https://cloud.google.com/certificate-authority-service/docs/policy-controls). Certificate Templates exist at the Project level in GCP. Before installing the plugin, identify the [Certificate Templates](https://console.cloud.google.com/security/cas) that you want to make available to Keyfactor Command and [create Certificate Templates in AD](https://software.keyfactor.com/Guides/AnyGateway_Generic/Content/AnyGateway/Preparing_Templates.htm).

> Certificate Templates in GCP are not required. The plugin will not specify a template for the [CreateCertificate RPC](https://cloud.google.com/certificate-authority-service/docs/reference/rpc/google.cloud.security.privateca.v1#google.cloud.security.privateca.v1.CertificateAuthorityService.CreateCertificate) if the `ProductId` (discussed later) is set to `Default`.

## Installation

1. Install AnyCA Gateway DCOM v24.2 per the [official Keyfactor documentation](https://software.keyfactor.com/Guides/AnyGateway_Generic/Content/AnyGateway/Introduction.htm).

2. Download the [latest GCP CAS AnyCA Gateway DCOM plugin assemblies](https://github.com/Keyfactor/gcp-cloud-cagateway/releases/latest).

3. Copy `*.dll` to the `C:\Program Files\Keyfactor\Keyfactor AnyGateway` directory.

4. Update the `CAProxyServer.config` file.
    1. Update the `$.configuration.unity.CAConnector` section to point at the `GoogleCAProxy` class.

        ```xml
        <alias alias="CAConnector" type="Keyfactor.AnyGateway.Google.GoogleCAProxy, GoogleCAProxy"/>
        ```

    2. Modify the `Newtonsoft.Json` `bindingRedirect` to redirect versions from `0.0.0.0-13.0.0.0` to `12.0.0.0`.

        ```xml
        <dependentAssembly>
            <assemblyIdentity name="Newtonsoft.Json" publicKeyToken="30AD4FE6B2A6AEED" culture="neutral" />
            <bindingRedirect oldVersion="0.0.0.0-13.0.0.0" newVersion="12.0.0.0" />
        </dependentAssembly>
        ```

    3. Add a `bindingRedirect` for `Google.Apis.Auth` to redirect versions from `0.0.0.0-1.67.0.0` to `1.67.0.0`.

        ```xml
        <dependentAssembly>
            <assemblyIdentity name="Google.Apis.Auth" publicKeyToken="4b01fa6e34db77ab" culture="neutral" />
            <bindingRedirect oldVersion="0.0.0.0-1.67.0.0" newVersion="1.67.0.0" />
        </dependentAssembly>
        ```

    4. Add a `bindingRedirect` for `System.Memory` to redirect versions from `0.0.0.0-4.0.1.2` to `4.0.1.1`.

        ```xml
        <dependentAssembly>
            <assemblyIdentity name="System.Memory" culture="neutral" publicKeyToken="cc7b13ffcd2ddd51" />
            <bindingRedirect oldVersion="0.0.0.0-4.0.1.2" newVersion="4.0.1.1" />
        </dependentAssembly>
        ```

    > Depending on additional environment-specific factors, additional binding redirects may need to be applied to `CAProxyServer.config`.

## Configuration
The following sections will breakdown the required configurations for the AnyGatewayConfig.json file that will be imported to configure the Google CA. 

### Templates

As discussed in the [Template Identification](#template-identification), the GCP CAS AnyCA Gateway DCOM plugin supports [GCP CAS Certificate Templates](https://cloud.google.com/certificate-authority-service/docs/policy-controls). The Keyfactor AnyCA Gateway DCOM maps [AD Certificate Templates](https://learn.microsoft.com/en-us/windows-server/identity/ad-cs/certificate-template-concepts) to GCP Certificate Templates via the `ProductID` property in the `Templates` section of configuration files. 

_At least one_ Certificate Template must be defined in this section with the `ProductID` set to `Default`. This Product ID corresponds to no Certificate Template for the [CreateCertificate RPC](https://cloud.google.com/certificate-authority-service/docs/reference/rpc/google.cloud.security.privateca.v1#google.cloud.security.privateca.v1.CertificateAuthorityService.CreateCertificate).

Subsequent Certificate Templates should set the `ProductID` to the Certificate Template ID in GCP CAS.

```json
"Templates": {
    "GCPCASDefault": {
        "ProductID": "Default",
            "Parameters": {
                "Lifetime": "300", /* Certificate validity in days */
            }
    }
}
```

> The `Lifetime` key should be added as a Custom Enrollment Parameter/Field for each Certificate Template in Keyfactor Command per the [official Keyfactor documentation](https://software.keyfactor.com/Core-OnPrem/Current/Content/ReferenceGuide/Configuring%20Template%20Options.htm).

## Security

Refer to the [official Keyfactor documentation](https://software.keyfactor.com/Guides/AnyGateway_Generic/Content/AnyGateway/cmdlets.htm) to configure the `Security` section. The following is provided as an example.

```json
/* Grant permissions on the CA to users or groups in the local domain.
   READ: Enumerate and read contents of certificates.
   ENROLL: Request certificates from the CA.
   OFFICER: Perform certificate functions such as issuance and revocation. This is equivalent to "Issue and Manage" permission on the Microsoft CA.
   ADMINISTRATOR: Configure/reconfigure the gateway.
  
  Valid permission settings are "Allow", "None", and "Deny".
*/
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

## CAConnection

The `CAConnection` section selects the GCP Project/CA Pool/CA whose certificate operations will be extended to Keyfactor. There are three required fields.

* `ProjectId` - The Resource ID of the project that contains the Google CA Service.
* `LocationId` - The GCP location ID where the project containing the target GCP CAS CA is located. For example, 'us-central1'.
* `CAPoolId` - The CA Pool ID in GCP CAS to use for certificate operations. If the CA Pool has resource name `projects/my-project/locations/us-central1/caPools/my-pool`, this field should be set to `my-pool`.
* `CAId` (optional) - The CA ID of a CA in the same CA Pool as CAPool. For example, to issue certificates from a CA with resource name `projects/my-project/locations/us-central1/caPools/my-pool/certificateAuthorities/my-ca`, this field should be set to `my-ca`.

```json
"CAConnection": {
    "LocationId": "us-east1",
    "ProjectId": "concise-frame-296019",
    "CAPoolId":"gcp-test-pool",
    "CAId":"ca-enterprise-subordinate-sandbox-tls"
}
```

> If `CAId` is not specified, CA selection will defer to GCP CAS - a CA in the CA Pool identified by `CAPoolId` will be selected automatically.

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
