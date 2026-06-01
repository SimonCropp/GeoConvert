<#
.SYNOPSIS
  Fails the build unless the shipped GeoConvert source has the required line coverage.

  Microsoft code coverage instruments every loaded module, so this scopes to files under src/
  (excluding the test project) and de-duplicates lines that appear in multiple class entries.
#>
param(
    [string] $Report = "TestResults/unit.cobertura.xml",
    [double] $Threshold = 100
)

[xml] $coverage = Get-Content $Report

# Map "file|line" -> max hits across all <class> entries for that file.
$lineHits = @{}
foreach ($package in $coverage.coverage.packages.package)
{
    foreach ($class in $package.classes.class)
    {
        $file = $class.filename
        if (-not $file)
        {
            continue
        }

        # Normalize separators and make the path relative to src/. Microsoft coverage emits absolute
        # paths containing /src/; coverlet emits paths already relative to its <source> root (src/).
        $relative = ($file -replace '\\', '/') -replace '^.*/src/', ''

        # Scope to the shipped projects (GeoConvert + GeoConvert.Cli); excludes the test project etc.
        if ($relative -notmatch '^GeoConvert(\.Cli)?/')
        {
            continue
        }

        foreach ($line in $class.lines.line)
        {
            $key = "$relative|$($line.number)"
            $hits = [int] $line.hits
            if (-not $lineHits.ContainsKey($key) -or $lineHits[$key] -lt $hits)
            {
                $lineHits[$key] = $hits
            }
        }
    }
}

$covered = 0
$uncovered = @{}
foreach ($entry in $lineHits.GetEnumerator())
{
    if ($entry.Value -gt 0)
    {
        $covered++
    }
    else
    {
        $parts = $entry.Key -split '\|'
        $name = $parts[0]
        if (-not $uncovered.ContainsKey($name)) { $uncovered[$name] = @() }
        $uncovered[$name] += [int] $parts[1]
    }
}

$total = $lineHits.Count
if ($total -eq 0)
{
    # An empty report means coverage collection produced nothing (the shipped source is thousands of
    # lines). Treat that as a failure rather than vacuously passing the gate.
    Write-Error "No shipped-source coverage data found in $Report. Coverage collection likely failed."
    exit 1
}

$percent = 100.0 * $covered / $total
"Line coverage (shipped source): {0}/{1} = {2:N2}%" -f $covered, $total, $percent

if ($percent -lt $Threshold)
{
    "Uncovered lines:"
    foreach ($name in ($uncovered.Keys | Sort-Object))
    {
        "  {0}: {1}" -f $name, (($uncovered[$name] | Sort-Object) -join ',')
    }

    Write-Error ("Coverage {0:N2}% is below the required {1}%." -f $percent, $Threshold)
    exit 1
}
