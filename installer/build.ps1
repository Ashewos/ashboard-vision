#Requires -Version 7
<#
.SYNOPSIS
    Fabrique l'installateur d'Ashboard Vision : installer/out/Ashboard-Vision-Setup-<version>.exe et son empreinte .sha256.

.DESCRIPTION
    1. publie Trofeo.Display, Trofeo.Settings, Trofeo.SensorHelper et Trofeo.ModuleHost (l'hôte des modules complémentaires) (Release, win-x64,
       autonome) dans installer/publish/ ;
    2. y ajoute les mods, les licences et le fichier LICENSE ;
    3. lit l'adresse officielle, la version et l'empreinte de PawnIO (pawnio.json) pour les donner à l'installateur, qui le télécharge
       pendant l'installation (PawnIO n'est jamais embarqué) ;
    4. fabrique les quatre paquets du SDK des modules complémentaires (AshboardVision.Sdk, .Sdk.Testing, .Sdk.Tool, .Sdk.Templates ; Release,
       dotnet pack) dans installer/publish/sdk/ : l'installateur les propose (case « Outils pour créer des modules (SDK) ») et la recette les
       joint à la version GitHub ; ils ne vont jamais sur nuget.org. Ils portent la version du SDK (1.0.x, fixée dans leurs .csproj), pas celle
       du logiciel : le paramètre -Version ne leur est jamais passé ;
    5. compile installer/ashboard-vision.iss avec Inno Setup 7.1 ou plus ;
    6. écrit le .sha256 au format « <empreinte>  <nom du fichier> » et copie les quatre .nupkg à côté de l'installateur, dans installer/out/.

    La publication passe par installer/artifacts/ (--artifacts-path) : les dossiers bin et obj des projets ne sont jamais touchés,
    ce qui laisse tranquille le service lancé depuis src/Trofeo.Display/bin/Release.

.PARAMETER Version
    Numéro SemVer de la version (1.0.0-beta.1, 1.0.0...).

.PARAMETER Iscc
    Chemin d'ISCC.exe. Par défaut : C:\Program Files\Inno Setup 7\ISCC.exe, sinon celui du PATH.

.EXAMPLE
    pwsh installer/build.ps1 -Version 1.0.0-beta.1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$')]
    [string] $Version,

    [string] $Iscc
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$MinimumInno = [version]'7.1'
$installerDir = $PSScriptRoot
$repo = Split-Path $installerDir -Parent
$publishDir = Join-Path $installerDir 'publish'
$artifactsDir = Join-Path $installerDir 'artifacts'
$outDir = Join-Path $installerDir 'out'
$sdkDir = Join-Path $publishDir 'sdk'
$setupName = "Ashboard-Vision-Setup-$Version.exe"

# Les quatre paquets du SDK, avec le projet de chacun. Ce sont eux, et eux seuls, qui sortent de Pack-Sdk.
$sdkPackages = [ordered]@{
    'AshboardVision.Sdk'           = 'src/AshboardVision.Sdk/AshboardVision.Sdk.csproj'
    'AshboardVision.Sdk.Testing'   = 'src/AshboardVision.Sdk.Testing/AshboardVision.Sdk.Testing.csproj'
    'AshboardVision.Sdk.Tool'      = 'tools/AshboardVision.Sdk.Tool/AshboardVision.Sdk.Tool.csproj'
    'AshboardVision.Sdk.Templates' = 'templates/AshboardVision.Sdk.Templates/AshboardVision.Sdk.Templates.csproj'
}

function Step([string] $message) { Write-Host "==> $message" -ForegroundColor Cyan }

function Find-Iscc {
    if ($Iscc) {
        if (-not (Test-Path -LiteralPath $Iscc)) { throw "ISCC.exe introuvable : $Iscc" }
        return (Resolve-Path -LiteralPath $Iscc).Path
    }
    $default = Join-Path ${env:ProgramFiles} 'Inno Setup 7\ISCC.exe'
    if (Test-Path -LiteralPath $default) { return $default }
    $found = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($found) { return $found.Source }
    throw "Inno Setup 7.1 ou plus est introuvable (cherché : $default, puis le PATH). Installe-le, ou passe -Iscc <chemin d'ISCC.exe>."
}

function Assert-InnoVersion([string] $isccPath) {
    # Les propriétés d'ISCC.exe disent 0.0.0.0 : la version est celle que le compilateur annonce au début de toute compilation.
    # Un script vide suffit (il échoue ensuite, sans importance) ; /O- évite d'écrire quoi que ce soit.
    $announce = "[Setup]`nAppName=x`n" | & $isccPath '/O-' '-' 2>&1 | Out-String
    if ($announce -notmatch 'Compiler engine version: Inno Setup (\d+(\.\d+){1,3})') {
        throw "Version d'Inno Setup illisible dans ce que $isccPath annonce."
    }
    $found = [version]$Matches[1]
    if ($found -lt $MinimumInno) { throw "Inno Setup $found est trop ancien : il faut la version $MinimumInno ou plus (le thème dynamique et l'installateur 64 bits viennent de là)." }
    Write-Host "    Inno Setup $found ($isccPath)"
}

function Publish-Project([string] $project) {
    Step "Publication de $project"
    # Dossier d'artefacts à part : ni src/*/bin ni src/*/obj ne sont écrits. -o rassemble les quatre programmes au même endroit.
    & dotnet publish (Join-Path $repo "src/$project/$project.csproj") `
        -c Release -r win-x64 --self-contained `
        -o $publishDir `
        --artifacts-path $artifactsDir `
        -p:Version=$Version `
        -p:DebugType=none -p:DebugSymbols=false -p:SatelliteResourceLanguages=fr `
        --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "La publication de $project a échoué (code $LASTEXITCODE)." }
}

function Pack-Sdk {
    Step 'Paquets du SDK (AshboardVision.Sdk, .Testing, .Tool, .Templates)'
    New-Item -ItemType Directory -Force -Path $sdkDir | Out-Null
    foreach ($id in $sdkPackages.Keys) {
        # Pas de -p:Version : ces paquets ont leur propre numéro (celui du SDK, dans leur .csproj), à part de celui du logiciel.
        # Pas de --no-restore non plus : le .nuspec de AshboardVision.Sdk n'est sans dépendance Trofeo.ModuleKit que si pack restaure.
        & dotnet pack (Join-Path $repo $sdkPackages[$id]) `
            -c Release `
            -o $sdkDir `
            --artifacts-path $artifactsDir `
            --nologo -v:minimal
        if ($LASTEXITCODE -ne 0) { throw "La fabrication du paquet $id a échoué (code $LASTEXITCODE)." }
    }

    # Exactement quatre paquets, un par identifiant (ni symboles ni reste d'un autre numéro).
    $found = @(Get-ChildItem -LiteralPath $sdkDir -File)
    $expected = @($sdkPackages.Keys | ForEach-Object { $id = $_; $found | Where-Object { $_.Name -match ('^' + [regex]::Escape($id) + '\.\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?\.nupkg$') } })
    if ($found.Count -ne $sdkPackages.Count -or $expected.Count -ne $sdkPackages.Count) {
        throw "Les paquets du SDK attendus sont $($sdkPackages.Count) (un par identifiant) ; trouvés dans $sdkDir : $(($found | ForEach-Object Name) -join ', ')."
    }
    foreach ($package in $found) { Write-Host "    $($package.Name)" }
}

function Copy-Mods {
    Step 'Copie des mods (la version du mod suit celle du logiciel)'
    $source = Join-Path $repo 'mods'
    $target = Join-Path $publishDir 'mods'
    Copy-Item -LiteralPath $source -Destination $target -Recurse
    # Les tests du mod ne servent pas à l'utilisateur.
    Get-ChildItem -LiteralPath $target -Recurse -File -Filter '*.test.ts' | Remove-Item -Force

    # `claude plugin update` ne rafraîchit que si la version du mod change : la copie publiée prend celle du logiciel
    # (le fichier du dépôt reste tel quel).
    $manifest = Join-Path $target 'ashboard-quotas/.claude-plugin/plugin.json'
    $json = Get-Content -LiteralPath $manifest -Raw -Encoding utf8 | ConvertFrom-Json
    $json.version = $Version
    $text = $json | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($manifest, $text + "`n", [System.Text.UTF8Encoding]::new($false))
}

function Copy-Licences {
    Step 'Copie des licences'
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $publishDir 'LICENSE')
    Copy-Item -LiteralPath (Join-Path $installerDir 'LICENCES-TIERCES.txt') -Destination (Join-Path $publishDir 'LICENCES-TIERCES.txt')

    # Les textes de licence que les paquets (et les paquets d'exécution de .NET) fournissent eux-mêmes, d'après les .deps.json de la publication.
    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' }
    $target = Join-Path $publishDir 'licences'
    $libraries = Get-ChildItem -LiteralPath $publishDir -Filter '*.deps.json' | ForEach-Object {
        (Get-Content -LiteralPath $_.FullName -Raw -Encoding utf8 | ConvertFrom-Json).libraries.PSObject.Properties
    }
    $copied = 0
    foreach ($library in $libraries | Where-Object { $_.Value.type -in 'package', 'runtimepack' } | Sort-Object Name -Unique) {
        $id, $packageVersion = $library.Name -split '/', 2
        $id = $id -replace '^runtimepack\.', ''
        $packageDir = Join-Path $nugetRoot "$($id.ToLowerInvariant())/$packageVersion"
        if (-not (Test-Path -LiteralPath $packageDir)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $packageDir -File | Where-Object { $_.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE|THIRD-PARTY-NOTICES)([.-].*)?$' }) {
            $destination = Join-Path $target $id
            New-Item -ItemType Directory -Force -Path $destination | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $file.Name)
            $copied++
        }
    }
    if ($copied -eq 0) { throw "Aucun texte de licence de paquet trouvé dans $nugetRoot : le cache NuGet n'a pas été restauré ?" }
    Write-Host "    $copied texte(s) de licence de paquets copié(s) dans licences/."

    # Les licences OFL des polices sont déjà à côté du programme (Assets/Fonts) : on le vérifie plutôt que de le supposer.
    $ofl = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File -Filter 'OFL-*.txt')
    if ($ofl.Count -lt 4) { throw "Les licences OFL des polices manquent dans la publication ($($ofl.Count) trouvée(s) sur 4)." }
}

function Read-PawnIo {
    $config = Get-Content -LiteralPath (Join-Path $installerDir 'pawnio.json') -Raw -Encoding utf8 | ConvertFrom-Json
    if ($config.url -notmatch '^https://github[.]com/namazso/PawnIO[.]Setup/releases/download/') { throw "pawnio.json : l'adresse de PawnIO n'est pas celle du dépôt officiel namazso/PawnIO.Setup ($($config.url))." }
    if ($config.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'pawnio.json : l''empreinte SHA-256 de PawnIO est mal formée.' }
    Write-Host "    PawnIO $($config.version) : téléchargé par l'installateur depuis $($config.url) (SHA-256 vérifié par Inno Setup)."
    return $config
}

# --- Début -------------------------------------------------------------------------------------------------------------------

Step 'Outils'
$isccPath = Find-Iscc
Assert-InnoVersion $isccPath

Step 'Dossiers de travail'
if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
if (Test-Path -LiteralPath $outDir) {
    Get-ChildItem -LiteralPath $outDir -Filter 'Ashboard-Vision-Setup-*' | Remove-Item -Force
    Get-ChildItem -LiteralPath $outDir -Filter 'AshboardVision.Sdk*.nupkg' | Remove-Item -Force
}
New-Item -ItemType Directory -Force -Path $publishDir, $outDir | Out-Null

foreach ($project in 'Trofeo.Display', 'Trofeo.Settings', 'Trofeo.SensorHelper', 'Trofeo.ModuleHost') { Publish-Project $project }
foreach ($exe in 'Trofeo.Display.exe', 'Trofeo.Settings.exe', 'Trofeo.SensorHelper.exe', 'Trofeo.ModuleHost.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir $exe))) { throw "$exe manque dans la publication." }
}
# Les symboles des bibliothèques natives (SkiaSharp, HarfBuzz) viennent avec leurs paquets : l'utilisateur n'en a pas l'usage.
Get-ChildItem -LiteralPath $publishDir -Recurse -File -Filter '*.pdb' | Remove-Item -Force

Pack-Sdk
Copy-Mods
Copy-Licences

Step 'PawnIO'
$pawnIo = Read-PawnIo

Step 'Compilation de l''installateur'
$isccArgs = @("/DAppVersion=$Version", "/DSourceDir=$publishDir", "/DOutputDir=$outDir", "/DSdkDir=$sdkDir", "/DPawnIoUrl=$($pawnIo.url)", "/DPawnIoSha256=$($pawnIo.sha256.ToLowerInvariant())")
$logPath = Join-Path $outDir 'iscc.log'
& $isccPath @isccArgs (Join-Path $installerDir 'ashboard-vision.iss') 2>&1 | Tee-Object -FilePath $logPath
if ($LASTEXITCODE -ne 0) { throw "ISCC a échoué (code $LASTEXITCODE). Journal : $logPath" }

$setup = Join-Path $outDir $setupName
if (-not (Test-Path -LiteralPath $setup)) { throw "L'installateur attendu est introuvable : $setup" }

Step 'Empreinte SHA-256'
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
# Format de sha256sum : « <empreinte>  <nom> », fin de ligne LF, sans BOM (le logiciel relit ce fichier avant de lancer l'installateur).
[System.IO.File]::WriteAllText("$setup.sha256", "$hash  $setupName`n", [System.Text.UTF8Encoding]::new($false))

# Les paquets du SDK sont joints à la version GitHub à côté de l'installateur (la recette les prend dans installer/out/).
Copy-Item -Path (Join-Path $sdkDir '*.nupkg') -Destination $outDir

$size = [math]::Round((Get-Item -LiteralPath $setup).Length / 1MB, 1)
Write-Host ''
Write-Host "Installateur : $setup ($size Mo)" -ForegroundColor Green
Write-Host "Empreinte    : $hash"
foreach ($package in Get-ChildItem -LiteralPath $outDir -Filter 'AshboardVision.Sdk*.nupkg') { Write-Host "Paquet SDK   : $($package.FullName)" }
