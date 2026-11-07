<#
.SYNOPSIS
    Aide de l'installateur d'Ashboard Vision pour le SDK .NET 10 : trouve la dernière version chez Microsoft, vérifie le fichier téléchargé, puis le lance.

.DESCRIPTION
    Inno Setup 7.1 ne lit pas le JSON, ne calcule que des empreintes SHA-256 et ne vérifie pas les signatures Authenticode : ce petit script le fait,
    en Windows PowerShell 5.1 (powershell.exe, présent sur tout Windows 10 et 11). L'installateur l'embarque (dontcopy), l'extrait dans son dossier
    temporaire, le copie dans un dossier réservé aux administrateurs (et vérifie son empreinte) et le lance avec
    « powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ». Le téléchargement lui-même est fait par Inno Setup, pour sa page de progression.

    -Resolve : lit releases.json (-ReleasesSource : l'adresse officielle par défaut, ou un fichier local pour les tests), prend latest-sdk, puis le
               fichier SDK win-x64 en .exe de cette version. Il refuse une adresse qui n'est pas en https:// sur un hôte de Microsoft de la liste
               ci-dessous. Résultat dans -Output (lignes « version=… », « url=… » (forme analysée de l'adresse), « sha512=… »).
    -Verify  : vérifie le fichier (sans le lancer) : voir « Les vérifications » ci-dessous.
    -Install : ouvre le fichier SANS partage en écriture ni en suppression (le temps de tout le travail), le vérifie sur ce fichier ouvert, puis lance
               « <fichier> /install /quiet /norestart » en le gardant ouvert jusqu'à la fin du setup. Code de sortie 0 : le setup a été lancé et il est
               fini, son propre code de sortie est dans -Output (ligne « exitcode=… »). Un fichier qui échoue à une vérification n'est jamais lancé.
    -CheckSubject, -CheckSubjectHex, -CheckRoot : les deux décisions du signataire, sur des valeurs, pour les tests.

    Les vérifications (-Verify et -Install), toutes obligatoires :
      1. l'empreinte SHA-512 du fichier est celle de releases.json ;
      2. la signature Authenticode est valide ET intégrée au fichier (pas une signature de catalogue) ;
      3. le sujet du certificat signataire, lu dans son encodage DER (jamais découpé dans un texte), a un seul attribut O, exactement « Microsoft Corporation »,
         et un seul attribut CN, exactement « .NET » (celui de dotnet.exe et du setup du SDK, relevé en 2026) ou « Microsoft Corporation » ;
      4. la chaîne du certificat signataire, bâtie par Windows, remonte à une racine de la courte liste ci-dessous (Microsoft Root Certificate Authority 2010
         ou 2011), par l'empreinte SHA-256 de son dernier certificat, et cette racine est une racine de confiance du magasin de Windows. Les certificats
         embarqués dans la signature servent d'intermédiaires possibles (ChainPolicy.ExtraStore) : cela ne change rien à la sûreté, car Windows ne bâtit une chaîne
         que si chaque certificat est réellement signé par le suivant jusqu'à une racine de confiance, et c'est la racine épinglée qui décide. AUCUN contrôle de
         révocation (rien ne prouve que Get-AuthenticodeSignature en fasse un) : la racine épinglée et la signature valide suffisent ; la date de validité du
         certificat est ignorée (la signature est horodatée) ;
      5. le champ ProductName des informations de version commence par « Microsoft .NET » (-ProductNamePrefix, pour les tests seulement).

    Code de sortie 0 : réussi ; autre : refus (la raison est dans -Output, ligne « error=… », et sur la sortie d'erreur).
      1 erreur imprévue ; 2 releases.json illisible ou introuvable ; 3 aucune version ou aucun fichier retenable ; 4 adresse refusée ;
      10 fichier introuvable ou non ouvrable ; 11 empreinte fausse ; 12 signature absente, invalide ou pas intégrée ; 13 signataire autre que Microsoft ;
      14 ce n'est pas un setup du SDK .NET ; 15 racine de la chaîne inconnue ; 20 le setup n'a pas pu être lancé.

.EXAMPLE
    powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File dotnet-sdk.ps1 -Resolve -Output resultat.txt
    powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File dotnet-sdk.ps1 -Install dotnet-sdk.exe -Sha512 <empreinte> -Output resultat.txt
#>
[CmdletBinding(DefaultParameterSetName = 'Resolve')]
param(
    [Parameter(ParameterSetName = 'Resolve', Mandatory)]
    [switch] $Resolve,

    [Parameter(ParameterSetName = 'Resolve')]
    [string] $ReleasesSource = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json',

    [Parameter(ParameterSetName = 'Verify', Mandatory)]
    [string] $Verify,

    [Parameter(ParameterSetName = 'Install', Mandatory)]
    [string] $Install,

    [Parameter(ParameterSetName = 'Verify', Mandatory)]
    [Parameter(ParameterSetName = 'Install', Mandatory)]
    [string] $Sha512,

    # Pour les tests seulement : l'installateur ne le passe jamais.
    [Parameter(ParameterSetName = 'Verify')]
    [Parameter(ParameterSetName = 'Install')]
    [string] $ProductNamePrefix = 'Microsoft .NET',

    [Parameter(ParameterSetName = 'CheckSubject', Mandatory)]
    [string] $CheckSubject,

    # Pour les tests : un sujet DER donné en hexadécimal (pour les types de chaînes et les RDN à plusieurs valeurs qu'un texte ne produit pas).
    [Parameter(ParameterSetName = 'CheckSubjectHex', Mandatory)]
    [string] $CheckSubjectHex,

    [Parameter(ParameterSetName = 'CheckRoot', Mandatory)]
    [string] $CheckRoot,

    [string] $Output
)

$ErrorActionPreference = 'Stop'
$script:EmbeddedCertificateCount = 0

# Les seuls hôtes d'où le SDK peut venir (Microsoft). L'adresse doit aussi être en https://, sans identifiant ni port particulier.
$AllowedHosts = @('builds.dotnet.microsoft.com', 'dotnetcli.blob.core.windows.net', 'download.visualstudio.microsoft.com')

# Les racines de Microsoft qui signent ses programmes, par l'empreinte SHA-256 de leur certificat (relevées sur le magasin « Autorités de certification
# racines de confiance » de Windows ; dotnet.exe et le setup du SDK 10.0.403 remontent à la racine 2011).
$AllowedRoots = @(
    '847DF6A78497943F27FC72EB93F9A637320A02B561D0A91B09E87A7807ED7C61'.ToLowerInvariant(), # Microsoft Root Certificate Authority 2011
    'DF545BF919A2439C36983B54CDFC903DFA4F37D3996D8D84B4C31EEC6F3C163E'.ToLowerInvariant()  # Microsoft Root Certificate Authority 2010
)

# Les attributs du sujet que l'on contrôle (OID X.500) et ce qu'ils doivent valoir.
$OidOrganization = '2.5.4.10'
$OidCommonName = '2.5.4.3'
$RequiredOrganization = 'Microsoft Corporation'
$AllowedCommonNames = @('.NET', 'Microsoft Corporation')

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

# Rend l'adresse analysée (Uri.AbsoluteUri) si elle est acceptée, sinon $null.
function Get-AllowedUrl([string] $url) {
    $uri = $null
    if (-not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref] $uri)) { return $null }
    $accepted = ($uri.Scheme -eq 'https') -and $uri.IsDefaultPort -and [string]::IsNullOrEmpty($uri.UserInfo) -and ($AllowedHosts -contains $uri.Host.ToLowerInvariant())
    if (-not $accepted) { return $null }
    return $uri.AbsoluteUri
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

    $url = Get-AllowedUrl ([string] $file.url)
    $hash = ([string] $file.hash).Trim().ToLowerInvariant()
    if (-not $url) { throw [Refusal]::new(4, "Adresse refusée (https:// sur un hôte de Microsoft de la liste seulement) : $($file.url)") }
    if ($hash -notmatch '^[0-9a-f]{128}$') { throw [Refusal]::new(3, 'releases.json : l''empreinte SHA-512 du fichier est absente ou mal formée.') }

    Write-Result @("version=$latest", "url=$url", "sha512=$hash")
}

# --- Le sujet du certificat, lu dans son encodage DER -------------------------------------------------------------------------

# Lit un en-tête DER à partir de $pos : rend l'étiquette, le début et la longueur du contenu, et avance $pos jusqu'au contenu.
function Read-DerHeader([byte[]] $bytes, [ref] $pos) {
    if ($pos.Value + 2 -gt $bytes.Length) { throw 'encodage DER tronqué' }
    $tag = $bytes[$pos.Value]; $pos.Value++
    $first = $bytes[$pos.Value]; $pos.Value++
    $length = 0
    if ($first -lt 0x80) { $length = [int] $first }
    else {
        $count = $first -band 0x7F
        if ($count -lt 1 -or $count -gt 3 -or $pos.Value + $count -gt $bytes.Length) { throw 'longueur DER invalide' }
        for ($i = 0; $i -lt $count; $i++) { $length = ($length -shl 8) -bor $bytes[$pos.Value]; $pos.Value++ }
    }
    if ($length -lt 0 -or $pos.Value + $length -gt $bytes.Length) { throw 'encodage DER tronqué' }
    return @{ Tag = [int] $tag; Start = $pos.Value; Length = $length }
}

function ConvertFrom-DerOid([byte[]] $content) {
    if ($content.Length -lt 1) { throw 'OID vide' }
    $parts = New-Object System.Collections.Generic.List[string]
    $parts.Add([string] [int] [math]::Floor($content[0] / 40)); $parts.Add([string] ($content[0] % 40))
    $value = [long] 0
    for ($i = 1; $i -lt $content.Length; $i++) {
        $value = ($value -shl 7) -bor ($content[$i] -band 0x7F)
        if (($content[$i] -band 0x80) -eq 0) { $parts.Add([string] $value); $value = 0 }
    }
    return ($parts -join '.')
}

# La valeur d'un attribut, d'après son étiquette DER ; $null pour un type que l'on ne sait pas lire (il ne vaudra alors rien).
function ConvertFrom-DerString([int] $tag, [byte[]] $content) {
    switch ($tag) {
        0x0C { return [System.Text.Encoding]::UTF8.GetString($content) }           # UTF8String
        0x13 { return [System.Text.Encoding]::ASCII.GetString($content) }          # PrintableString
        0x16 { return [System.Text.Encoding]::ASCII.GetString($content) }          # IA5String
        0x14 { return [System.Text.Encoding]::GetEncoding(28591).GetString($content) } # TeletexString (latin-1)
        0x1E { return [System.Text.Encoding]::BigEndianUnicode.GetString($content) }   # BMPString
        default { return $null }
    }
}

# Découpe un Name DER (SEQUENCE de SET d'attributs) en une liste de { Oid, Value }, sans jamais passer par un texte.
function Get-NameAttributes([byte[]] $raw) {
    $attributes = New-Object System.Collections.Generic.List[object]
    $pos = 0
    $name = Read-DerHeader $raw ([ref] $pos)
    if ($name.Tag -ne 0x30 -or $name.Start + $name.Length -ne $raw.Length) { throw 'le sujet n''est pas une SEQUENCE DER' }
    $end = $name.Start + $name.Length
    while ($pos -lt $end) {
        $set = Read-DerHeader $raw ([ref] $pos)
        if ($set.Tag -ne 0x31) { throw 'attribut du sujet mal formé (SET attendu)' }
        $setEnd = $set.Start + $set.Length
        while ($pos -lt $setEnd) {
            $pair = Read-DerHeader $raw ([ref] $pos)
            if ($pair.Tag -ne 0x30) { throw 'attribut du sujet mal formé (SEQUENCE attendue)' }
            $pairEnd = $pair.Start + $pair.Length
            $oid = Read-DerHeader $raw ([ref] $pos)
            if ($oid.Tag -ne 0x06) { throw 'attribut du sujet mal formé (OID attendu)' }
            $oidBytes = New-Object byte[] $oid.Length; [Array]::Copy($raw, $oid.Start, $oidBytes, 0, $oid.Length); $pos = $oid.Start + $oid.Length
            $value = Read-DerHeader $raw ([ref] $pos)
            $valueBytes = New-Object byte[] $value.Length; [Array]::Copy($raw, $value.Start, $valueBytes, 0, $value.Length); $pos = $value.Start + $value.Length
            if ($pos -ne $pairEnd) { throw 'attribut du sujet mal formé (reste inattendu)' }
            $attributes.Add([pscustomobject] @{ Oid = (ConvertFrom-DerOid $oidBytes); Value = (ConvertFrom-DerString $value.Tag $valueBytes) })
        }
        if ($pos -ne $setEnd) { throw 'attribut du sujet mal formé (SET)' }
    }
    return , $attributes.ToArray()
}

# La décision sur le sujet : $null si c'est Microsoft, sinon la raison du refus.
function Get-SubjectRefusal([byte[]] $rawSubject) {
    try { $attributes = Get-NameAttributes $rawSubject }
    catch { return "Le sujet du certificat signataire est illisible : $($_.Exception.Message)" }
    $organizations = @($attributes | Where-Object { $_.Oid -eq $OidOrganization })
    $commonNames = @($attributes | Where-Object { $_.Oid -eq $OidCommonName })
    if ($organizations.Count -ne 1 -or $organizations[0].Value -cne $RequiredOrganization) {
        return "Le certificat signataire n'est pas celui de Microsoft Corporation (organisation : $((@($organizations | ForEach-Object { $_.Value })) -join ' | '))."
    }
    if ($commonNames.Count -ne 1 -or ($AllowedCommonNames -cnotcontains $commonNames[0].Value)) {
        return "Le nom du certificat signataire n'est pas celui d'un programme de Microsoft (nom : $((@($commonNames | ForEach-Object { $_.Value })) -join ' | '))."
    }
    return $null
}

# La décision sur la racine de la chaîne : $null si l'empreinte SHA-256 est dans la liste, sinon la raison du refus.
function Get-RootRefusal([string] $sha256) {
    if ($AllowedRoots -contains $sha256.Trim().ToLowerInvariant()) { return $null }
    return "La chaîne du certificat signataire ne remonte pas à une racine de Microsoft connue (empreinte SHA-256 de la racine : $sha256)."
}

# Les certificats que la signature Authenticode embarque (PKCS#7 de la table des certificats du fichier PE), ou un tableau vide si on n'arrive pas à les lire.
function Get-EmbeddedCertificates([System.IO.Stream] $stream) {
    try {
        $reader = New-Object System.IO.BinaryReader($stream, [System.Text.Encoding]::ASCII, $true)
        try {
            $stream.Position = 0
            if ($reader.ReadUInt16() -ne 0x5A4D) { return @() }
            $stream.Position = 0x3C
            $peOffset = [long] $reader.ReadUInt32()
            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550) { return @() }
            $stream.Position = $peOffset + 24
            $magic = $reader.ReadUInt16()
            if ($magic -eq 0x20B) { $directories = 112 } elseif ($magic -eq 0x10B) { $directories = 96 } else { return @() }
            $stream.Position = $peOffset + 24 + $directories + 4 * 8   # l'entrée 4 : la table des certificats (déplacement dans le fichier, pas une adresse virtuelle)
            $tableOffset = [long] $reader.ReadUInt32()
            $tableSize = [long] $reader.ReadUInt32()
            if ($tableOffset -eq 0 -or $tableSize -lt 8 -or $tableOffset + $tableSize -gt $stream.Length) { return @() }
            $stream.Position = $tableOffset
            $length = [long] $reader.ReadUInt32()
            [void] $reader.ReadUInt16()                      # wRevision
            $type = $reader.ReadUInt16()                     # wCertificateType : 2 = PKCS#7 SignedData
            if ($type -ne 2 -or $length -lt 8 -or $length -gt $tableSize) { return @() }
            $pkcs7 = $reader.ReadBytes([int] ($length - 8))
        }
        finally { $reader.Dispose() }
        Add-Type -AssemblyName System.Security
        $cms = New-Object System.Security.Cryptography.Pkcs.SignedCms
        $cms.Decode($pkcs7)
        return @($cms.Certificates | ForEach-Object { $_ })
    }
    catch { return @() }
}

# La chaîne du certificat signataire : sa racine doit être l'une des racines de Microsoft de la liste.
function Test-SignerChain([System.Security.Cryptography.X509Certificates.X509Certificate2] $certificate, $embedded) {
    $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
    $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
    $chain.ChainPolicy.VerificationFlags = [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::IgnoreNotTimeValid
    # Les certificats de la signature ne servent que d'intermédiaires possibles : sans eux, un pare-feu qui bloque l'infrastructure de certificats de Microsoft
    # ferait refuser le SDK à tort. Ils ne peuvent pas ouvrir de porte : Build n'aboutit que si chaque certificat est signé par le suivant et que le dernier est une
    # racine de confiance de Windows, puis l'empreinte de cette racine doit être dans la liste.
    foreach ($certificate2 in @($embedded)) { [void] $chain.ChainPolicy.ExtraStore.Add($certificate2) }
    try {
        if (-not $chain.Build($certificate) -or $chain.ChainElements.Count -lt 2) {
            $statuses = ($chain.ChainStatus | ForEach-Object { $_.Status }) -join ', '
            throw [Refusal]::new(15, "La chaîne du certificat signataire n'a pas pu être établie jusqu'à une racine de confiance ($statuses).")
        }
        $root = $chain.ChainElements[$chain.ChainElements.Count - 1].Certificate
        $sha = [System.Security.Cryptography.SHA256]::Create()
        $rootHash = ([BitConverter]::ToString($sha.ComputeHash($root.RawData)) -replace '-', '').ToLowerInvariant()
        $refusal = Get-RootRefusal $rootHash
        if ($refusal) { throw [Refusal]::new(15, $refusal) }
    }
    finally { $chain.Dispose() }
}

# Ouvre le fichier sans partage en écriture ni en suppression, puis applique toutes les vérifications à ce fichier ouvert. Rend le flux ouvert
# (à garder ouvert aussi longtemps que le fichier doit rester tel quel) ; si une vérification échoue, le flux est fermé et le refus levé.
function Open-VerifiedSdkFile {
    if (-not (Test-Path -LiteralPath $script:FilePath -PathType Leaf)) { throw [Refusal]::new(10, "Fichier introuvable : $($script:FilePath)") }
    $expected = $Sha512.Trim().ToLowerInvariant()
    if ($expected -notmatch '^[0-9a-f]{128}$') { throw [Refusal]::new(11, 'L''empreinte SHA-512 attendue est mal formée.') }

    try { $stream = New-Object System.IO.FileStream($script:FilePath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read) }
    catch { throw [Refusal]::new(10, "Le fichier n'a pas pu être ouvert sans partage en écriture : $($_.Exception.Message)") }
    try {
        $hasher = [System.Security.Cryptography.SHA512]::Create()
        $actual = ([BitConverter]::ToString($hasher.ComputeHash($stream)) -replace '-', '').ToLowerInvariant()
        if ($actual -ne $expected) { throw [Refusal]::new(11, 'L''empreinte SHA-512 du fichier n''est pas celle de releases.json.') }

        # La signature intégrée au fichier (pas une signature de catalogue), valide.
        $signature = Get-AuthenticodeSignature -LiteralPath $script:FilePath
        if ($signature.Status -ne 'Valid' -or $signature.SignatureType -ne 'Authenticode' -or -not $signature.SignerCertificate) {
            throw [Refusal]::new(12, "La signature Authenticode du fichier est absente, invalide ou pas intégrée au fichier (état : $($signature.Status), type : $($signature.SignatureType)).")
        }
        $subjectRefusal = Get-SubjectRefusal $signature.SignerCertificate.SubjectName.RawData
        if ($subjectRefusal) { throw [Refusal]::new(13, $subjectRefusal) }
        $embedded = @(Get-EmbeddedCertificates $stream)
        $script:EmbeddedCertificateCount = $embedded.Count
        Test-SignerChain $signature.SignerCertificate $embedded

        $product = [string] [System.Diagnostics.FileVersionInfo]::GetVersionInfo($script:FilePath).ProductName
        if (-not $product.StartsWith($ProductNamePrefix, [System.StringComparison]::Ordinal)) {
            throw [Refusal]::new(14, "Ce fichier n'est pas un setup du SDK .NET (produit : « $product »).")
        }
        return $stream
    }
    catch {
        $stream.Dispose()
        throw
    }
}

function Test-SdkFile {
    $script:FilePath = $Verify
    (Open-VerifiedSdkFile).Dispose()
    Write-Result @('ok', "embedded-certificates=$($script:EmbeddedCertificateCount)")
}

function Install-Sdk {
    $script:FilePath = $Install
    $stream = Open-VerifiedSdkFile
    try {
        # Le fichier reste ouvert (en lecture, sans partage en écriture ni suppression) jusqu'à la fin du setup.
        $info = New-Object System.Diagnostics.ProcessStartInfo($script:FilePath, '/install /quiet /norestart')
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $process = $null
        try { $process = [System.Diagnostics.Process]::Start($info) }
        catch { throw [Refusal]::new(20, "Le setup n'a pas pu être lancé : $($_.Exception.Message)") }
        $process.WaitForExit()
        Write-Result @("exitcode=$($process.ExitCode)")
        $process.Dispose()
    }
    finally { $stream.Dispose() }
}

function Test-SubjectText {
    $name = New-Object System.Security.Cryptography.X509Certificates.X500DistinguishedName($CheckSubject)
    $refusal = Get-SubjectRefusal $name.RawData
    if ($refusal) { throw [Refusal]::new(13, $refusal) }
    Write-Result @('ok')
}

function Test-SubjectHex {
    if ($CheckSubjectHex -notmatch '^([0-9a-fA-F]{2})+$') { throw [Refusal]::new(13, 'Le sujet donné en hexadécimal est mal formé.') }
    $bytes = New-Object byte[] ($CheckSubjectHex.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) { $bytes[$i] = [Convert]::ToByte($CheckSubjectHex.Substring(2 * $i, 2), 16) }
    $refusal = Get-SubjectRefusal $bytes
    if ($refusal) { throw [Refusal]::new(13, $refusal) }
    Write-Result @('ok')
}

function Test-RootText {
    $refusal = Get-RootRefusal $CheckRoot
    if ($refusal) { throw [Refusal]::new(15, $refusal) }
    Write-Result @('ok')
}

try {
    switch ($PSCmdlet.ParameterSetName) {
        'Verify' { Test-SdkFile }
        'Install' { Install-Sdk }
        'CheckSubject' { Test-SubjectText }
        'CheckSubjectHex' { Test-SubjectHex }
        'CheckRoot' { Test-RootText }
        default { Resolve-Sdk }
    }
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
