<#
    Inno Setup's script API, as PowerShell.

    A script translated out of an installer calls the functions Inno Setup would have provided
    it. This module provides them. Everything here is named after the Inno function it stands
    in for, with an "Inno" prefix so that nothing collides with a PowerShell alias -- aliases
    win over functions in command resolution, so an unprefixed Copy or Delete would quietly
    resolve to Copy-Item or Remove-Item instead.

    What is not here is not silently absent: a translated script generates a throwing stub for
    every function it calls that this module does not define, so a run that reaches unsupported
    ground stops there rather than carrying on with a wrong answer.

    Call Initialize-InnoEnvironment before running a translated script.
#>

Set-StrictMode -Version Latest

$script:InnoConstants = @{}
$script:InnoExtractedRoot = $null
$script:InnoSilent = $false
$script:InnoFindHandles = @{}
$script:InnoNextFindHandle = 1

function Initialize-InnoEnvironment {
    <#
        .SYNOPSIS
        Supplies the state Inno Setup would have held while running the installer.

        .PARAMETER Constants
        Values for the {app}, {tmp}, {src} and similar constants the script expands. Keys are
        given without braces.

        .PARAMETER ExtractedRoot
        A directory holding the installer's already-extracted payload, which is what
        InnoExtractTemporaryFile copies from. Without it that function throws, because this
        module cannot open the installer itself.

        .PARAMETER Silent
        What InnoWizardSilent reports.
    #>
    [CmdletBinding()]
    param(
        [hashtable] $Constants = @{},
        [string] $ExtractedRoot,
        [switch] $Silent
    )

    $script:InnoConstants = @{}

    foreach ($key in $Constants.Keys) {
        $script:InnoConstants[[string]$key] = [string]$Constants[$key]
    }

    $script:InnoExtractedRoot = $ExtractedRoot
    $script:InnoSilent = [bool]$Silent
    $script:InnoFindHandles = @{}
    $script:InnoNextFindHandle = 1
}

function Get-InnoConstant {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Name)

    if ($script:InnoConstants.ContainsKey($Name)) {
        return $script:InnoConstants[$Name]
    }

    throw "The installer script expanded {$Name}, which no value was supplied for. Pass it to Initialize-InnoEnvironment -Constants."
}

#region strings and paths

function InnoExpandConstant {
    <#
        .SYNOPSIS
        Replaces every {name} in a string with the value supplied for it.

        .DESCRIPTION
        {{ is a literal brace. A {code:...} constant cannot be expanded here: it calls back into
        the script, and which routine it calls is not recorded in the compiled form.
    #>
    param([string] $S)

    if ($null -eq $S) { return '' }

    $result = [System.Text.StringBuilder]::new()
    $index = 0

    while ($index -lt $S.Length) {
        $character = $S[$index]

        if ($character -ne '{') {
            [void]$result.Append($character)
            $index++
            continue
        }

        if ($index + 1 -lt $S.Length -and $S[$index + 1] -eq '{') {
            [void]$result.Append('{')
            $index += 2
            continue
        }

        $close = $S.IndexOf('}', $index)

        if ($close -lt 0) {
            [void]$result.Append($character)
            $index++
            continue
        }

        $name = $S.Substring($index + 1, $close - $index - 1)

        if ($name -like 'code:*') {
            throw "InnoExpandConstant cannot resolve {$name}: it runs a routine in the installer script, and the compiled form does not record which."
        }

        [void]$result.Append((Get-InnoConstant -Name $name))
        $index = $close + 1
    }

    return $result.ToString()
}

function InnoAddBackslash { param([string] $S) if ($S.EndsWith('\')) { $S } else { "$S\" } }

function InnoRemoveBackslash { param([string] $S) $S.TrimEnd('\') }

function InnoRemoveBackslashUnlessRoot {
    param([string] $S)

    if ($S.Length -le 3 -and $S -match '^[A-Za-z]:\\$') { return $S }

    InnoRemoveBackslash $S
}

function InnoExtractFileName { param([string] $S) [System.IO.Path]::GetFileName($S) }

function InnoExtractFileExt { param([string] $S) [System.IO.Path]::GetExtension($S) }

function InnoExtractFileDir {
    param([string] $S)

    $directory = [System.IO.Path]::GetDirectoryName($S)

    if ($null -eq $directory) { '' } else { InnoRemoveBackslashUnlessRoot $directory }
}

function InnoExtractFilePath {
    param([string] $S)

    $directory = [System.IO.Path]::GetDirectoryName($S)

    if ([string]::IsNullOrEmpty($directory)) { '' } else { InnoAddBackslash $directory }
}

function InnoExtractFileDrive { param([string] $S) [System.IO.Path]::GetPathRoot($S).TrimEnd('\') }

function InnoChangeFileExt { param([string] $S, [string] $Ext) [System.IO.Path]::ChangeExtension($S, $Ext) }

function InnoRemoveQuotes { param([string] $S) $S.Trim('"') }

function InnoUppercase { param([string] $S) $S.ToUpperInvariant() }

function InnoLowercase { param([string] $S) $S.ToLowerInvariant() }

function InnoAnsiUppercase { param([string] $S) $S.ToUpper() }

function InnoAnsiLowercase { param([string] $S) $S.ToLower() }

function InnoTrim { param([string] $S) $S.Trim() }

function InnoLength { param($S) if ($null -eq $S) { 0 } elseif ($S -is [string]) { $S.Length } else { @($S).Count } }

function InnoPos {
    <# Pascal strings are one-based, and zero means not found. #>
    param([string] $SubStr, [string] $S)

    $S.IndexOf($SubStr, [System.StringComparison]::Ordinal) + 1
}

function InnoCopy {
    <# Copy(S, Index, Count) with a one-based index, clamped the way Pascal clamps it. #>
    param([string] $S, [int] $Index, [int] $Count)

    if ($null -eq $S -or $Index -gt $S.Length -or $Count -le 0) { return '' }

    $start = [Math]::Max(0, $Index - 1)
    $length = [Math]::Min($Count, $S.Length - $start)

    if ($length -le 0) { return '' }

    $S.Substring($start, $length)
}

function InnoDelete {
    param([ref] $S, [int] $Index, [int] $Count)

    $value = [string]$S.Value

    if ($Index -lt 1 -or $Index -gt $value.Length -or $Count -le 0) { return }

    $start = $Index - 1
    $length = [Math]::Min($Count, $value.Length - $start)

    $S.Value = $value.Remove($start, $length)
}

function InnoInsert {
    param([string] $Source, [ref] $S, [int] $Index)

    $value = [string]$S.Value
    $at = [Math]::Min([Math]::Max($Index - 1, 0), $value.Length)

    $S.Value = $value.Insert($at, $Source)
}

function InnoStringOfChar { param([string] $C, [int] $I) if ($I -le 0) { '' } else { [string]::new($C[0], $I) } }

function InnoStringChangeEx {
    param([ref] $S, [string] $FromStr, [string] $ToStr, [bool] $SupportDBCS)

    $value = [string]$S.Value
    $count = 0
    $index = 0

    while (($index = $value.IndexOf($FromStr, $index, [System.StringComparison]::OrdinalIgnoreCase)) -ge 0) {
        $value = $value.Remove($index, $FromStr.Length).Insert($index, $ToStr)
        $index += $ToStr.Length
        $count++
    }

    $S.Value = $value

    $count
}

function InnoIntToStr { param($I) [string][long]$I }

function InnoStrToInt { param([string] $S) [int]::Parse($S, [System.Globalization.CultureInfo]::InvariantCulture) }

function InnoStrToIntDef {
    param([string] $S, [int] $Default)

    $parsed = 0

    if ([int]::TryParse($S, [ref]$parsed)) { $parsed } else { $Default }
}

function InnoRound { param($E) [int][Math]::Round([double]$E, [MidpointRounding]::ToEven) }

function InnoFormat {
    param([string] $Format, $Args)

    # Pascal's format specifiers are not .NET's, so only the common integer and string cases
    # are translated. Anything else is left for the caller to see rather than mangled.
    if ($Format -match '%[^sd%]') {
        throw "InnoFormat does not translate the format string '$Format'."
    }

    $index = 0
    $values = @($Args)

    [System.Text.RegularExpressions.Regex]::Replace($Format, '%[sd%]', {
        param($match)

        if ($match.Value -eq '%%') { return '%' }

        $value = $values[$index]
        $script:null = $index++

        [string]$value
    })
}

function InnoSetLength { param([ref] $S, [int] $NewLength) $S.Value = [string]$S.Value.PadRight($NewLength).Substring(0, $NewLength) }

function InnoSetArrayLength {
    param([ref] $Arr, [int] $Count)

    $existing = @($Arr.Value)
    $result = New-Object object[] $Count

    for ($index = 0; $index -lt [Math]::Min($Count, $existing.Count); $index++) {
        $result[$index] = $existing[$index]
    }

    $Arr.Value = $result
}

function InnoGetArrayLength { param($Arr) @($Arr).Count }

function InnoInc { param([ref] $X) $X.Value = $X.Value + 1 }

function InnoDec { param([ref] $X) $X.Value = $X.Value - 1 }

#endregion

#region files and directories

function InnoFileExists { param([string] $Name) Test-Path -LiteralPath $Name -PathType Leaf }

function InnoDirExists { param([string] $Name) Test-Path -LiteralPath $Name -PathType Container }

function InnoFileOrDirExists { param([string] $Name) Test-Path -LiteralPath $Name }

function InnoFileSize {
    param([string] $Name, [ref] $Size)

    if (-not (Test-Path -LiteralPath $Name -PathType Leaf)) { return $false }

    $Size.Value = [int](Get-Item -LiteralPath $Name).Length

    $true
}

function InnoFileCopy {
    param([string] $ExistingFile, [string] $NewFile, [bool] $FailIfExists)

    if ($FailIfExists -and (Test-Path -LiteralPath $NewFile)) { return $false }

    try {
        $directory = [System.IO.Path]::GetDirectoryName($NewFile)

        if ($directory -and -not (Test-Path -LiteralPath $directory)) {
            $null = New-Item -ItemType Directory -Path $directory -Force
        }

        Copy-Item -LiteralPath $ExistingFile -Destination $NewFile -Force
        $true
    }
    catch { $false }
}

function InnoDeleteFile {
    param([string] $FileName)

    try { Remove-Item -LiteralPath $FileName -Force -ErrorAction Stop; $true } catch { $false }
}

function InnoRenameFile {
    param([string] $OldName, [string] $NewName)

    try { Move-Item -LiteralPath $OldName -Destination $NewName -Force -ErrorAction Stop; $true } catch { $false }
}

function InnoCreateDir {
    param([string] $Dir)

    try { $null = New-Item -ItemType Directory -Path $Dir -Force -ErrorAction Stop; $true } catch { $false }
}

function InnoForceDirectories { param([string] $Dir) InnoCreateDir $Dir }

function InnoRemoveDir {
    param([string] $Dir)

    try { Remove-Item -LiteralPath $Dir -Force -ErrorAction Stop; $true } catch { $false }
}

function InnoDelTree {
    param([string] $Path, [bool] $IsDir, [bool] $DeleteFiles, [bool] $DeleteSubdirsAlso)

    try { Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop; $true } catch { $false }
}

function InnoFindFirst {
    <#
        Inno's find record is a cursor. Here it is a hashtable holding the remaining matches, so
        InnoFindNext can advance it and InnoFindClose can release it.
    #>
    param([string] $FileName, [ref] $FindRec)

    # Not $matches: that name is PowerShell's own, and the regex engine overwrites it.
    $entries = @(Get-ChildItem -Path $FileName -Force -ErrorAction SilentlyContinue)

    if ($entries.Count -eq 0) {
        $FindRec.Value = $null
        return $false
    }

    $first = $entries[0]

    $FindRec.Value = [pscustomobject]@{
        Field0    = $first.Name
        Field1    = [int]$first.Attributes
        Field2    = $(if ($first.PSIsContainer) { 0 } else { [int]$first.Length })
        Remaining = [System.Collections.Generic.Queue[object]]::new([object[]]@($entries | Select-Object -Skip 1))
    }

    $true
}

function InnoFindNext {
    param([ref] $FindRec)

    $record = $FindRec.Value

    if ($null -eq $record -or $record.Remaining.Count -eq 0) { return $false }

    $next = $record.Remaining.Dequeue()

    $record.Field0 = $next.Name
    $record.Field1 = [int]$next.Attributes
    $record.Field2 = $(if ($next.PSIsContainer) { 0 } else { [int]$next.Length })

    $true
}

function InnoFindClose { param([ref] $FindRec) $FindRec.Value = $null }

function InnoLoadStringsFromFile {
    param([string] $FileName, [ref] $S)

    if (-not (Test-Path -LiteralPath $FileName -PathType Leaf)) { return $false }

    $S.Value = @(Get-Content -LiteralPath $FileName)

    $true
}

function InnoSaveStringToFile {
    param([string] $FileName, [string] $S, [bool] $Append)

    try {
        if ($Append) { Add-Content -LiteralPath $FileName -Value $S -NoNewline }
        else { Set-Content -LiteralPath $FileName -Value $S -NoNewline }

        $true
    }
    catch { $false }
}

function InnoGetSpaceOnDisk {
    param([string] $Path, [bool] $InMegabytes, [ref] $Free, [ref] $Total)

    $root = [System.IO.Path]::GetPathRoot((Resolve-Path -LiteralPath $Path -ErrorAction SilentlyContinue))

    if (-not $root) { return $false }

    $drive = [System.IO.DriveInfo]::new($root)
    $divisor = if ($InMegabytes) { 1MB } else { 1 }

    $Free.Value = [int]($drive.AvailableFreeSpace / $divisor)
    $Total.Value = [int]($drive.TotalSize / $divisor)

    $true
}

function InnoExtractTemporaryFile {
    <#
        Inno extracts the named file out of the running installer. This module has no installer
        to read, so it copies from the directory Initialize-InnoEnvironment was given.
    #>
    param([string] $FileName)

    if (-not $script:InnoExtractedRoot) {
        throw "InnoExtractTemporaryFile('$FileName') needs the installer's payload. Pass -ExtractedRoot to Initialize-InnoEnvironment."
    }

    $source = Join-Path $script:InnoExtractedRoot $FileName
    $destination = Join-Path (Get-InnoConstant -Name 'tmp') $FileName

    if (-not (Test-Path -LiteralPath $source)) {
        throw "InnoExtractTemporaryFile('$FileName') found no such file under $script:InnoExtractedRoot."
    }

    $directory = [System.IO.Path]::GetDirectoryName($destination)

    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        $null = New-Item -ItemType Directory -Path $directory -Force
    }

    Copy-Item -LiteralPath $source -Destination $destination -Force
}

#endregion

#region registry

# 0x80000000 on its own parses as a negative Int32, so each root carries the long suffix.
$script:InnoRegistryRoots = @{
    [uint32]0x80000000L = 'HKEY_CLASSES_ROOT'
    [uint32]0x80000001L = 'HKEY_CURRENT_USER'
    [uint32]0x80000002L = 'HKEY_LOCAL_MACHINE'
    [uint32]0x80000003L = 'HKEY_USERS'
    [uint32]0x80000005L = 'HKEY_CURRENT_CONFIG'
}

function Get-InnoRegistryPath {
    param($RootKey, [string] $SubKeyName)

    if (-not $IsWindows) {
        throw "The installer script reads the Windows registry, which this platform does not have."
    }

    # Inno passes the root as a signed integer, so the sign has to be put back before lookup.
    $root = [uint32]([int64]$RootKey -band 0xFFFFFFFF)

    if (-not $script:InnoRegistryRoots.ContainsKey($root)) {
        throw ("Unknown registry root 0x{0:X8}." -f $root)
    }

    "Registry::$($script:InnoRegistryRoots[$root])\$SubKeyName"
}

function InnoRegKeyExists {
    param($RootKey, [string] $SubKeyName)

    Test-Path -LiteralPath (Get-InnoRegistryPath $RootKey $SubKeyName)
}

function InnoRegValueExists {
    param($RootKey, [string] $SubKeyName, [string] $ValueName)

    $path = Get-InnoRegistryPath $RootKey $SubKeyName

    if (-not (Test-Path -LiteralPath $path)) { return $false }

    $null -ne (Get-ItemProperty -LiteralPath $path -Name $ValueName -ErrorAction SilentlyContinue)
}

function InnoRegQueryStringValue {
    param($RootKey, [string] $SubKeyName, [string] $ValueName, [ref] $ResultStr)

    $path = Get-InnoRegistryPath $RootKey $SubKeyName
    $property = Get-ItemProperty -LiteralPath $path -Name $ValueName -ErrorAction SilentlyContinue

    if ($null -eq $property) { return $false }

    $ResultStr.Value = [string]$property.$ValueName

    $true
}

function InnoRegQueryDWordValue {
    param($RootKey, [string] $SubKeyName, [string] $ValueName, [ref] $ResultDWord)

    $path = Get-InnoRegistryPath $RootKey $SubKeyName
    $property = Get-ItemProperty -LiteralPath $path -Name $ValueName -ErrorAction SilentlyContinue

    if ($null -eq $property) { return $false }

    $ResultDWord.Value = [int]$property.$ValueName

    $true
}

function InnoRegWriteStringValue {
    param($RootKey, [string] $SubKeyName, [string] $ValueName, [string] $Data)

    $path = Get-InnoRegistryPath $RootKey $SubKeyName

    if (-not (Test-Path -LiteralPath $path)) { $null = New-Item -Path $path -Force }

    $null = New-ItemProperty -LiteralPath $path -Name $ValueName -Value $Data -PropertyType String -Force

    $true
}

function InnoRegWriteDWordValue {
    param($RootKey, [string] $SubKeyName, [string] $ValueName, [int] $Data)

    $path = Get-InnoRegistryPath $RootKey $SubKeyName

    if (-not (Test-Path -LiteralPath $path)) { $null = New-Item -Path $path -Force }

    $null = New-ItemProperty -LiteralPath $path -Name $ValueName -Value $Data -PropertyType DWord -Force

    $true
}

function InnoRegDeleteKeyIncludingSubkeys {
    param($RootKey, [string] $SubKeyName)

    $path = Get-InnoRegistryPath $RootKey $SubKeyName

    try { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop; $true } catch { $false }
}

function InnoRegGetSubkeyNames {
    param($RootKey, [string] $SubKeyName, [ref] $Names)

    $path = Get-InnoRegistryPath $RootKey $SubKeyName

    if (-not (Test-Path -LiteralPath $path)) { return $false }

    $Names.Value = @(Get-ChildItem -LiteralPath $path | ForEach-Object { $_.PSChildName })

    $true
}

function InnoRegGetValueNames {
    param($RootKey, [string] $SubKeyName, [ref] $Names)

    $path = Get-InnoRegistryPath $RootKey $SubKeyName

    if (-not (Test-Path -LiteralPath $path)) { return $false }

    $Names.Value = @((Get-Item -LiteralPath $path).GetValueNames())

    $true
}

#endregion

#region ini files

function Read-InnoIni {
    param([string] $FileName)

    $sections = @{}
    $current = ''

    if (-not (Test-Path -LiteralPath $FileName -PathType Leaf)) { return $sections }

    foreach ($line in Get-Content -LiteralPath $FileName) {
        $trimmed = $line.Trim()

        if ($trimmed -match '^\[(.*)\]$') { $current = $Matches[1]; continue }
        if ($trimmed -match '^\s*[;#]' -or -not $trimmed.Contains('=')) { continue }

        $split = $trimmed.IndexOf('=')
        $key = $trimmed.Substring(0, $split).Trim()

        if (-not $sections.ContainsKey($current)) { $sections[$current] = @{} }

        $sections[$current][$key] = $trimmed.Substring($split + 1).Trim()
    }

    $sections
}

function InnoGetIniString {
    param([string] $Section, [string] $Key, [string] $Default, [string] $FileName)

    $ini = Read-InnoIni $FileName

    if ($ini.ContainsKey($Section) -and $ini[$Section].ContainsKey($Key)) { $ini[$Section][$Key] } else { $Default }
}

function InnoGetIniInt {
    param([string] $Section, [string] $Key, [int] $Default, [int] $Min, [int] $Max, [string] $FileName)

    $value = InnoGetIniString $Section $Key '' $FileName
    $parsed = 0

    if (-not [int]::TryParse($value, [ref]$parsed)) { return $Default }
    if ($Min -ne $Max -and ($parsed -lt $Min -or $parsed -gt $Max)) { return $Default }

    $parsed
}

function InnoGetIniBool {
    param([string] $Section, [string] $Key, [bool] $Default, [string] $FileName)

    $value = InnoGetIniString $Section $Key '' $FileName

    if ($value -eq '') { $Default } else { $value -eq '1' }
}

function InnoIniKeyExists {
    param([string] $Section, [string] $Key, [string] $FileName)

    $ini = Read-InnoIni $FileName

    $ini.ContainsKey($Section) -and $ini[$Section].ContainsKey($Key)
}

function InnoSetIniString {
    param([string] $Section, [string] $Key, [string] $Value, [string] $FileName)

    $lines = if (Test-Path -LiteralPath $FileName) { @(Get-Content -LiteralPath $FileName) } else { @() }
    $output = [System.Collections.Generic.List[string]]::new()

    $inSection = $false
    $written = $false
    $sectionSeen = $false

    foreach ($line in $lines) {
        $trimmed = $line.Trim()

        if ($trimmed -match '^\[(.*)\]$') {
            if ($inSection -and -not $written) { $output.Add("$Key=$Value"); $written = $true }

            $inSection = $Matches[1] -eq $Section
            $sectionSeen = $sectionSeen -or $inSection
        }
        elseif ($inSection -and $trimmed -match "^\s*$([regex]::Escape($Key))\s*=") {
            $output.Add("$Key=$Value")
            $written = $true

            continue
        }

        $output.Add($line)
    }

    if (-not $sectionSeen) { $output.Add("[$Section]"); $output.Add("$Key=$Value") }
    elseif (-not $written) { $output.Add("$Key=$Value") }

    Set-Content -LiteralPath $FileName -Value $output

    $true
}

function InnoSetIniBool {
    param([string] $Section, [string] $Key, [bool] $Value, [string] $FileName)

    InnoSetIniString $Section $Key $(if ($Value) { '1' } else { '0' }) $FileName
}

#endregion

#region processes and the wizard

function InnoExec {
    param([string] $Filename, [string] $Params, [string] $WorkingDir, [int] $ShowCmd, [int] $Wait, [ref] $ResultCode)

    try {
        $arguments = @{ FilePath = $Filename; PassThru = $true; NoNewWindow = $true }

        if ($Params) { $arguments['ArgumentList'] = $Params }
        if ($WorkingDir) { $arguments['WorkingDirectory'] = $WorkingDir }

        $process = Start-Process @arguments -Wait:($Wait -ne 0)

        if ($Wait -ne 0) { $ResultCode.Value = $process.ExitCode } else { $ResultCode.Value = 0 }

        $true
    }
    catch { $false }
}

function InnoShellExec {
    param([string] $Verb, [string] $Filename, [string] $Params, [string] $WorkingDir, [int] $ShowCmd, [int] $Wait, [ref] $ResultCode)

    InnoExec $Filename $Params $WorkingDir $ShowCmd $Wait $ResultCode
}

function InnoSleep { param([int] $Milliseconds) Start-Sleep -Milliseconds $Milliseconds }

function InnoLog { param([string] $S) Write-Verbose $S }

function InnoMsgBox {
    <#
        There is no wizard to show a dialog on, so the message is written to the host and the
        default answer is returned: IDOK for a plain message, IDYES for a question.
    #>
    param([string] $Text, [int] $Type, [int] $Buttons)

    Write-Host "[installer] $Text"

    if ($Buttons -eq 4) { 6 } else { 1 }
}

function InnoAbort { throw 'The installer script called Abort.' }

function InnoParamCount { $script:InnoScriptArgs.Count }

function InnoParamStr { param([int] $Index) if ($Index -ge 1 -and $Index -le $script:InnoScriptArgs.Count) { $script:InnoScriptArgs[$Index - 1] } else { '' } }

$script:InnoScriptArgs = @()

function InnoIsWin64 { [Environment]::Is64BitOperatingSystem }

function InnoIs64BitInstallMode { [Environment]::Is64BitOperatingSystem }

function InnoIsUninstaller { $false }

function InnoWizardSilent { $script:InnoSilent }

function InnoGetDateTimeString {
    param([string] $DateTimeFormat, [string] $DateSeparator, [string] $TimeSeparator)

    (Get-Date).ToString($DateTimeFormat.Replace('/', $DateSeparator).Replace(':', $TimeSeparator))
}

function InnoGetWindowsVersion { [int]([Environment]::OSVersion.Version.Major * 0x1000000 + [Environment]::OSVersion.Version.Minor * 0x10000 + [Environment]::OSVersion.Version.Build) }

function InnoGetWindowsVersionEx {
    param([ref] $Version)

    $os = [Environment]::OSVersion.Version

    $Version.Value = [pscustomobject]@{
        Field0 = $os.Major
        Field1 = $os.Minor
        Field2 = $os.Build
        Field3 = 0
        Field4 = 0
        Field5 = 0
        Field6 = $true
    }
}

#endregion

#region operators whose meaning depends on the operand

function InnoAnd {
    <#
        Pascal Script compiles a logical and a bitwise AND to the same instruction, so which one
        was meant is decided here from what the operands turn out to be.
    #>
    param($Left, $Right)

    if ($Left -is [bool] -or $Right -is [bool]) { [bool]$Left -and [bool]$Right } else { [int]$Left -band [int]$Right }
}

function InnoOr {
    param($Left, $Right)

    if ($Left -is [bool] -or $Right -is [bool]) { [bool]$Left -or [bool]$Right } else { [int]$Left -bor [int]$Right }
}

function InnoXor {
    param($Left, $Right)

    if ($Left -is [bool] -or $Right -is [bool]) { [bool]$Left -xor [bool]$Right } else { [int]$Left -bxor [int]$Right }
}

function InnoNot {
    param($Value)

    if ($Value -is [bool]) { -not $Value } else { -bnot [int]$Value }
}

function InnoIn {
    param($Value, $Set)

    @($Set) -contains $Value
}

function InnoIs {
    <#
        Pascal's "is" tests an object against a class Inno Setup registered. The class does not
        exist here, so the test cannot be answered rather than answered wrongly.
    #>
    param($Value, $Type)

    throw "The installer script used 'is' to test a type. Inno Setup's class model is not available outside the installer."
}

#endregion

#region the parts that need the installer itself

function Invoke-InnoMethod {
    <#
        Calls a method on one of the objects Inno Setup registers -- the wizard form, its
        controls, string lists. None of them exist outside a running installer, so this throws
        rather than pretending the call did something.
    #>
    param([Parameter(Mandatory)] $Instance, [Parameter(Mandatory)][string] $Method, $Arguments = @())

    throw "The installer script called $Method on a $(if ($null -eq $Instance) { 'null' } else { $Instance.GetType().Name }). Inno Setup's object model is not available outside the installer."
}

function Invoke-InnoNative {
    <#
        Calls into a native library the installer shipped. Translating the call is possible; doing
        it safely is not, because the library is the installer's own and its calling convention is
        recorded only as a packed byte.
    #>
    param([Parameter(Mandatory)][string] $Library, [Parameter(Mandatory)][string] $EntryPoint, $Arguments = @())

    throw "The installer script called $EntryPoint in $Library. Native calls are not translated."
}

#endregion

Export-ModuleMember -Function 'Inno*', 'Initialize-InnoEnvironment', 'Invoke-InnoMethod', 'Invoke-InnoNative', 'Get-InnoConstant'
