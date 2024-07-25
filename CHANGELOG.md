# v2.0.0
* Migrate `packages.config` to `PackageReference` format
* Upgrade packages to support Keyfactor AnyCA Gateway DCOM v24.2
    * Upgrade `Keyfactor.AnyGateway.SDK` to `24.2.0-PRERELEASE-47446`
* Add support for [GCP CAS Certificate Templates](https://cloud.google.com/certificate-authority-service/docs/policy-controls)
* Enable configuration of CA Pool-based or CA-specific certificate enrollment. If the `CAId` is specified, certificates are enrolled with the CA specified by `CAId`. Otherwise, GCP CAS selects a CA in the CA Pool based on policy.

# v1.1.0 
  - Remove template references from README
  - Small bug fixes  

# v1.0.0
* Initial Release. Support for Google GA CA Service.  Sync, Enroll, and Revocation. 
