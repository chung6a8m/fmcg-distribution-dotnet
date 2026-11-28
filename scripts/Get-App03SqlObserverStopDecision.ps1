# The caller owns the absolute deadline and SQL command timeout. No new database
# work after parent death, no drain for a browser-owned observer, no size overrun.
function Get-App03SqlObserverStopDecision {
    param([bool]$ParentExited, [bool]$PipeCompleted, [bool]$DrainOnStop, [bool]$AtSizeLimit)
    if ($ParentExited) { return 'parentEnded' }
    if ($AtSizeLimit) { return 'sizeLimit' }
    if (!$PipeCompleted) { return 'sample' }
    if ($DrainOnStop) { return 'drain' }
    return 'parentEnded'
}
