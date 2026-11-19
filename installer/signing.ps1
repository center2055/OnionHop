# Authenticode signing for the Windows release builds, with Azure Artifact Signing, as Rana GmbH.
#
# The build scripts dot-source this file when they run with -Sign. They sign OnionHop's own
# executables before packaging, and hand Inno Setup the same command, so the installer and the
# uninstaller inside it are signed too. Third-party programs in the package (Tor, sing-box, Xray,
# the transports) stay as their projects ship them.
#
# What signing needs on the machine:
#   - an Azure CLI login whose identity has the role "Artifact Signing Certificate Profile Signer"
#     on the signing account. The release workflow logs in as the app registration
#     "OnionHop GitHub Signing" with GitHub's OIDC token, so no secret is stored anywhere.
#   - signtool.exe from the Windows SDK, 10.0.22621 or newer
#   - the .NET runtime the Microsoft signing client is built for (10 for the pinned version)
#
# The endpoint, account and profile are identifiers, not secrets.

$script:SigningEndpoint = "https://neu.codesigning.azure.net/"
$script:SigningAccount = "ranagmbh"
$script:SigningProfile = "rana-public"
# The common name every signature has to carry.
$script:SigningSubject = "Rana GmbH"
$script:SigningTimestampUrl = "http://timestamp.acs.microsoft.com"
$script:SigningProjectUrl = "https://github.com/center2055/OnionHop"
$script:SigningScope = "https://codesigning.azure.net/.default"

# Microsoft.ArtifactSigning.Client from nuget.org, pinned by version and SHA-256.
$script:SigningClientVersion = "1.0.151"
$script:SigningClientSha256 = "35375a4bc4e1102da729fc3d8d9925f774c3baae6b8b7ff6398d948ca9ab0d8a"

$script:SigningContext = $null

function Find-OnionHopSignTool {
  $kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
  $found = Get-ChildItem -Path $kits -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^10\.\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { [pscustomobject]@{ Version = [version]$_.Name; Path = (Join-Path $_.FullName "x64\signtool.exe") } } |
    Where-Object { Test-Path $_.Path } |
    Select-Object -First 1

  if (-not $found) {
    throw "signtool.exe was not found under '$kits'. Install the Windows SDK (10.0.22621 or newer)."
  }
  if ($found.Version -lt [version]"10.0.22621.0") {
    throw "signtool $($found.Version) is too old for Azure Artifact Signing. Install the Windows SDK 10.0.22621 or newer."
  }
  return $found.Path
}

function Get-OnionHopSigningClient {
  $base = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
  $dir = Join-Path $base "onionhop-signing\client-$script:SigningClientVersion"
  $dlib = Join-Path $dir "bin\x64\Azure.CodeSigning.Dlib.dll"
  if (Test-Path $dlib) {
    return $dir
  }

  $package = "$dir.nupkg"
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dir) | Out-Null
  $id = "microsoft.artifactsigning.client"
  $url = "https://api.nuget.org/v3-flatcontainer/$id/$script:SigningClientVersion/$id.$script:SigningClientVersion.nupkg"
  Write-Host "Downloading the Microsoft signing client $script:SigningClientVersion..." -ForegroundColor Cyan
  [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
  Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $package

  $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $package).Hash.ToLowerInvariant()
  if ($hash -ne $script:SigningClientSha256) {
    Remove-Item -Force -LiteralPath $package
    throw "The signing client download has SHA-256 $hash, expected $script:SigningClientSha256. Nothing was signed."
  }

  if (Test-Path $dir) { Remove-Item -Recurse -Force -LiteralPath $dir }
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [IO.Compression.ZipFile]::ExtractToDirectory($package, $dir)
  Remove-Item -Force -LiteralPath $package
  if (!(Test-Path $dlib)) {
    throw "The signing client package has no bin\x64\Azure.CodeSigning.Dlib.dll."
  }
  return $dir
}

function Assert-OnionHopSigningRuntime {
  param([Parameter(Mandatory = $true)][string]$ClientDir)

  # signtool loads the client into its own process, and the client needs its .NET runtime there.
  $config = Get-Content -Raw -LiteralPath (Join-Path $ClientDir "bin\x64\Azure.CodeSigning.Dlib.runtimeconfig.json") | ConvertFrom-Json
  $needed = [int](([string]$config.runtimeOptions.framework.version).Split('.')[0])
  $installed = @()
  if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $installed = @(& dotnet --list-runtimes |
      Where-Object { $_ -match '^Microsoft\.NETCore\.App (\d+)\.' } |
      ForEach-Object { [int]$Matches[1] })
  }
  if (-not ($installed | Where-Object { $_ -ge $needed })) {
    throw "The signing client needs the .NET $needed runtime (x64), and it is not installed. Installed: $(($installed | Sort-Object -Unique) -join ', ')."
  }
}

# Finds the tools, checks the Azure login and writes the client's settings. Called once per script,
# early: the first token request has to come soon after a workflow's Azure login.
function Initialize-OnionHopSigning {
  if ($script:SigningContext) {
    return $script:SigningContext
  }

  $signTool = Find-OnionHopSignTool
  $clientDir = Get-OnionHopSigningClient
  Assert-OnionHopSigningRuntime -ClientDir $clientDir

  if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Signing needs the Azure CLI (az) with a login that may sign. Build without -Sign for an unsigned package."
  }
  $expires = & az account get-access-token --scope $script:SigningScope --query expiresOn --output tsv
  if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($expires)) {
    throw "The Azure CLI has no login that gets a token for Azure Artifact Signing. Run 'az login' (or azure/login in a workflow) first."
  }
  Write-Host "Azure signing token is valid until $expires." -ForegroundColor Green

  # Only the Azure CLI login is used: no probing for other identities, and no browser window.
  $metadata = [ordered]@{
    Endpoint = $script:SigningEndpoint
    CodeSigningAccountName = $script:SigningAccount
    CertificateProfileName = $script:SigningProfile
    ExcludeCredentials = @(
      "ManagedIdentityCredential",
      "EnvironmentCredential",
      "WorkloadIdentityCredential",
      "SharedTokenCacheCredential",
      "VisualStudioCredential",
      "VisualStudioCodeCredential",
      "AzurePowerShellCredential",
      "AzureDeveloperCliCredential",
      "InteractiveBrowserCredential"
    )
  }
  $metadataPath = Join-Path (Split-Path -Parent $clientDir) "metadata.json"
  [IO.File]::WriteAllText($metadataPath, ($metadata | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))

  $script:SigningContext = [pscustomobject]@{
    SignTool = $signTool
    Dlib = (Join-Path $clientDir "bin\x64\Azure.CodeSigning.Dlib.dll")
    Metadata = $metadataPath
  }
  Write-Host "Signing as '$script:SigningSubject' with the profile '$script:SigningProfile' of the account '$script:SigningAccount'." -ForegroundColor Cyan
  return $script:SigningContext
}

# Fails unless every file carries a valid, timestamped signature of the expected publisher.
function Assert-OnionHopSigned {
  param([Parameter(Mandatory = $true)][string[]]$Files)

  foreach ($file in $Files) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    $subject = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { "" }
    if ($signature.Status -ne "Valid") {
      throw "'$file' has no valid signature (status: $($signature.Status))."
    }
    if ($subject -notmatch "(^|,\s*)CN=$([regex]::Escape($script:SigningSubject))(,|$)") {
      throw "'$file' is signed by '$subject', not by '$script:SigningSubject'."
    }
    if (-not $signature.TimeStamperCertificate) {
      throw "The signature of '$file' has no timestamp, so it would stop being valid when the certificate expires."
    }
    Write-Host "Signed: $file" -ForegroundColor Green
  }
}

function Invoke-OnionHopSigning {
  param(
    [Parameter(Mandatory = $true)][string[]]$Files,
    [string]$Description = "OnionHop"
  )

  $context = Initialize-OnionHopSigning
  $missing = @($Files | Where-Object { -not (Test-Path -LiteralPath $_) })
  if ($missing.Count -gt 0) {
    throw ("Files to sign are missing:`n - " + ($missing -join "`n - "))
  }

  $arguments = @(
    "sign", "/fd", "SHA256", "/tr", $script:SigningTimestampUrl, "/td", "SHA256",
    "/dlib", $context.Dlib, "/dmdf", $context.Metadata,
    "/d", $Description, "/du", $script:SigningProjectUrl
  ) + $Files

  # The signing and timestamp services are remote: one failed request should not cost the build.
  for ($attempt = 1; $attempt -le 3; $attempt++) {
    & $context.SignTool @arguments
    if ($LASTEXITCODE -eq 0) {
      break
    }
    if ($attempt -eq 3) {
      throw "signtool failed with exit code $LASTEXITCODE."
    }
    Write-Warning "signtool failed with exit code $LASTEXITCODE. Trying again..."
    Start-Sleep -Seconds (10 * $attempt)
  }

  Assert-OnionHopSigned -Files $Files
}

# Signs OnionHop's own programs in a publish folder: the named ones, which have to be there, and
# ArtiHop, which is built from center2055/ArtiHop, when it is bundled.
function Invoke-OnionHopPublishSigning {
  param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string[]]$Names,
    [string]$Description = "OnionHop"
  )

  $files = @($Names | ForEach-Object { Join-Path $PublishDir $_ })
  $artiHop = Join-Path $PublishDir "artihop\artihop.exe"
  if (Test-Path -LiteralPath $artiHop) {
    $files += $artiHop
  }
  Invoke-OnionHopSigning -Files $files -Description $Description
}

# The ISCC arguments that make Inno Setup sign the installer and its uninstaller. The .iss files
# switch signing on when SignToolName is defined. In an Inno sign command $q is a quotation mark
# and $f the quoted file, so no quoting has to survive the way here.
function Get-OnionHopInnoSigningArguments {
  param([string]$Description = "OnionHop")

  $context = Initialize-OnionHopSigning
  $q = '$q'
  $command = "$q$($context.SignTool)$q sign /fd SHA256 /tr $script:SigningTimestampUrl /td SHA256 " +
    "/dlib $q$($context.Dlib)$q /dmdf $q$($context.Metadata)$q " +
    "/d $q$Description$q /du $script:SigningProjectUrl " + '$f'
  return @("/Sonionhopsign=$command", "/DSignToolName=onionhopsign")
}
