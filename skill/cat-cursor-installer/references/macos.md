# macOS 路线

先确认 macOS 具体版本和用户是否已有指针管理工具。Windows CUR、注册表和 EXE 不能安装到 Mac。

可评估 Mousecape。它通过私有 CoreGraphics 接口提供运行时指针主题，使用 `.cape` 文件，通常支持多倍率图像及多种指针角色。使用前浏览维护者当前发布说明与问题记录，核对用户系统版本的兼容性。已出现 macOS 26 的兼容性问题，不能只依据 README 中旧版系统范围承诺可用。

主要来源如下，执行时重新读取所需内容。

- https://github.com/alexzielenski/Mousecape
- https://github.com/alexzielenski/Mousecape/releases
- https://github.com/alexzielenski/Mousecape/issues
- https://github.com/alexzielenski/Mousecape/blob/master/README.md

通过当前项目源码或官方示例核对 `.cape` 字段、类型 ID、热点单位和多倍率图像布局后生成主题。八种 Windows 类型在 Mac 上的角色映射按实际支持情况处理，提供对应说明。保留旧主题，通过工具的恢复默认或重新选择旧主题恢复。第三方工具安装由用户操作或按其明确授权执行，不默认关闭系统完整性保护或绕过系统安全设置。

兼容性已证实时，交付 `.cape`、原始透明素材、导入及恢复说明和 ZIP，说明依赖工具。用户需要“与 Windows 同样双击即自动完成”的原生 Mac 安装器时，需要单独实现和真机验证；当前 skill 的随附编译模板仅用于 Windows。

无法取得兼容方案时继续完成设计素材及配置，清楚标注 Mac 安装部分缺少已验证兼容工具，避免把素材 ZIP 描述成已可用的 Mac 安装器。
