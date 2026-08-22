# 构建与发布

## 本地构建

需要 Windows、.NET 10 SDK 和 Inno Setup 6.5+（推荐 7.x）。

- `./publish.ps1`：生成 `artifacts/single-file/FitBudsControl.exe`
- `./build-installer.ps1`：先生成单文件版，再生成 `artifacts/installer/FitBudsControl-Setup-<版本>.exe`

安装器默认选择“仅为我安装”，也允许用户改为“为所有用户安装”。当前用户安装会写入用户程序目录；所有用户安装会请求管理员权限并写入 Program Files。

简体中文 Inno Setup 语言文件在构建时按需下载；如果无法下载，会回退到 Inno Setup 自带的默认语言。

## GitHub Actions

`.github/workflows/autobuild.yml` 会在 push、pull request 和手动运行时构建 Windows x64 单文件版与安装包并上传为 Actions artifact。

推送 `v*` 标签（例如 `v1.0.48`）时，还会自动创建或更新同名 GitHub Release，并上传：

- `FitBudsControl.exe`
- `FitBudsControl-Setup-<版本>.exe`

## 应用设置说明

“应用”页面包含：

- 开机自动启动：写入当前登录用户的 Windows 启动项；即使应用是为所有用户安装，这个开关仍按用户分别生效
- 任务栏图标始终蓝色
- 耳机事件自动打开任务栏菜单，可分别选择连接、断开、降噪模式变化、音效模式变化和低电量

开发者选项默认隐藏。在“关于”页面连续点击版本号 7 次后，本次程序运行期间显示开发者选项；完全退出程序后需要重新解锁。
