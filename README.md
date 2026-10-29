# 黑屏守护

<img src="assets/app-icon.png" width="120" alt="黑屏守护图标">

Windows 10 / 11 便携工具：显示黑屏、隔离普通键鼠操作，输入密码后恢复桌面。原来的程序留在原桌面运行，本工具不会主动挂起它们。

[下载最新版](https://github.com/xnnb04664-afk/black-screen-guard/releases/latest) · [使用说明](使用说明.md) · [更新记录](CHANGELOG.md) · [审查与验证](AUDIT.md)

## 使用

1. 从 Releases 下载 `BlackScreenGuard-v1.0.1-win.zip`，解压全部文件。
2. 双击 `黑屏守护.exe`，保留同目录的 `黑屏守护.exe.config`。
3. 首次设置密码，点击“开始黑屏”。3 秒倒计时内可以取消或切回要继续运行的软件。
4. 黑屏时按任意键或单击，输入密码解开。

运行需要 .NET Framework 4.8，无需管理员权限。密码按当前 Windows 用户保存；更新应用不会重置原密码。

## 功能与限制

- 为显示器创建黑色窗口，原桌面继续运行；拦截常用切换、关闭、媒体及截图快捷键。
- 独立桌面隔离键鼠，密码验证使用 PBKDF2-SHA256，密码记录使用当前用户 DPAPI 保护。
- 可防止自动睡眠；启动进程和黑屏进程相互监测，异常退出后尝试恢复桌面。
- 不安装服务、不设置开机自启、不修改 Windows 安全策略。

这不是 Windows 安全锁。Ctrl+Alt+Del、Windows 安全界面、管理员操作、关机和强制重启仍可使用。第三方悬浮层可能保持可见，具体播放器或课程平台也可能因失焦或不可见而自行暂停，需要实际短测。

忘记密码时的系统恢复方法见[使用说明](使用说明.md#忘记密码或出现异常)。

## 构建与验证

在 Windows PowerShell 或 PowerShell 7 中执行：

```powershell
.\build.ps1                 # 正式版：dist/黑屏守护.exe
.\tests\verify-release.ps1  # 正式版结构、禁用测试入口、界面及图标检查
.\tests\verify.ps1          # 会短暂切换测试黑屏并自动恢复，期间请勿操作
.\package.ps1               # 打包已编译的正式版并生成 SHA-256
.\tests\verify-package.ps1  # 检查便携包内容及哈希
```

使用 Windows 自带的 .NET Framework 编译器，不依赖 NuGet。自测版本单独编译到 `.artifacts/test`；故障注入、快捷键注入和自动测试退出逻辑不进入正式版。验证使用临时密码文件，保留你的实际密码。

`assets/app.ico` 为 16–256 像素多尺寸图标；修改原图后可运行 `build-icon.ps1` 重新转换。
