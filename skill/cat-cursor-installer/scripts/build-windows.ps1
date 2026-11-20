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
if ($config.cats) {
    $bytes = [IO.File]::ReadAllBytes($configFile)
    $hashInput.Write($bytes,0,$bytes.Length)
    foreach ($cat in $config.cats) {
        foreach ($candidate in $cat.cursors) {
            $bytes = [IO.File]::ReadAllBytes((Resolve-Asset $candidate.image))
            $hashInput.Write($bytes,0,$bytes.Length)
        }
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
if ($config.cats) {
    if ($entries.Count -ne 8) { throw '可分配的猫咪主题必须包含八种指针。' }
    $catalog = New-Object Xml.XmlDocument
    $root = $catalog.CreateElement('cats')
    [void]$catalog.AppendChild($root)
    $defaultCats = @($config.defaultCats)
    if ($defaultCats.Count -ne 8) { throw '请为八种指针指定默认猫咪。' }
    $root.SetAttribute('defaults',($defaultCats -join ','))
    for ($catIndex=0; $catIndex -lt $config.cats.Count; $catIndex++) {
        $cat = $config.cats[$catIndex]
        $entry = $catalog.CreateElement('cat')
        $entry.SetAttribute('name',[string]$cat.name)
        [void]$root.AppendChild($entry)
        for ($i=0; $i -lt 8; $i++) {
            if ($defaultCats[$i] -eq $catIndex) { continue }
            $variant = @($cat.cursors | Where-Object id -eq $ids[$i])
            if ($variant.Count -ne 1) { throw "缺少猫咪造型：$($cat.name) / $($ids[$i])" }
            $variant = $variant[0]
            $number = $catIndex + 1
            $png = Resolve-Asset $variant.image
            $cur = Join-Path $build "$i-$number.cur"
            [CursorEncoder]::ConvertScaled($png,$cur,[int]$variant.hotspot[0],[int]$variant.hotspot[1],0.65)
            $arguments += "/resource:$png,picture$i-$number"
            $arguments += "/resource:$cur,cursor$i-$number"
            $node = $theme.CreateElement('variant')
            $node.SetAttribute('index',"$i")
            $node.SetAttribute('number',"$number")
            $node.SetAttribute('x',[string]$variant.hotspot[0])
            $node.SetAttribute('y',[string]$variant.hotspot[1])
            [void]$themeRoot.ChildNodes[$i].AppendChild($node)
        }
    }
    $catalog.Save((Join-Path $build 'cats.xml'))
    $arguments += '/resource:'+(Join-Path $build 'cats.xml')+',cats'
}
$theme.Save((Join-Path $build 'theme.xml'))
$arguments += '/resource:'+(Join-Path $build 'theme.xml')+',theme'
$arguments += Join-Path $PSScriptRoot 'CursorEncoder.cs'
$arguments += $sourcePath
& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
# Reuse the main executable for restoration instead of duplicating bundled artwork.
$restoreSource = Join-Path $build 'RestoreLauncher.cs'
$restoreCode = @'
using System;
using System.IO;
using System.Diagnostics;
using System.Windows.Forms;
internal static class RestoreLauncher {
    [STAThread] static void Main(string[] args) {
        string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "喵喵就位.exe");
        if (args.Length == 1 && args[0] == "--verify") { Environment.Exit(File.Exists(exe) ? 0 : 1); return; }
        if (!File.Exists(exe)) { MessageBox.Show("请将恢复程序与喵喵就位.exe放在同一文件夹。", "喵喵就位"); return; }
        Process.Start(new ProcessStartInfo(exe, "--restore") { WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory, UseShellExecute = true });
    }
}
'@
[IO.File]::WriteAllText($restoreSource,$restoreCode,[Text.UTF8Encoding]::new($true))
& $compiler '/nologo' '/target:winexe' '/reference:System.Windows.Forms.dll' ('/win32manifest:'+(Join-Path $template 'app.manifest')) ('/out:'+(Join-Path $package '恢复原鼠标.exe')) $restoreSource
if ($LASTEXITCODE -ne 0) { throw '恢复入口编译失败。' }
$instructions = "喵喵就位`r`n`r`n解压后双击喵喵就位.exe。完成后在同窗试用所选指针。`r`n恢复时双击恢复原鼠标.exe。首次备份在 %LOCALAPPDATA%\\MiaoMiaoReady，重复安装持续保留。`r`n本包选择 $($entries.Count) 种指针。忙碌中为静态图。软件自绘指针由软件决定。`r`n本程序采用系统高分屏适配，跨不同缩放屏幕可重新运行。程序未签名，系统可能显示安全提示。`r`n"
[IO.File]::WriteAllText((Join-Path $package '使用说明.txt'),$instructions,[Text.UTF8Encoding]::new($true))
Compress-Archive -LiteralPath $package -DestinationPath (Join-Path $output '喵喵就位-安装包.zip')
Write-Output (Join-Path $output '喵喵就位-安装包.zip')
