# FitBuds Turbo 控制

FitBuds Turbo 控制是一个运行在 Windows 上的开源托盘工具，用来管理 EDIFIER FitBuds Turbo 耳机。它通过 Bluetooth RFCOMM 连接设备，可查看左右耳与充电盒电量、切换降噪和音效模式，并修改提示音、触控、均衡器和定时设置。

## 功能

- 托盘菜单快速查看状态和切换常用模式
- 自动重连、开机启动和低电量通知
- 设备名称、降噪轮换、触控映射、音效和四段均衡器设置
- 开发者选项中的 Bluetooth 协议通讯日志和数据提取工具
- 启动时从 GitHub Release 自动检查更新，可在“设置 > 应用”中关闭

## 系统要求

- Windows 10 1809（版本 17763）或更高版本
- 支持 Bluetooth RFCOMM 的蓝牙适配器
- 使用源码构建时需要 .NET 10 SDK；生成安装器还需要 Inno Setup 6.5+ 或 7.x

## 下载与运行

在 [Releases](https://github.com/langningchen/FitBudsControl/releases) 下载以下任一版本：

- `FitBudsControl-Portable-<版本>.zip`：解压到任意目录后运行 `FitBudsControl.exe`，portable 目录中的 DLL、配置文件和运行时文件需要保持在一起。
- `FitBudsControl-Setup-<版本>.exe`：运行安装器。安装器和 portable 包使用相同的多文件程序目录，不会把应用打包成单文件 EXE。

首次运行后，可在“设置 > 设备”填写耳机 Bluetooth 地址和 RFCOMM 通道。设置保存在 `%LOCALAPPDATA%\FitBudsControl\settings.json`。

## 从源码构建

PowerShell 中运行：

```powershell
./publish.ps1
```

脚本会生成 self-contained 的多文件 portable 目录和 zip：

```text
artifacts/portable/FitBudsControl/
artifacts/portable/FitBudsControl-Portable-<版本>.zip
```

生成 Inno Setup 安装器：

```powershell
./build-installer.ps1
```

输出为 `artifacts/installer/FitBudsControl-Setup-<版本>.exe`。更完整的构建、发布和 GitHub Actions 说明见 [BUILDING.md](BUILDING.md)。

## 发布流程

推送形如 `v1.0.49` 的 tag 会触发 GitHub Actions，在 Windows runner 上构建 portable zip 和多文件安装器，并将两者上传到同名 GitHub Release。工作流也支持 pull request 和手动运行；普通构建不会创建 Release。

## 许可证

本项目使用 [GNU Affero General Public License v3.0](LICENSE)。第三方组件和许可证信息见 [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt)。
