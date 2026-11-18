# Windows 生成配置

路径相对配置文件所在目录解析。`cursors` 数组仅填写用户选中的类型，1 到 8 项。`hotspot` 是对应 PNG 原始像素坐标，构建前查看图像后填写。图片要求 RGBA 透明 PNG，文件建议放在本次任务目录。

```json
{
  "runner": "assets/running-right.png",
  "brand": "assets/sitting-avatar.png",
  "cursors": [
    {"id":"normal","image":"assets/normal.png","hotspot":[23,30]},
    {"id":"text","image":"assets/text.png","hotspot":[154,160]}
  ]
}
```

调用方式如下，替换为实际绝对路径。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<skill>/scripts/build-windows.ps1" -ConfigPath "<task>/theme.json" -OutputDirectory "<task>/package"
```

脚本把透明区域裁掉，保持纵横比缩小至带一像素留边的正方形，并按同一变换调整热点。会验证热点处于非透明轮廓中。生成源图像和 CUR 独立保存以便检查，不用更改素材原图。图像透明度不合格应在生成阶段修正。

输出 `喵喵就位.exe`、`恢复原鼠标.exe`、使用说明及同级 ZIP。所有素材嵌入 EXE，用户只需解压运行。安装器根据资源摘要使用不同素材目录，第一次备份位于当前用户 LocalAppData 的 MiaoMiaoReady 内。

自检示例可使用以下命令。预览比例变量仅用于测试窗口布局。

```powershell
$env:MIAOMIAO_PREVIEW_SCALE='1.25'
& '<output>/喵喵就位.exe' --self-test '<task>/preview-125'
Remove-Item Env:MIAOMIAO_PREVIEW_SCALE
```

运行 EXE 时 PowerShell 可能立即返回，使用 Start-Process -PassThru -Wait 或等价进程等待方式，核对退出码及 passed.txt。100%、125%、150% 适配应实际查看生成截图。程序是系统 DPI 感知，跨不同缩放显示器时重新启动以适配。
