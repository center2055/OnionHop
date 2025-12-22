
$source = "d:\NSRS\CodingProjects\OnionHop\logo.jpg"
$dest = "d:\NSRS\CodingProjects\OnionHop\OnionHop\logo.png"

Add-Type -AssemblyName System.Drawing

if (Test-Path $source) {
    $img = [System.Drawing.Image]::FromFile($source)
    $img.Save($dest, [System.Drawing.Imaging.ImageFormat]::Png)
    $img.Dispose()
    Write-Host "Created logo.png"
} else {
    Write-Host "Error: logo.jpg not found"
}
