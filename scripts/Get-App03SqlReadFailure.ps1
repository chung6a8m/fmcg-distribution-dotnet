function Get-App03SqlReadFailure([Exception]$Failure, [System.Data.ConnectionState]$ConnectionState) {
    while ($null -ne $Failure -and $Failure -isnot [System.Data.SqlClient.SqlException]) {
        $Failure = $Failure.InnerException
    }
    $number = if ($null -ne $Failure) { $Failure.Number }
    return @{ Number=$number; Continue=($number -eq -2 -and $ConnectionState -eq 'Open') }
}
