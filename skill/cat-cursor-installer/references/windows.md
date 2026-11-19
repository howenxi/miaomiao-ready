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

每个 cursor 可选 contentScale（0.3～1，默认1）。文字输入建议先用0.65，热点采用同一缩放变换，透明画布大小保持不变。

构建时另嵌入theme.xml，保存选中类型的原图热点，并把CursorEncoder编译进EXE供运行时调大小。请通过build-windows.ps1编译，单独只编译Program.cs不再具备完整资源和依赖。

2026-11-19 当前尺寸更新：25/35/45采用64px单帧CUR和ConvertPixels，避免32px资源截断。应用后用SetSystemCursor设置拥有的原生句柄（该API成功后接管句柄）；预览用同一原生资源DrawIconEx绘制。Windows重新载入主题会回到系统默认缩放，可重新运行安装器恢复保存尺寸。

2026-11-19 后续修正（优先于上面旧尺寸说明）：预览改用CUR解码后1:1居中。Win11创建与系统复制阶段均使用SetThreadCursorCreationScaling(1)，阻止DPI二次缩放；finally恢复线程原策略。Win10缺少接口时沿用旧路径，未经真机验证。三档预览验证包括试用线程指针的像素/热点和实际绘制居中；应用验证核对共享句柄的画布、热点与轮廓，边缘允许2px误差，不把边缘处理误判成整体缩放，也不宣称屏幕最终合成像素完全一致。自检不会移动鼠标位置；检测自己线程的GetCursor，避免把其他前台应用的GetCursorInfo当成本程序的试用指针。

官方接口说明：https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setthreadcursorcreationscaling
