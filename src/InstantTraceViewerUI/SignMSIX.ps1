$ErrorActionPreference = "Stop"

# Config
$certName = "CN=FE01EC5C-A845-4996-83AA-3BF4024D23C2"
$pfxPath = "$env:TEMP\MyMsixTestCert.pfx"
$msixPath = "D:\repos\itv2\src\InstantTraceViewerUI\bin\MSIX\InstantTraceViewer_X64.msix"
$pfxPassword = "hello123"

# Step 1: Create a self-signed certificate
$cert = New-SelfSignedCertificate `
    -Subject $certName `
    -Type CodeSigning `
    -KeyExportPolicy Exportable `
    -KeyUsage DigitalSignature `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -CertStoreLocation "Cert:\CurrentUser\My"

# Step 2: Export the certificate to a .pfx file
$securePassword = ConvertTo-SecureString -String $pfxPassword -Force -AsPlainText
Export-PfxCertificate `
    -Cert $cert `
    -FilePath $pfxPath `
    -Password $securePassword

# Step 3: Import to Trusted People (so Windows will trust the cert locally)
Import-PfxCertificate `
    -FilePath $pfxPath `
    -Password $securePassword `
    -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null

# Step 4: Sign the MSIX
& signtool.exe sign /debug /fd SHA256 /a /f $pfxPath /p $pfxPassword /td SHA256 $msixPath