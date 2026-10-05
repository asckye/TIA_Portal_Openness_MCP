# Decode the single TextContent shared by V4 MCP and reflection callers.
function ConvertFrom-McpResult($Result) {
    $text=@($Result.Content | Where-Object { $null -ne $_.Text })
    if($text.Count -ne 1){throw 'Expected one V4 TextContent'}
    $value=$text[0].Text | ConvertFrom-Json
    if($value.schemaVersion -ne 4 -or $value.ok -isnot [bool] -or $null -eq $value.meta -or
       !($value.PSObject.Properties.Name -contains 'data') -or !($value.PSObject.Properties.Name -contains 'error') -or
       ($value.ok -and $null -ne $value.error) -or (!$value.ok -and $null -eq $value.error)) {
        throw 'Invalid V4 envelope'
    }
    if([bool]$Result.IsError -ne !$value.ok){throw 'V4 isError/ok mismatch'}
    return $value
}
