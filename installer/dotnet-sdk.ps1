<#
.SYNOPSIS
    Aide de l'installateur d'Ashboard Vision pour le SDK .NET 10 : trouve la dernière version chez Microsoft, puis vérifie le fichier téléchargé.

.DESCRIPTION
    Inno Setup 7.1 ne lit pas le JSON, ne calcule que des empreintes SHA-256 et ne vérifie pas les signatures Authenticode : ce petit script le fait,
    en Windows PowerShell 5.1 (powershell.exe, présent sur tout Windows 10 et 11). L'installateur l'embarque (dontcopy), l'extrait dans son dossier
    temporaire et le lance avec « powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ». Le téléchargement lui-même est fait par
    Inno Setup, pour sa page de progression.

    -Resolve : lit releases.json (-ReleasesSource : l'adresse officielle par défaut, ou un fichier local pour les tests), prend latest-sdk, puis le
               fichier SDK win-x64 en .exe de cette version. Il refuse une adresse qui n'est pas en https:// sur un hôte de Microsoft de la liste
               ci-dessous. Résultat dans -Output (lignes « version=… », « url=… », « sha512=… »).
    -Verify  : vérifie le fichier : son empreinte SHA-512 est celle de releases.json, et il porte une signature Authenticode valide, intégrée au
               fichier, dont le signataire est Microsoft Corporation. Un fichier qui échoue n'est jamais lancé par l'installateur.

    Code de sortie 0 : réussi ; autre : refus (la raison est dans -Output, ligne « error=… », et sur la sortie d'erreur).
      1 erreur imprévue ; 2 releases.json illisible ou introuvable ; 3 aucune version ou aucun fichier retenable ; 4 adresse refusée ;
      10 fichier introuvable ; 11 empreinte fausse ; 12 signature absente ou invalide ; 13 signataire autre que Microsoft.

.EXAMPLE
    powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File dotnet-sdk.ps1 -Resolve -Output resultat.txt
    powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File dotnet-sdk.ps1 -Verify dotnet-sdk.exe -Sha512 <empreinte> -Output resultat.txt
#>
[CmdletBinding(DefaultParameterSetName = 'Resolve')]
param(
    [Parameter(ParameterSetName = 'Resolve', Mandatory)]
    [switch] $Resolve,

    [Parameter(ParameterSetName = 'Resolve')]
    [string] $ReleasesSource = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json',

    [Parameter(ParameterSetName = 'Verify', Mandatory)]
    [string] $Verify,

    [Parameter(ParameterSetName = 'Verify', Mandatory)]
    [string] $Sha512,

    [string] $Output
)

$ErrorActionPreference = 'Stop'

# Les seuls hôtes d'où le SDK peut venir (Microsoft). L'adresse doit aussi être en https://, sans identifiant ni port particulier.
$AllowedHosts = @('builds.dotnet.microsoft.com', 'dotnetcli.blob.core.windows.net', 'download.visualstudio.microsoft.com')

# Un refus : le code de sortie dit l'étape, le texte dit pourquoi.
class Refusal : System.Exception {
    [int] $Code
    Refusal([int] $code, [string] $message) : base($message) { $this.Code = $code }
}

function Write-Result([string[]] $lines) {
    if (-not $Output) { return }
    [System.IO.File]::WriteAllText($Output, (($lines -join "`r`n") + "`r`n"), (New-Object System.Text.UTF8Encoding($true)))
}

function Read-Releases([string] $source) {
    $text = $null
    try {
        if ($source -match '^[hH][tT][tT][pP][sS]?://') {
            if ($source -notmatch '^[hH][tT][tT][pP][sS]://') { throw [Refusal]::new(4, 'releases.json : l''adresse doit être en https://.') }
            # Windows PowerShell 5.1 n'active pas toujours TLS 1.2.
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $response = Invoke-WebRequest -Uri $source -UseBasicParsing -TimeoutSec 30
            $text = [System.Text.Encoding]::UTF8.GetString($response.RawContentStream.ToArray())
        }
        else {
            $text = [System.IO.File]::ReadAllText($source, [System.Text.Encoding]::UTF8)
        }
        $data = $text | ConvertFrom-Json
        if ($null -eq $data) { throw [Refusal]::new(2, 'releases.json est vide.') }
        return $data
    }
    catch [Refusal] { throw }
    catch { throw [Refusal]::new(2, "releases.json est illisible ou introuvable : $($_.Exception.Message)") }
}

function Test-AllowedUrl([string] $url) {
    $uri = $null
    if (-not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref] $uri)) { return $false }
    return ($uri.Scheme -eq 'https') -and $uri.IsDefaultPort -and [string]::IsNullOrEmpty($uri.UserInfo) -and ($AllowedHosts -contains $uri.Host.ToLowerInvariant())
}

function Resolve-Sdk {
    $data = Read-Releases $ReleasesSource
    $latest = [string] $data.'latest-sdk'
    if ($latest -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw [Refusal]::new(3, "releases.json : latest-sdk est absent ou mal formé ($latest).") }

    # L'entrée SDK de cette version, dans « sdk » ou dans « sdks » de chaque publication.
    $candidates = @()
    foreach ($release in @($data.releases)) {
        foreach ($entry in (@($release.sdk) + @($release.sdks))) {
            if ($entry -and ([string] $entry.version) -eq $latest) { $candidates += $entry }
        }
    }
    if ($candidates.Count -eq 0) { throw [Refusal]::new(3, "releases.json : la version $latest (latest-sdk) n'a pas d'entrée SDK dans releases.") }

    $file = $null
    foreach ($entry in $candidates) {
        $file = @($entry.files) | Where-Object { $_ -and $_.rid -eq 'win-x64' -and ([string] $_.name) -like '*.exe' } | Select-Object -First 1
        if ($file) { break }
    }
    if (-not $file) { throw [Refusal]::new(3, "releases.json : la version $latest n'a pas de fichier .exe pour win-x64.") }

    $url = [string] $file.url
    $hash = ([string] $file.hash).Trim().ToLowerInvariant()
    if (-not (Test-AllowedUrl $url)) { throw [Refusal]::new(4, "Adresse refusée (https:// sur un hôte de Microsoft de la liste seulement) : $url") }
    if ($hash -notmatch '^[0-9a-f]{128}$') { throw [Refusal]::new(3, 'releases.json : l''empreinte SHA-512 du fichier est absente ou mal formée.') }

    Write-Result @("version=$latest", "url=$url", "sha512=$hash")
}

function Test-SdkFile {
    if (-not (Test-Path -LiteralPath $Verify -PathType Leaf)) { throw [Refusal]::new(10, "Fichier introuvable : $Verify") }
    $expected = $Sha512.Trim().ToLowerInvariant()
    if ($expected -notmatch '^[0-9a-f]{128}$') { throw [Refusal]::new(11, 'L''empreinte SHA-512 attendue est mal formée.') }

    $actual = (Get-FileHash -LiteralPath $Verify -Algorithm SHA512).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw [Refusal]::new(11, 'L''empreinte SHA-512 du fichier n''est pas celle de releases.json.') }

    # La signature intégrée au fichier (pas une signature de catalogue), valide, et le signataire : Microsoft Corporation.
    $signature = Get-AuthenticodeSignature -LiteralPath $Verify
    if ($signature.Status -ne 'Valid' -or $signature.SignatureType -ne 'Authenticode' -or -not $signature.SignerCertificate) {
        throw [Refusal]::new(12, "La signature Authenticode du fichier est absente ou invalide (état : $($signature.Status), type : $($signature.SignatureType)).")
    }
    $subject = $signature.SignerCertificate.Subject
    if ($subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') { throw [Refusal]::new(13, "Le fichier n'est pas signé par Microsoft : $subject") }

    Write-Result @('ok')
}

try {
    if ($PSCmdlet.ParameterSetName -eq 'Verify') { Test-SdkFile } else { Resolve-Sdk }
    exit 0
}
catch [Refusal] {
    Write-Result @("error=$($_.Exception.Message)")
    [Console]::Error.WriteLine($_.Exception.Message)
    exit $_.Exception.Code
}
catch {
    Write-Result @("error=$($_.Exception.Message)")
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
