param(
    [Parameter(Mandatory=$true)][string]$ConfigPath,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -Path (Join-Path $PSScriptRoot 'CursorEncoder.cs') -ReferencedAssemblies System.Drawing
$configFile = (Resolve-Path -LiteralPath $ConfigPath).Path
$configFolder = Split-Path -Parent $configFile
$config = Get-Content -LiteralPath $configFile -Raw -Encoding UTF8 | ConvertFrom-Json
$ids = @('normal','text','link','unavailable','busy','move','horizontal','vertical')
$names = @('正常选择','文字输入','链接点击','不可用','忙碌中','移动画布','左右调整','上下调整')
function Resolve-Asset([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { throw '素材路径不能为空。' }
    if (-not [IO.Path]::IsPathRooted($value)) { $value = Join-Path $configFolder $value }
    return (Resolve-Path -LiteralPath $value).Path
}
$entries = @($config.cursors)
if ($entries.Count -lt 1 -or $entries.Count -gt 8) { throw '请选择 1 到 8 种指针。' }
$mapping = @{}
foreach ($item in $entries) {
    if ($ids -notcontains $item.id -or $mapping.ContainsKey($item.id)) { throw '类型不正确或重复。' }
    if (@($item.hotspot).Count -ne 2) { throw '每种指针需要两个原图热点坐标。' }
    $mapping[$item.id] = $item
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    if (@(Get-ChildItem -LiteralPath $output -Force).Count -gt 0) { throw '请选择新的空输出目录，保留以前的安装包。' }
} else { New-Item -ItemType Directory -Path $output | Out-Null }
$build = Join-Path $output 'build'
$package = Join-Path $output '喵喵就位'
New-Item -ItemType Directory -Path $build,$package | Out-Null
$selected = @()
$first = $null
for ($i = 0; $i -lt 8; $i++) {
    $enabled = $mapping.ContainsKey($ids[$i]); $selected += $enabled
    if (-not $enabled) { continue }
    $entry = $mapping[$ids[$i]]
    $image = Resolve-Asset $entry.image
    $cursor = Join-Path $build ($i.ToString()+'.cur')
    $contentScale = if ($null -ne $entry.contentScale) { [float]$entry.contentScale } else { [float]1 }
    [CursorEncoder]::ConvertScaled($image, $cursor, [int]$entry.hotspot[0], [int]$entry.hotspot[1], $contentScale)
    Copy-Item -LiteralPath $image -Destination (Join-Path $build ($i.ToString()+'.png'))
    if ($null -eq $first) { $first = $i }
}
$brand = if ($config.brand) { Resolve-Asset $config.brand } else { Join-Path $build ($first.ToString()+'.png') }; Copy-Item -LiteralPath $brand -Destination (Join-Path $build 'brand.png')
$runner = Resolve-Asset $config.runner
Copy-Item -LiteralPath $runner -Destination (Join-Path $build 'runner.png')
[CursorEncoder]::Icon((Join-Path $build ($first.ToString()+'.png')), (Join-Path $build 'cat.ico'))
$hashInput = New-Object IO.MemoryStream
for ($i = 0; $i -lt 8; $i++) {
    if ($selected[$i]) {
        $hashInput.WriteByte([byte]$i)
        $bytes = [IO.File]::ReadAllBytes((Join-Path $build ($i.ToString()+'.cur')))
        $hashInput.Write($bytes,0,$bytes.Length)
    }
}
$sha = [Security.Cryptography.SHA256]::Create()
$digest = ([BitConverter]::ToString($sha.ComputeHash($hashInput.ToArray()))).Replace('-','').Substring(0,16)
$sha.Dispose(); $hashInput.Dispose()
$template = Join-Path (Split-Path -Parent $PSScriptRoot) 'assets/windows'
$source = [IO.File]::ReadAllText((Join-Path $template 'Program.cs'))
$flags = ($selected | ForEach-Object { $_.ToString().ToLowerInvariant() }) -join ', '
$source = $source.Replace('internal static bool Changed;', ('internal static bool Changed; internal static readonly bool[] Selected = { '+$flags+' };'))
$source = $source.Replace('cursors-v1', ('cursors-'+$digest))
# Preserve unselected Windows roles and omit them from asset validation and copying.
$source = $source.Replace("for (int i = 0; i < Names.Length; i++)`r`n        {", "for (int i = 0; i < Names.Length; i++)`r`n        {`r`n            if (!Selected[i]) continue;")
$source = $source.Replace("for (int i = 0; i < Names.Length; i++)`n        {", "for (int i = 0; i < Names.Length; i++)`n        {`n            if (!Selected[i]) continue;")
$source = $source.Replace('string path = Path.Combine(Root, "cursors-', 'if (!Selected[i]) continue; string path = Path.Combine(Root, "cursors-')
$source = $source.Replace('string path = CursorPath(i);', 'if (!Selected[i]) continue; string path = CursorPath(i);')
$source = $source.Replace('for (int i = 0; i < 8; i++)', 'int visibleIndex = 0; for (int i = 0; i < 8; i++)')
$source = $source.Replace('int index = i;', 'if (!Setup.Selected[i]) continue; int layoutIndex = visibleIndex++; int index = i;')
$source = $source.Replace('D(i % 2 * 306), D(i / 2 * 100)', 'D(layoutIndex % 2 * 306), D(layoutIndex / 2 * 100)')
$source = $source.Replace('八种', ($entries.Count.ToString()+'种')).Replace('Eight bundled cursors', 'Selected bundled cursors')
$source = $source.Replace('两只小猫', '小猫们')
$rows = [int][Math]::Ceiling($entries.Count / 2.0)
$layout = 'ClientSize = new Size(D(660), D('+ (290 + $rows * 100) +')); scenarios.Height = D('+($rows * 100 + 8)+'); done.Top = D('+(228 + $rows * 100)+'); restore.Top = done.Top;'
$source = $source.Replace('ClientSize = new Size(D(660), D(690));', $layout)
$sourcePath = Join-Path $build 'Program.cs'
[IO.File]::WriteAllText($sourcePath,$source,[Text.UTF8Encoding]::new($true))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw '需要可用的 Windows .NET Framework C# 编译器。' }
$arguments = @('/nologo','/target:winexe','/optimize+','/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Xml.Linq.dll',('/win32manifest:'+(Join-Path $template 'app.manifest')),('/win32icon:'+(Join-Path $build 'cat.ico')),('/resource:'+(Join-Path $build 'runner.png')+',runner'),('/out:'+(Join-Path $package '喵喵就位.exe')))
for ($i = 0; $i -lt 8; $i++) {
    $resourceIndex = if ($selected[$i]) { $i } else { $first }
    $arguments += '/resource:'+(Join-Path $build ($resourceIndex.ToString()+'.cur'))+',cursor'+$i
    $arguments += '/resource:'+(Join-Path $build ($resourceIndex.ToString()+'.png'))+',picture'+$i
}
$arguments += '/resource:'+(Join-Path $build 'brand.png')+',brand'
$theme = New-Object Xml.XmlDocument
$themeRoot = $theme.CreateElement('theme'); [void]$theme.AppendChild($themeRoot)
for ($i=0; $i -lt 8; $i++) {
    if (-not $selected[$i]) { continue }
    $element=$theme.CreateElement('cursor')
    $element.SetAttribute('index',$i.ToString())
    $element.SetAttribute('x',([int]$mapping[$ids[$i]].hotspot[0]).ToString())
    $element.SetAttribute('y',([int]$mapping[$ids[$i]].hotspot[1]).ToString())
    [void]$themeRoot.AppendChild($element)
}
$theme.Save((Join-Path $build 'theme.xml'))
$arguments += '/resource:'+(Join-Path $build 'theme.xml')+',theme'
$arguments += Join-Path $PSScriptRoot 'CursorEncoder.cs'
$arguments += $sourcePath
& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Copy-Item -LiteralPath (Join-Path $package '喵喵就位.exe') -Destination (Join-Path $package '恢复原鼠标.exe')
$instructions = "喵喵就位`r`n`r`n解压后双击喵喵就位.exe。完成后在同窗试用所选指针。`r`n恢复时双击恢复原鼠标.exe。首次备份在 %LOCALAPPDATA%\\MiaoMiaoReady，重复安装持续保留。`r`n本包选择 $($entries.Count) 种指针。忙碌中为静态图。软件自绘指针由软件决定。`r`n本程序采用系统高分屏适配，跨不同缩放屏幕可重新运行。程序未签名，系统可能显示安全提示。`r`n"
[IO.File]::WriteAllText((Join-Path $package '使用说明.txt'),$instructions,[Text.UTF8Encoding]::new($true))
Compress-Archive -LiteralPath $package -DestinationPath (Join-Path $output '喵喵就位-安装包.zip')
Write-Output (Join-Path $output '喵喵就位-安装包.zip')
