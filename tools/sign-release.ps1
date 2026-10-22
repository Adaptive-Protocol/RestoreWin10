param(
  [Parameter(Mandatory = $true)][string]$File,   # the published RestoreWin10.exe
  [string]$Thumbprint,                           # the code-signing certificate's SHA-1 thumbprint (from Cert:\CurrentUser\My)
  [string]$TimestampUrl = 'http://timestamp.digicert.com'   # the CA's RFC 3161 timestamp server (Sectigo: http://timestamp.sectigo.com)
)
# Signs a RestoreWin10 release with Authenticode, SHA-256, and a trusted timestamp, then verifies it.
#
# The timestamp keeps the signature valid after the certificate expires; without it, every release would show as
# invalid once the certificate lapses. Sign after publishing and before computing SHA256SUMS: signing changes the file.
#
#   powershell -NoProfile -File tools\sign-release.ps1 -File publish\RestoreWin10.exe -Thumbprint <thumbprint>
#
# Without -Thumbprint it lists the code-signing certificates it can find and signs nothing.
$ErrorActionPreference = 'Stop'
$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$signtool = Get-ChildItem -Path $kits -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
  Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw "signtool.exe not found under $kits (it comes with the Windows SDK)" }
if (-not (Test-Path -LiteralPath $File)) { throw "no such file: $File" }

if (-not $Thumbprint) {
  $certs = @(Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue) +
           @(Get-ChildItem Cert:\LocalMachine\My -CodeSigningCert -ErrorAction SilentlyContinue)
  if ($certs.Count -eq 0) { "No code-signing certificate found in the current user's or the computer's store. Nothing was signed."; exit 1 }
  "Code-signing certificates found (pass one with -Thumbprint):"
  $certs | ForEach-Object { "  {0}  {1}  valid until {2:yyyy-MM-dd}" -f $_.Thumbprint, $_.Subject, $_.NotAfter }
  exit 1
}

& $signtool.FullName sign /fd sha256 /tr $TimestampUrl /td sha256 /sha1 $Thumbprint `
  /d 'RestoreWin10' /du 'https://github.com/Adaptive-Protocol/RestoreWin10' $File
if ($LASTEXITCODE -ne 0) { throw "signing failed ($LASTEXITCODE)" }
& $signtool.FullName verify /pa /tw $File
if ($LASTEXITCODE -ne 0) { throw "the signature does not verify ($LASTEXITCODE)" }
$sig = Get-AuthenticodeSignature -LiteralPath $File
"Signed: {0}; signer: {1}; timestamped: {2}" -f $sig.Status, $sig.SignerCertificate.Subject, [bool]$sig.TimeStamperCertificate
"SHA-256 after signing: " + (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLower()
