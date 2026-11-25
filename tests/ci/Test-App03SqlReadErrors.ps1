$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Data
. (Join-Path $PSScriptRoot '../../scripts/Get-App03SqlReadFailure.ps1')

# The provider constructors are internal. Exercise its real Number and InnerException
# properties without connecting to a database or deliberately cancelling a real query.
function New-WrappedSqlException([int]$Number) {
    $flags = [Reflection.BindingFlags]'Instance,NonPublic'
    # Framework and Core expose different optional tails after the same seven fields.
    $baseTypes = [type[]]@([int], [byte], [byte], [string], [string], [string], [int])
    $ctor = $null
    foreach ($signature in @($baseTypes, ($baseTypes + @([uint32])), ($baseTypes + @([Exception])),
            ($baseTypes + @([uint32], [Exception])))) {
        $ctor = [System.Data.SqlClient.SqlError].GetConstructor($flags, $null, [type[]]$signature, $null)
        if ($null -ne $ctor) { break }
    }
    if ($null -eq $ctor) { throw 'Unsupported SQL provider: no recognized SqlError constructor.' }
    $native = [ComponentModel.Win32Exception]::new(258)
    $argsForError = [Collections.Generic.List[object]]::new()
    foreach ($item in @($Number,[byte]0,[byte]11,'server','private error','procedure',0)) { $argsForError.Add($item) }
    foreach ($parameter in ($ctor.GetParameters() | Select-Object -Skip 7)) {
        if ($parameter.ParameterType -eq [uint32]) { $argsForError.Add([uint32]258) }
        else { $argsForError.Add($native) }
    }
    $sqlError = $ctor.Invoke($argsForError.ToArray())
    $errors = [Activator]::CreateInstance([System.Data.SqlClient.SqlErrorCollection], $true)
    $null = $errors.GetType().GetMethod('Add', $flags).Invoke($errors, @($sqlError))
    $exceptionCtor = [System.Data.SqlClient.SqlException].GetConstructor($flags, $null,
        [type[]]@([string], [System.Data.SqlClient.SqlErrorCollection], [Exception], [guid]), $null)
    if ($null -eq $exceptionCtor) { throw 'Unsupported SQL provider: no recognized SqlException constructor.' }
    $sqlException = $exceptionCtor.Invoke(@('private sql error', $errors, $native, [guid]::Empty))
    return [Reflection.TargetInvocationException]::new($sqlException)
}

foreach ($case in @(@(-2,'Open',$true), @(-2,'Broken',$false), @(1205,'Open',$false))) {
    $ex = New-WrappedSqlException $case[0]
    if ($ex.GetBaseException() -isnot [ComponentModel.Win32Exception]) { throw 'Invalid nested-error fixture.' }
    $result = Get-App03SqlReadFailure $ex $case[1]
    if ($result.Number -ne $case[0] -or $result.Continue -ne $case[2]) { throw 'Incorrect SQL error decision.' }
}
$unrelated = Get-App03SqlReadFailure ([ComponentModel.Win32Exception]::new(258)) Open
if ($null -ne $unrelated.Number -or $unrelated.Continue) { throw 'A native error alone is not a SQL timeout.' }
Write-Output 'APP-03 SQL exception classification: four cases passed.'
